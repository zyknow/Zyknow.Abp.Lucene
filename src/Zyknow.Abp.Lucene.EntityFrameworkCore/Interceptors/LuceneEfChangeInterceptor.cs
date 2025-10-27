using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.Uow;
using Zyknow.Abp.Lucene.Indexing;
using Zyknow.Abp.Lucene.Options;

namespace Zyknow.Abp.Lucene.Interceptors;

/// <summary>
/// EF Core SaveChanges 拦截器：在 SaveChanges 过程中收集实体的增删改，
/// 并聚合到当前 UoW，最终在事务提交（OnCompleted）后一次性刷新到 Lucene。
/// - 仅对在 <see cref="LuceneOptions"/> 中注册的实体生效。
/// - 支持软删除（<see cref="ISoftDelete"/>）识别：当 <c>IsDeleted</c> 从 false->true 时按删除处理。
/// - AutoSave/多次 SaveChanges：变更会被累积，最终只提交一次。
/// </summary>
public class LuceneEfChangeInterceptor : SaveChangesInterceptor
{
    private readonly ILogger<LuceneEfChangeInterceptor> _logger;

    /// <summary>
    /// EF Core SaveChanges 拦截器：在 SaveChanges 过程中收集实体的增删改，
    /// 并聚合到当前 UoW，最终在事务提交（OnCompleted）后一次性刷新到 Lucene。
    /// - 仅对在 <see cref="LuceneOptions"/> 中注册的实体生效。
    /// - 支持软删除（<see cref="ISoftDelete"/>）识别：当 <c>IsDeleted</c> 从 false->true 时按删除处理。
    /// - AutoSave/多次 SaveChanges：变更会被累积，最终只提交一次。
    /// </summary>
    public LuceneEfChangeInterceptor(ILogger<LuceneEfChangeInterceptor> logger)
    {
        _logger = logger;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        TryCollect(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        TryCollect(eventData.Context);
        return new ValueTask<InterceptionResult<int>>(result);
    }

    private void TryCollect(DbContext? db)
    {
        if (db == null)
        {
            return;
        }

        // 读取配置开关
        var options = db.GetService<IOptions<LuceneOptions>>().Value;
        if (!options.EnableAutoIndexingEvents)
        {
            return;
        }

        if (options.Descriptors.Count == 0)
        {
            return;
        }

        // 解析依赖：通过 DbContext 持有的 ServiceProvider 获取 Scoped 依赖
        var uowManager = db.GetService<IUnitOfWorkManager>();
        var indexer = db.GetService<LuceneIndexManager>();

        var entries = db.ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified || e.State == EntityState.Deleted)
            .ToList();

        if (entries.Count == 0)
        {
            return;
        }

        var anyCollected = false;

        // 取当前 UoW 缓冲（或本地临时缓冲）
        var uow = uowManager.Current;
        var buffer = uow != null
            ? (ChangeBuffer)(uow.Items.TryGetValue("Lucene:EfChanges", out var v) && v is ChangeBuffer b ? b : (uow.Items["Lucene:EfChanges"] = new ChangeBuffer()))
            : new ChangeBuffer();

        foreach (var entry in entries)
        {
            var entityType = entry.Metadata.ClrType; // 去代理类型
            if (!options.Descriptors.TryGetValue(entityType, out var descriptor))
            {
                continue; // 非索引实体
            }

            // 软删除识别：Modified 且 IsDeleted 从 false->true
            if (entry.State == EntityState.Modified && IsSoftDeleteToDeleted(entry))
            {
                if (TryGetId(entry, descriptor.IdFieldName, out var delId))
                {
                    buffer.Delete(entityType, delId!);
                    _logger.LogDebug("[EF] queued soft-delete: {Entity} #{Id}", entityType.Name, delId);
                    anyCollected = true;
                }
                continue;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                case EntityState.Modified:
                {
                    var key = TryGetId(entry, descriptor.IdFieldName, out var id)
                        ? id!
                        : $"__tmp__{Guid.NewGuid():N}"; // 主键未生成（如数据库生成），使用临时键做去重

                    buffer.Upsert(entityType, key, entry.Entity);
                    _logger.LogDebug("[EF] queued upsert: {Entity} key={Key}", entityType.Name, key);
                    anyCollected = true;
                    break;
                }
                case EntityState.Deleted:
                {
                    if (TryGetId(entry, descriptor.IdFieldName, out var delId))
                    {
                        buffer.Delete(entityType, delId!);
                        _logger.LogDebug("[EF] queued delete: {Entity} #{Id}", entityType.Name, delId);
                        anyCollected = true;
                    }
                    break;
                }
            }
        }

        if (!anyCollected)
        {
            return;
        }

        // 挂载一次 UoW Completed 回调（若存在 UoW）；无 UoW 则立即执行
        if (uow != null)
        {
            if (!uow.Items.ContainsKey("Lucene:EfChanges:Registered"))
            {
                uow.Items["Lucene:EfChanges:Registered"] = true;
                uow.OnCompleted(async () =>
                {
                    await FlushAsync(indexer, buffer);
                    buffer.Clear();
                });
            }
        }
        else
        {
            FlushAsync(indexer, buffer).GetAwaiter().GetResult();
            buffer.Clear();
        }
    }

