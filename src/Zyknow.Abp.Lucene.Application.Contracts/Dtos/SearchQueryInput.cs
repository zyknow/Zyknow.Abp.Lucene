using Volo.Abp.Application.Dtos;
using Zyknow.Abp.Lucene.Options;

namespace Zyknow.Abp.Lucene.Dtos;

/// <summary>
/// 搜索查询输入参数。
/// - 支持 Lucene 查询语法（例如：短语查询、字段限定、多字段组合）。
/// - 与 <see cref="LuceneOptions.LuceneQuery"/> 的全局查询选项协同工作（AND/OR、多字段模式、模糊/前缀开关等）。
/// </summary>
public class SearchQueryInput : PagedAndSortedResultRequestDto
{
    /// <summary>
    /// 原始查询字符串。支持 Lucene 的解析器语法（例如：
    /// <code>title:Lucene AND author:Erik</code>、短语查询 <code>"Pro .NET"</code>、前缀 <code>Luc*</code> 等）。
    /// 若未指定字段，默认使用实体在 Fluent 配置中声明的所有参与索引的字段。
    /// </summary>
    public string Query { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用模糊查询（Fuzzy）。
    /// - 模糊匹配将依据全局选项 <c>FuzzyMaxEdits</c>（Levenshtein 编辑距离）进行召回。
    /// - 注意：模糊查询会降低精确度并可能影响性能，请按需使用。
    /// </summary>
    public bool Fuzzy { get; set; } = false;

    /// <summary>
    /// 是否启用前缀匹配（Prefix）。
    /// - 适用于前缀词条召回，若在索引侧启用了 <c>Autocomplete</c>（NGram/EdgeNGram），可显著提升效果。
    /// - 未启用自动补全索引时，前缀匹配仅对已分词生成的前缀有效，召回可能受限。
    /// </summary>
    public bool Prefix { get; set; } = true;

    /// <summary>
    /// 是否请求高亮片段（Highlights）。
    /// - 启用后，服务端会为命中文档的“已存储（Store=true）”字段尝试生成高亮片段，并通过 <see cref="SearchHitDto.Highlights"/> 返回。
    /// - 片段默认使用 <em> 标签包裹；可读性与准确度受分词与字段内容影响。
    /// - 建议：为需要高亮的字段开启 <c>Store()</c>；如需更佳效果，可在索引侧考虑启用词向量（StoreTermVectors）。
    /// </summary>
    public bool Highlight { get; set; } = false;
}