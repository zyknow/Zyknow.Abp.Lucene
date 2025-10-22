using Volo.Abp.Uow;
using Zyknow.Abp.Lucene.Services;

namespace Zyknow.Abp.Lucene.Indexing;

/// <summary>
/// 在单个作用域/UnitOfWork 内聚合需要写入 Lucene 的实体变更，
/// 并在事务提交后一次性批处理执行，降低 IndexWriter 打开/提交频次。
/// </summary>
public interface IIndexingCollector
{
    IReadOnlyDictionary<Type, Dictionary<string, object>> Upserts { get; }
    IReadOnlyDictionary<Type, HashSet<string>> Deletes { get; }
    void Upsert<T>(T entity, string id);
    void Delete<T>(string id);

    /// <summary>
    /// 确保仅注册一次提交后的批处理回调。
    /// </summary>
    void RegisterOnCompleted(IUnitOfWork uow, LuceneIndexManager indexer);

    Task ProcessImmediatelyAsync(LuceneIndexManager indexer);
}