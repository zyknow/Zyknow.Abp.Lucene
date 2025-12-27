namespace Zyknow.Abp.Lucene.Descriptors;

public class EntitySearchDescriptor(Type type, string? indexName)
{
    public Type EntityType { get; } = type;
    public string IndexName { get; } = indexName ?? type.Name;
    public List<FieldDescriptor> Fields { get; } = [];
    public string IdFieldName { get; set; } = "Id";

    /// <summary>
    /// 默认查询过滤（强制条件）：每次查询都会以 MUST 方式叠加。
    /// </summary>
    public List<ILuceneQueryFilterRegistration> DefaultQueryFilters { get; } = [];

    /// <summary>
    /// 索引剔除规则：当返回 true 时，实体将不会被写入索引，并会尽可能从索引中删除。
    /// </summary>
    public List<ILuceneIndexExclusionRegistration> IndexExclusions { get; } = [];
}