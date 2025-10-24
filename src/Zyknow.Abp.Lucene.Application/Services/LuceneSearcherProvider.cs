using System.Collections.Concurrent;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.MultiTenancy;
using Zyknow.Abp.Lucene.Descriptors;
using Zyknow.Abp.Lucene.Options;
using Directory = Lucene.Net.Store.Directory;

namespace Zyknow.Abp.Lucene.Services;


// TODO: 若需要更低延迟的“近实时”（未 Commit 也可见），可引入 TrackingIndexWriter + ControlledRealTimeReopenThread，在写入后按延迟阈值触发刷新。
// 跨进程写入的场景可配合分布式锁（ABP Distributed Lock）扩展写入保护。
// 为 SearcherManager 增设后台定时刷新（低频 MaybeRefresh），减少每次查询调用刷新带来的微小开销

public interface ILuceneSearcherProvider : IDisposable
{
    Task<SearchLease> AcquireAsync(EntitySearchDescriptor descriptor, CancellationToken cancellationToken = default);
}

public sealed class SearchLease : IDisposable
{
    private readonly SearcherManager _manager;
    private bool _released;
    public IndexSearcher Searcher { get; }

    internal SearchLease(SearcherManager manager, IndexSearcher searcher)
    {
        _manager = manager;
        Searcher = searcher;
    }

    public void Dispose()
    {
        if (_released) return;
        _released = true;
        _manager.Release(Searcher);
    }
}

public sealed class LuceneSearcherProvider : ILuceneSearcherProvider
{
    private readonly LuceneOptions _options;
    private readonly ICurrentTenant _currentTenant;
    private readonly ILogger<LuceneSearcherProvider> _logger;

    private sealed class Entry : IDisposable
    {
        public required string IndexPath { get; init; }
        public required Directory Directory { get; init; }
        public required SearcherManager Manager { get; init; }
        public void Dispose()
        {
            Manager?.Dispose();
            Directory?.Dispose();
        }
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public LuceneSearcherProvider(IOptions<LuceneOptions> options, ICurrentTenant currentTenant, ILogger<LuceneSearcherProvider> logger)
    {
        _options = options.Value;
        _currentTenant = currentTenant;
        _logger = logger;
    }

    public async Task<SearchLease> AcquireAsync(EntitySearchDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        var indexPath = GetIndexPath(descriptor.IndexName);
        var entry = _entries.GetOrAdd(indexPath, _ => CreateEntry(indexPath));

        // 轻量刷新，确保看到最近提交
        try
        {
            entry.Manager.MaybeRefresh();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SearcherManager MaybeRefresh failed for {Index}", descriptor.IndexName);
        }

        var searcher = entry.Manager.Acquire();
        return new SearchLease(entry.Manager, searcher);
    }

    private Entry CreateEntry(string indexPath)
    {
        _logger.LogInformation("Create SearcherManager for index path {Path}", indexPath);
        var dir = _options.DirectoryFactory(indexPath);

        // 若索引尚未初始化，创建一个空的提交以便 Reader 正常打开
        EnsureIndexInitialized(dir);

        // 使用 Directory 版本的 SearcherManager；读取端自动增量刷新
        var manager = new SearcherManager(dir, new SearcherFactory());
        return new Entry { IndexPath = indexPath, Directory = dir, Manager = manager };
    }

    private void EnsureIndexInitialized(Directory dir)
    {
        try
        {
            if (dir.ListAll().Length == 0)
            {
                using var iw = new IndexWriter(dir, new IndexWriterConfig(LuceneVersion.LUCENE_48, _options.AnalyzerFactory()));
                iw.Commit();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "EnsureIndexInitialized encountered error; will proceed if index already exists");
        }
    }

    private string GetIndexPath(string indexName)
    {
        var root = _options.IndexRootPath;
        if (_options.PerTenantIndex && _currentTenant.Id.HasValue)
        {
            return Path.Combine(root, _currentTenant.Id.Value.ToString(), indexName);
        }
        return Path.Combine(root, indexName);
    }

    public void Dispose()
    {
        foreach (var kv in _entries)
        {
            try { kv.Value.Dispose(); } catch { /* ignore */ }
        }
        _entries.Clear();
    }
}
