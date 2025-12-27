namespace Zyknow.Abp.Lucene.Descriptors;

/// <summary>
/// 默认查询过滤上下文（用于在 ConfigureLucene 中集中配置强制过滤条件）。
/// </summary>
public sealed record LuceneQueryFilterContext<TInput>(
    string EntityName,
    EntitySearchDescriptor Descriptor,
    TInput Input
);



