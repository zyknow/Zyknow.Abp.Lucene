namespace Zyknow.Abp.Lucene.Descriptors;

/// <summary>
/// 索引剔除规则：当返回 true 时，实体将不会被写入索引，并会尽可能从索引中删除。
/// </summary>
public interface ILuceneIndexExclusionRegistration
{
    bool ShouldExclude(IServiceProvider serviceProvider, object entity);
}

internal sealed class DelegateLuceneIndexExclusionRegistration<TEntity>(
    Func<IServiceProvider, TEntity, bool> predicate)
    : ILuceneIndexExclusionRegistration
{
    public bool ShouldExclude(IServiceProvider serviceProvider, object entity)
    {
        if (entity is not TEntity typed)
        {
            return false;
        }

        return predicate(serviceProvider, typed);
    }
}



