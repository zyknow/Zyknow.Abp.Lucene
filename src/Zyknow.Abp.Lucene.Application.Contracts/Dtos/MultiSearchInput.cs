namespace Zyknow.Abp.Lucene.Dtos;

/// <summary>
/// 多实体搜索输入。
/// </summary>
public class MultiSearchInput : SearchQueryInput
{
    /// <summary>
    /// 参与聚合的实体索引名集合。
    /// </summary>
    public List<string> Entities { get; set; } = new();
}