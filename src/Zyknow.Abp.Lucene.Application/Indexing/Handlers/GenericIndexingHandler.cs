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

        var id = GetIdString(entity);
        if (id != null)
        {
            collector.Upsert(entity, id);
            logger.LogDebug("Queued upsert: {EntityType} #{Id}", typeof(T).Name, id);
            RegisterOnce();
        }

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
            return g.Id.ToString();
        }

        var prop = entity.GetType().GetProperty("Id");
        var val = prop?.GetValue(entity);
        return val?.ToString();
    }
}