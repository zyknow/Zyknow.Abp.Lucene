using Lucene.Net.Index;
using Lucene.Net.Search;

namespace Zyknow.Abp.Lucene.Filtering;

/// <summary>
/// 常用 Lucene Query 构建助手（用于 ConfigureLucene/ForceFilter 场景）。
/// </summary>
public static class LuceneQueries
{
    /// <summary>
    /// 永不匹配的 Query（兼容 Lucene.Net 4.8：没有 MatchNoDocsQuery 时使用一个不可能命中的 TermQuery）。
    /// </summary>
    public static Query None()
    {
        return new TermQuery(new Term("__NeverMatch__", "__NeverMatch__"));
    }

    /// <summary>
    /// 等值匹配（TermQuery）。
    /// </summary>
    public static Query Term(string fieldName, string value)
    {
        return new TermQuery(new Term(fieldName, value));
    }

    /// <summary>
    /// 多值 IN（任意一个命中即可）。
    /// - values 为空时默认返回永不匹配（用于“无权限则查不到”这类强制过滤）。
    /// </summary>
    public static Query In(string fieldName, IEnumerable<string> values, bool matchNoDocsWhenEmpty = true)
    {
        if (values == null)
        {
            return matchNoDocsWhenEmpty ? None() : new MatchAllDocsQuery();
        }

        var list = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (list.Count == 0)
        {
            return matchNoDocsWhenEmpty ? None() : new MatchAllDocsQuery();
        }

        if (list.Count == 1)
        {
            return Term(fieldName, list[0]);
        }

        var bq = new BooleanQuery { MinimumNumberShouldMatch = 1 };
        foreach (var v in list)
        {
            bq.Add(Term(fieldName, v), Occur.SHOULD);
        }

        return bq;
    }

    /// <summary>
    /// object 版本的 IN，内部使用 ToString()。
    /// </summary>
    public static Query In(string fieldName, IEnumerable<object?> values, bool matchNoDocsWhenEmpty = true)
    {
        if (values == null)
        {
            return matchNoDocsWhenEmpty ? None() : new MatchAllDocsQuery();
        }

        return In(fieldName, values.Where(v => v != null).Select(v => v!.ToString()!), matchNoDocsWhenEmpty);
    }
}


