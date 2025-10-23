using System.Collections;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Uow;
using Zyknow.Abp.Lucene.Services;

namespace Zyknow.Abp.Lucene.Indexing;

public class IndexingCollector(ILogger<IndexingCollector> logger) : IIndexingCollector, IScopedDependency
{
    private readonly Dictionary<Type, HashSet<string>> _deletes = new();
    private readonly Dictionary<Type, Dictionary<string, object>> _upserts = new();
    private bool _registered;

    public IReadOnlyDictionary<Type, Dictionary<string, object>> Upserts => _upserts;
    public IReadOnlyDictionary<Type, HashSet<string>> Deletes => _deletes;

    public void Upsert<T>(T entity, string id)
    {
        var type = typeof(T);
        if (!_upserts.TryGetValue(type, out var map))
        {
            map = new Dictionary<string, object>();
            _upserts[type] = map;
        }

        map[id] = entity!;
        logger.LogDebug("IndexingCollector queued upsert: {EntityType} #{Id}", type.Name, id);
    }

    public void Delete<T>(string id)
    {
        var type = typeof(T);
        if (!_deletes.TryGetValue(type, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            _deletes[type] = set;
        }

        set.Add(id);
        logger.LogDebug("IndexingCollector queued delete: {EntityType} #{Id}", type.Name, id);
    }

    public void RegisterOnCompleted(IUnitOfWork uow, LuceneIndexManager indexer)
    {
        if (_registered)
        {
            logger.LogTrace("IndexingCollector OnCompleted already registered, skipping.");
            return;
        }

        _registered = true;
        logger.LogDebug("IndexingCollector registering OnCompleted for batched flush.");

        uow.OnCompleted(async () =>
        {
            logger.LogInformation("IndexingCollector UoW completed. Flushing queued changes to Lucene...");
            await FlushAsync(indexer);
            Reset();
        });
    }

    public Task ProcessImmediatelyAsync(LuceneIndexManager indexer)
    {
        logger.LogInformation("IndexingCollector processing immediately without UoW.");
        return FlushAsync(indexer);
    }

    private async Task FlushAsync(LuceneIndexManager indexer)
    {
        // 统计
        var totalUpsertDocs = _upserts.Sum(kv => kv.Value.Count);
        var totalDeleteIds = _deletes.Sum(kv => kv.Value.Count);
        logger.LogInformation("IndexingCollector flush start: upserts={UpsertsTypes}/{UpsertDocs}, deletes={DeleteTypes}/{DeleteDocs}",
            _upserts.Count, totalUpsertDocs, _deletes.Count, totalDeleteIds);

        // 删除优先：避免同一事务内先 upsert 后删除导致最终仍被索引
        foreach (var (type, ids) in _deletes)
        {
            // 从 upserts 中移除将被删除的主键
            if (_upserts.TryGetValue(type, out var map))
            {
                foreach (var id in ids)
                {
                    map.Remove(id);
                }
            }
        }

        // 批量 upsert
        foreach (var (type, map) in _upserts)
        {
            var count = map.Count;
            if (count == 0)
            {
                continue;
            }

            logger.LogDebug("Flushing upserts: {EntityType} x{Count}", type.Name, count);

            var indexRange = typeof(LuceneIndexManager).GetMethod(nameof(LuceneIndexManager.IndexRangeAsync))!;
            var generic = indexRange.MakeGenericMethod(type);

            // 构造 List<T> 参数
            var listType = typeof(List<>).MakeGenericType(type);
            var list = Activator.CreateInstance(listType)!;
            var ilist = (IList)list;
            foreach (var obj in map.Values)
            {
                ilist.Add(obj);
            }

            // replace:false 表示增量更新
            await (Task)generic.Invoke(indexer, [list, false])!;
        }

        // 批量删除
        foreach (var (type, ids) in _deletes)
        {
            var count = ids.Count;
            if (count == 0)
            {
                continue;
            }

            logger.LogDebug("Flushing deletes: {EntityType} x{Count}", type.Name, count);

            var deleteRange = typeof(LuceneIndexManager).GetMethod(nameof(LuceneIndexManager.DeleteRangeAsync))!;
            var gdeleteRange = deleteRange.MakeGenericMethod(type);
            // 构造 List<object> 参数
            var list = new List<object>(ids);
            await (Task)gdeleteRange.Invoke(indexer, [list])!;
        }

        logger.LogInformation("IndexingCollector flush completed: upserts={UpsertDocs}, deletes={DeleteDocs}", totalUpsertDocs, totalDeleteIds);
    }

    private void Reset()
    {
        _upserts.Clear();
        _deletes.Clear();
        _registered = false;
        logger.LogTrace("IndexingCollector state reset.");
    }
}