    private static bool IsSoftDeleteToDeleted(EntityEntry entry)
    {
        if (entry.Entity is not ISoftDelete)
        {
            return false;
        }

        var prop = entry.Properties.FirstOrDefault(p => string.Equals(p.Metadata.Name, nameof(ISoftDelete.IsDeleted), StringComparison.Ordinal));
        if (prop == null)
        {
            return false;
        }

        if (!prop.IsModified)
        {
            return false;
        }

        try
        {
            var original = (bool?)prop.OriginalValue ?? false;
            var current = (bool?)prop.CurrentValue ?? false;
            return original == false && current == true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetId(EntityEntry entry, string idFieldName, [NotNullWhen(true)] out string? id)
    {
        id = null;

        // 优先按描述的 Id 字段名读取
        var idProp = entry.Properties.FirstOrDefault(p => string.Equals(p.Metadata.Name, idFieldName, StringComparison.Ordinal));
        if (idProp != null)
        {
            var v = idProp.CurrentValue ?? idProp.OriginalValue;
            var s = v?.ToString();
            if (!string.IsNullOrWhiteSpace(s) && !IsEmptyGuidString(s))
            {
                id = s;
                return true;
            }
        }

        // 回退：从主键元数据读取（兼容自定义主键名/复合主键，拼接）
        var keys = entry.Properties.Where(p => p.Metadata.IsPrimaryKey()).ToList();
        if (keys.Count > 0)
        {
            var parts = new List<string>(keys.Count);
            foreach (var k in keys)
            {
                var v = k.CurrentValue ?? k.OriginalValue;
                var s = v?.ToString();
                if (string.IsNullOrWhiteSpace(s))
                {
                    parts.Clear();
                    break;
                }
                parts.Add(s);
            }
            if (parts.Count > 0)
            {
                id = parts.Count == 1 ? parts[0] : string.Join("|", parts);
                if (!string.IsNullOrWhiteSpace(id) && !IsEmptyGuidString(id))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsEmptyGuidString(string s)
    {
        return Guid.TryParse(s, out var g) && g == Guid.Empty;
    }

    private static async Task FlushAsync(LuceneIndexManager indexer, ChangeBuffer buffer)
    {
        // 原子快照并清空缓冲，避免与并发写入竞争
        var (upserts, deletes) = buffer.SnapshotAndClear();

        // 删除优先：从待写入的 upserts 中移除已标记删除的键
        foreach (var (type, ids) in deletes)
        {
            if (upserts.TryGetValue(type, out var map))
            {
                foreach (var id in ids)
                {
                    map.Remove(id);
                }
            }
        }

        foreach (var (type, map) in upserts)
        {
            if (map.Count == 0) continue;
            var mi = typeof(LuceneIndexManager).GetMethod(nameof(LuceneIndexManager.IndexRangeAsync))!;
            var g = mi.MakeGenericMethod(type);
            var listType = typeof(List<>).MakeGenericType(type);
            var list = (IList)Activator.CreateInstance(listType)!;
            foreach (var obj in map.Values) list.Add(obj);
            await (Task)g.Invoke(indexer, new object[] { list, false })!;
        }

        foreach (var (type, ids) in deletes)
        {
            if (ids.Count == 0) continue;
            var mi = typeof(LuceneIndexManager).GetMethod(nameof(LuceneIndexManager.DeleteRangeAsync))!;
            var g = mi.MakeGenericMethod(type);
            await (Task)g.Invoke(indexer, new object[] { ids.Cast<object>().ToList() })!;
        }
    }

    private class ChangeBuffer
    {
        private readonly Lock _sync = new();
        private readonly Dictionary<Type, Dictionary<string, object>> _upserts = new();
        private readonly Dictionary<Type, HashSet<string>> _deletes = new();

        public void Upsert(Type type, string id, object entity)
        {
            lock (_sync)
            {
                if (!_upserts.TryGetValue(type, out var map))
                {
                    map = new Dictionary<string, object>();
                    _upserts[type] = map;
                }
                _upserts[type][id] = entity;
            }
        }

        public void Delete(Type type, string id)
        {
            lock (_sync)
            {
                if (!_deletes.TryGetValue(type, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    _deletes[type] = set;
                }
                _deletes[type].Add(id);
            }
        }

        public (Dictionary<Type, Dictionary<string, object>> Upserts, Dictionary<Type, HashSet<string>> Deletes) SnapshotAndClear()
        {
            lock (_sync)
            {
                var upserts = _upserts.ToDictionary(kv => kv.Key, kv => new Dictionary<string, object>(kv.Value));
                var deletes = _deletes.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value));
                _upserts.Clear();
                _deletes.Clear();
                return (upserts, deletes);
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _upserts.Clear();
                _deletes.Clear();
            }
        }
    }
}


