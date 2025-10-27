using System.Collections.Generic;

namespace Simple.Books;

/// <summary>
/// 并发 + UnitOfWork Lucene 测试结果。
/// </summary>
public sealed class LuceneConcurrentTestResultDto
{
    /// <summary>并发线程数。</summary>
    public int Threads { get; init; }
    /// <summary>每个线程插入的实体数。</summary>
    public int PerThread { get; init; }
    /// <summary>总插入条数（预期）。</summary>
    public int Inserted { get; init; }
    /// <summary>数据库中实际的 Book 条数（当前租户）。</summary>
    public long DbCount { get; init; }
    /// <summary>Lucene 索引中文档数（Book 索引）。</summary>
    public int IndexCount { get; init; }
    /// <summary>总耗时（毫秒）。</summary>
    public long ElapsedMs { get; init; }
    /// <summary>错误列表（若为空则表示成功）。</summary>
    public List<string> Errors { get; set; } = new();
}
