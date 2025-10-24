using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.MultiTenancy;
using Zyknow.Abp.Lucene.Descriptors;
using Zyknow.Abp.Lucene.Options;
using System.Collections.Concurrent;

namespace Zyknow.Abp.Lucene.Indexing;

public class LuceneIndexManager(IOptions<LuceneOptions> options, ICurrentTenant currentTenant, ILogger<LuceneIndexManager> logger)
{
    private readonly LuceneOptions _options = options.Value;
    // 新增：按索引路径维护写入锁，串行化同一索引的写操作，避免 IndexWriter 并发冲突
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _indexLocks = new(StringComparer.OrdinalIgnoreCase);

    public Task IndexAsync<T>(T entity)
    {
        var descriptor = GetDescriptor(typeof(T));
        logger.LogInformation("Indexing single entity: {EntityType}", typeof(T).Name);
        Write(descriptor, writer =>
        {
            var doc = LuceneDocumentFactory.CreateDocument(entity!, descriptor);
            var id = doc.Get(descriptor.IdFieldName);
            writer.UpdateDocument(new Term(descriptor.IdFieldName, id), doc);
            logger.LogDebug("IndexWriter UpdateDocument: {Index} #{Id}", descriptor.IndexName, id);
        });
        return Task.CompletedTask;
    }

    public Task IndexRangeAsync<T>(IEnumerable<T> entities, bool replace = false)
    {
        var descriptor = GetDescriptor(typeof(T));
        var list = entities as ICollection<T> ?? entities.ToList();
        logger.LogInformation("Indexing {Count} entities into {Index} (replace={Replace})", list.Count, descriptor.IndexName, replace);
        Write(descriptor, writer =>
        {
            if (replace)
            {
                writer.DeleteAll();
                logger.LogDebug("IndexWriter DeleteAll: {Index}", descriptor.IndexName);
            }

            foreach (var entity in list)
            {
                var doc = LuceneDocumentFactory.CreateDocument(entity!, descriptor);
                var id = doc.Get(descriptor.IdFieldName);
                writer.UpdateDocument(new Term(descriptor.IdFieldName, id), doc);
            }
            logger.LogDebug("IndexWriter UpdateDocument batch done: {Index} x{Count}", descriptor.IndexName, list.Count);
        });
        return Task.CompletedTask;
    }

