using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Entities.Events;
using Volo.Abp.EventBus;
using Volo.Abp.Uow;
using Zyknow.Abp.Lucene.Options;
using Zyknow.Abp.Lucene.Services;

// changed from .Local to correct namespace

namespace Zyknow.Abp.Lucene.Indexing.Handlers;

/// <summary>
/// 通用的实体事件处理器：在同一 UnitOfWork 中聚合创建/更新/删除事件，
/// 并在事务提交后一次性批量写入 Lucene 索引。
/// 仅对已通过 Fluent DSL 注册的实体类型生效。
/// </summary>
public class GenericIndexingHandler<T>(
    IIndexingCollector collector,
    IUnitOfWorkManager uow,
    LuceneIndexManager indexer,
    IOptions<LuceneOptions> options,
    ILogger<GenericIndexingHandler<T>> logger)
    :
        ILocalEventHandler<EntityCreatedEventData<T>>,
        ILocalEventHandler<EntityUpdatedEventData<T>>,
        ILocalEventHandler<EntityDeletedEventData<T>>,
        ITransientDependency
{
    public Task HandleEventAsync(EntityCreatedEventData<T> eventData)
    {
        logger.LogDebug("EntityCreated received: {EntityType}", typeof(T).Name);
        return HandleUpsert(eventData.Entity);
    }

    public Task HandleEventAsync(EntityDeletedEventData<T> eventData)
    {
        if (!IsConfigured())
        {
            logger.LogTrace("Skip indexing (not configured or disabled): {EntityType}", typeof(T).Name);
            return Task.CompletedTask;
        }

        var id = GetIdString(eventData.Entity);
        if (id != null)
        {
            collector.Delete<T>(id);
            logger.LogDebug("Queued delete: {EntityType} #{Id}", typeof(T).Name, id);
            RegisterOnce();
        }

        return Task.CompletedTask;
    }

    public Task HandleEventAsync(EntityUpdatedEventData<T> eventData)
    {
        logger.LogDebug("EntityUpdated received: {EntityType}", typeof(T).Name);
        return HandleUpsert(eventData.Entity);
    }

    private Task HandleUpsert(T entity)
    {
        if (!IsConfigured())
        {
            logger.LogTrace("Skip indexing (not configured or disabled): {EntityType}", typeof(T).Name);
            return Task.CompletedTask;
        }

        // 若此时实体 Id 尚未生成（例如 Guid.Empty 或空），为收集器使用一个临时唯一键，避免同一 UoW 内覆盖。
        var id = GetIdString(entity);
        var key = !string.IsNullOrWhiteSpace(id) ? id : $"__tmp__{Guid.NewGuid():N}";

        collector.Upsert(entity, key);
        logger.LogDebug("Queued upsert: {EntityType} key={Key}", typeof(T).Name, key);
        RegisterOnce();
        return Task.CompletedTask;
    }

    private bool IsConfigured()
    {
        var opts = options.Value;
        if (!opts.EnableAutoIndexingEvents)
        {
            return false;
        }

        return opts.Descriptors.ContainsKey(typeof(T));
    }

    private void RegisterOnce()
    {
        var current = uow.Current;
        if (current != null)
        {
            logger.LogDebug("Register OnCompleted for {EntityType}", typeof(T).Name);
            collector.RegisterOnCompleted(current, indexer);
        }
        else
        {
            logger.LogDebug("No active UoW, process immediately for {EntityType}", typeof(T).Name);
            // 无 UoW 情况：立即执行批处理
            collector.ProcessImmediatelyAsync(indexer).GetAwaiter().GetResult();
        }
    }

    private static string? GetIdString(T entity)
    {
        if (entity == null)
        {
            return null;
        }

        if (entity is IEntity<Guid> g)
        {
            return g.Id == Guid.Empty ? null : g.Id.ToString();
        }

        var prop = entity.GetType().GetProperty("Id");
        var val = prop?.GetValue(entity);
        var s = val?.ToString();
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        // 若是 Guid 字符串且为空 Guid，也视为未就绪
        if (Guid.TryParse(s, out var gv) && gv == Guid.Empty)
        {
            return null;
        }

        return s;
    }
}