    public Task IndexRangeDocumentsAsync(EntitySearchDescriptor descriptor, IEnumerable<Document> documents,
        bool replace = false)
    {
        var docs = documents as ICollection<Document> ?? documents.ToList();
        logger.LogInformation("Indexing {Count} raw documents into {Index} (replace={Replace})", docs.Count, descriptor.IndexName, replace);
        Write(descriptor, writer =>
        {
            if (replace)
            {
                writer.DeleteAll();
                logger.LogDebug("IndexWriter DeleteAll: {Index}", descriptor.IndexName);
            }

            foreach (var doc in docs)
            {
                var id = doc.Get(descriptor.IdFieldName);
                writer.UpdateDocument(new Term(descriptor.IdFieldName, id), doc);
            }
            logger.LogDebug("IndexWriter UpdateDocument batch done: {Index} x{Count}", descriptor.IndexName, docs.Count);
        });
        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(object id)
    {
        var descriptor = GetDescriptor(typeof(T));
        logger.LogInformation("Deleting single document from {Index}: #{Id}", descriptor.IndexName, id);
        Write(descriptor, writer =>
        {
            writer.DeleteDocuments(new Term(descriptor.IdFieldName, id.ToString()));
            logger.LogDebug("IndexWriter DeleteDocuments: {Index} #{Id}", descriptor.IndexName, id);
        });
        return Task.CompletedTask;
    }

    public Task RebuildAsync(Type? entityType = null)
    {
        if (entityType == null)
        {
            logger.LogInformation("Rebuild: delete all indexes for all configured entities ({Count})", _options.Descriptors.Count);
            foreach (var d in _options.Descriptors.Values)
            {
                Write(d, writer =>
                {
                    writer.DeleteAll();
                    logger.LogDebug("IndexWriter DeleteAll: {Index}", d.IndexName);
                });
            }
        }
        else
        {
            var d = GetDescriptor(entityType);
            logger.LogInformation("Rebuild: delete all documents for {Index}", d.IndexName);
            Write(d, writer =>
            {
                writer.DeleteAll();
                logger.LogDebug("IndexWriter DeleteAll: {Index}", d.IndexName);
            });
        }

        return Task.CompletedTask;
    }

    public Task DeleteRangeAsync<T>(IEnumerable<object> ids)
    {
        var descriptor = GetDescriptor(typeof(T));
        var list = ids as ICollection<object> ?? ids.ToList();
        logger.LogInformation("Deleting {Count} documents from {Index}", list.Count, descriptor.IndexName);
        Write(descriptor, writer =>
        {
            foreach (var id in list)
            {
                writer.DeleteDocuments(new Term(descriptor.IdFieldName, id.ToString()));
            }
            logger.LogDebug("IndexWriter Delete batch done: {Index} x{Count}", descriptor.IndexName, list.Count);
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 按任意字段和值集合删除索引中的文档。
    /// </summary>
    public Task DeleteByFieldAsync(EntitySearchDescriptor descriptor, string fieldName, IEnumerable<object> values)
    {
        var vals = values?.Where(v => v != null).Select(v => v.ToString()!).ToList() ?? new List<string>();
        logger.LogInformation("DeleteByField: index={Index}, field={Field}, valuesCount={Count}", descriptor.IndexName, fieldName, vals.Count);
        Write(descriptor, writer =>
        {
            var terms = vals.Select(v => new Term(fieldName, v)).ToArray();
            if (terms.Length > 0)
            {
                writer.DeleteDocuments(terms);
                logger.LogDebug("IndexWriter DeleteDocuments by field: {Index} {Field} x{Count}", descriptor.IndexName, fieldName, terms.Length);
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 按任意字段和值集合删除（泛型实体版本）。
    /// </summary>
    public Task DeleteByFieldAsync<T>(string fieldName, IEnumerable<object> values)
    {
        var descriptor = GetDescriptor(typeof(T));
        return DeleteByFieldAsync(descriptor, fieldName, values);
    }

    /// <summary>
    /// 按任意字段和单个值删除（泛型实体版本）。
    /// </summary>
    public Task DeleteByFieldAsync<T>(string fieldName, object value)
        => DeleteByFieldAsync<T>(fieldName, new[] { value });

    protected virtual EntitySearchDescriptor GetDescriptor(Type type)
    {
        if (!_options.Descriptors.TryGetValue(type, out var descriptor))
        {
            throw new BusinessException("Lucene:EntityNotConfigured").WithData("Entity", type.FullName);
        }

        return descriptor;
    }

    protected virtual void Write(EntitySearchDescriptor descriptor, Action<IndexWriter> action)
    {
        var indexPath = GetIndexPath(descriptor.IndexName);
        logger.LogDebug("Open IndexWriter: {Index} path={Path}", descriptor.IndexName, indexPath);

        // 串行化同一索引的写入，避免并发 IndexWriter 导致的 write.lock 争用
        var gate = _indexLocks.GetOrAdd(indexPath, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            using var dir = _options.DirectoryFactory(indexPath);
            var analyzer = _options.AnalyzerFactory();
            var config = new IndexWriterConfig(LuceneVersion.LUCENE_48, analyzer);
            using var writer = new IndexWriter(dir, config);
            action(writer);
            writer.Commit();
            logger.LogInformation("Committed changes to index: {Index}", descriptor.IndexName);
        }
        finally
        {
            gate.Release();
        }
    }

    public virtual string GetIndexPath(string indexName)
    {
        var root = _options.IndexRootPath;
        if (_options.PerTenantIndex && currentTenant.Id.HasValue)
        {
            return Path.Combine(root, currentTenant.Id.Value.ToString(), indexName);
        }

        return Path.Combine(root, indexName);
    }
}
