using System.Reflection;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.QueryParsers.Classic;
using Lucene.Net.Search;
using Lucene.Net.Util;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Entities;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.MultiTenancy;
using Zyknow.Abp.Lucene.Descriptors;
using Zyknow.Abp.Lucene.Dtos;
using Zyknow.Abp.Lucene.Filtering;
using Zyknow.Abp.Lucene.Options;
using Zyknow.Abp.Lucene.Permissions;
using Lucene.Net.Search.Highlight;
using Lucene.Net.Analysis;

namespace Zyknow.Abp.Lucene.Services;

public class LuceneAppService(
    IOptions<LuceneOptions> options,
    ICurrentTenant currentTenant,
    ILogger<LuceneAppService> logger,
    IServiceProvider serviceProvider,
    IEnumerable<ILuceneFilterProvider> filterProviders,
    ILuceneSearcherProvider searcherProvider)
    : ApplicationService, ILuceneService
{
    private readonly LuceneOptions _options = options.Value;
    private readonly ILuceneSearcherProvider _searcherProvider = searcherProvider;

    [Authorize(ZyknowLucenePermissions.Search.Default)]
    public virtual async Task<SearchResultDto> SearchAsync(string entityName, SearchQueryInput input)
    {
        await Task.Yield();
        var descriptor =
            _options.Descriptors.Values.FirstOrDefault(d =>
                d.IndexName.Equals(entityName, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
        {
            throw new BusinessException("Lucene:EntityNotConfigured").WithData("Entity", entityName);
        }

        var indexPath = GetIndexPath(descriptor.IndexName);
        logger.LogInformation(
            "Lucene search on index {Index} at path {Path}. Query={Query} Prefix={Prefix} Fuzzy={Fuzzy}",
            descriptor.IndexName, indexPath, input.Query, input.Prefix, input.Fuzzy);

        using var lease = await _searcherProvider.AcquireAsync(descriptor);
        var reader = lease.Searcher.IndexReader;

        var fields = descriptor.Fields.Where(f => f.Searchable).Select(f => f.Name).Distinct().ToArray();
        var analyzer = _options.AnalyzerFactory();
        var parser = new MultiFieldQueryParser(LuceneVersion.LUCENE_48, fields, analyzer)
        {
            DefaultOperator = _options.LuceneQuery.MultiFieldMode.Equals("AND", StringComparison.OrdinalIgnoreCase)
                ? Operator.AND
                : Operator.OR,
            AllowLeadingWildcard = true
        };

        var variants = new List<string> { input.Query };
        if (input.Prefix)
        {
            variants.Add($"{input.Query}*");
        }

        if (input.Fuzzy)
        {
            variants.Add($"{input.Query}~{_options.LuceneQuery.FuzzyMaxEdits}");
        }

        Query finalQuery;
        if (variants.Count == 1)
        {
            finalQuery = parser.Parse(variants[0]);
        }
        else
        {
            var bq = new BooleanQuery();
            foreach (var v in variants)
            {
                try
                {
                    var q = parser.Parse(v);
                    bq.Add(q, Occur.SHOULD);
                }
                catch (ParseException ex)
                {
                    logger.LogWarning("Lucene query variant parse failed: variant={Variant} error={Error}", v,
                        ex.Message);
                }
            }

            finalQuery = bq;
        }

        // 执行过滤器（MUST 合并）
        var filterCtx = new SearchFilterContext(entityName, descriptor, input);
        var composed = new BooleanQuery { { finalQuery, Occur.MUST } };
        foreach (var provider in filterProviders)
        {
            var fq = await provider.BuildAsync(filterCtx);
            if (fq == null && filterCtx.Expression != null)
            {
                // 使用端直接提供了表达式时，尝试统一转换
                try
                {
                    var gtype = filterCtx.Expression.Type.GetGenericArguments().FirstOrDefault();
                    if (gtype != null)
                    {
                        var method = typeof(LinqLucene).GetMethod(nameof(LinqLucene.Where))!.MakeGenericMethod(gtype);
                        fq = (Query)method.Invoke(null, [descriptor, filterCtx.Expression])!;
                    }
                }
                catch
                {
                    /* ignore conversion errors */
                }
            }

            if (fq != null)
            {
                composed.Add(fq, Occur.MUST);
            }
        }

        var searcher = lease.Searcher;
        var topN = input.SkipCount + input.MaxResultCount;
        var hits = searcher.Search(composed, topN);
        logger.LogInformation("Lucene search returned {Total} hits", hits.TotalHits);
        var slice = hits.ScoreDocs.Skip(input.SkipCount).Take(input.MaxResultCount).ToList();

        var results = new List<SearchHitDto>(slice.Count);
        foreach (var sd in slice)
        {
            var doc = searcher.Doc(sd.Doc);
            var payload = new Dictionary<string, string>();
            foreach (var f in descriptor.Fields.Where(f => f.Store))
            {
                payload[f.Name] = doc.Get(f.Name);
            }

            // 新增：按需构建高亮片段
            Dictionary<string, List<string>>? highlights = null;
            if (input.Highlight)
            {
                highlights = BuildHighlights(composed, analyzer, doc, descriptor.Fields.Where(x => x.Store));
            }

            results.Add(new SearchHitDto
            {
                EntityId = doc.Get(descriptor.IdFieldName) ?? string.Empty,
                Score = sd.Score,
                Payload = payload,
                Highlights = highlights ?? new()
            });
        }

        return new SearchResultDto(hits.TotalHits, results);
    }

    // 新增：MultiReader 聚合多实体搜索
    [Authorize(ZyknowLucenePermissions.Search.Default)]
    public virtual async Task<SearchResultDto> SearchManyAsync(MultiSearchInput input)
    {
        await Task.Yield();

        var entityNames = input.Entities;
        if (entityNames == null || entityNames.Count == 0)
        {
            throw new BusinessException("Lucene:EmptyEntities");
        }

        var descriptors = new List<EntitySearchDescriptor>();
        var leases = new List<SearchLease>();

        try
        {
            foreach (var name in entityNames)
            {
                var d = _options.Descriptors.Values.FirstOrDefault(x =>
                    x.IndexName.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (d == null)
                {
                    logger.LogWarning("Lucene multi search ignored unconfigured entity {Entity}", name);
                    continue;
                }

                var p = GetIndexPath(d.IndexName);
                if (!System.IO.Directory.Exists(p))
                {
                    logger.LogInformation("Lucene multi search skipped entity {Entity} with no directory at {Path}", name, p);
                    continue;
                }

                var lease = await _searcherProvider.AcquireAsync(d);
                leases.Add(lease);
                descriptors.Add(d);
            }

            if (descriptors.Count == 0)
            {
                logger.LogInformation("Lucene multi search: no configured entities found. Returning empty result.");
                return new SearchResultDto(0, new List<SearchHitDto>(0));
            }

            var fields = descriptors
                .SelectMany(d => d.Fields.Where(f => f.Searchable).Select(f => f.Name))
                .Distinct()
                .ToArray();

            var analyzer = _options.AnalyzerFactory();
            var parser = new MultiFieldQueryParser(LuceneVersion.LUCENE_48, fields, analyzer)
            {
                DefaultOperator = _options.LuceneQuery.MultiFieldMode.Equals("AND", StringComparison.OrdinalIgnoreCase)
                    ? Operator.AND
                    : Operator.OR,
                AllowLeadingWildcard = true
            };

            var variants = new List<string> { input.Query };
            if (input.Prefix)
            {
                variants.Add($"{input.Query}*");
            }
            if (input.Fuzzy)
            {
                variants.Add($"{input.Query}~{_options.LuceneQuery.FuzzyMaxEdits}");
            }

            Query finalQuery;
            if (variants.Count == 1)
            {
                finalQuery = parser.Parse(variants[0]);
            }
            else
            {
                var bq = new BooleanQuery();
                foreach (var v in variants)
                {
                    try
                    {
                        var q = parser.Parse(v);
                        bq.Add(q, Occur.SHOULD);
                    }
                    catch (ParseException ex)
                    {
                        logger.LogWarning("Lucene query variant parse failed: variant={Variant} error={Error}", v,
                            ex.Message);
                    }
                }
                finalQuery = bq;
            }

            var readers = leases.Select(l => l.Searcher.IndexReader).ToArray();
            // 注意：不要让 MultiReader 关闭子 reader（由 SearcherManager 管理）
            using var multi = new MultiReader(readers, false);
            var searcher = new IndexSearcher(multi);
            var topN = input.SkipCount + input.MaxResultCount;
            var hits = searcher.Search(finalQuery, topN);
            logger.LogInformation("Lucene multi search returned {Total} hits across {Count} indexes", hits.TotalHits, readers.Length);
            var slice = hits.ScoreDocs.Skip(input.SkipCount).Take(input.MaxResultCount).ToList();

            var results = new List<SearchHitDto>(slice.Count);
            foreach (var sd in slice)
            {
                var doc = searcher.Doc(sd.Doc);
                var payload = new Dictionary<string, string>();
                foreach (var d in descriptors)
                {
                    foreach (var f in d.Fields.Where(f => f.Store))
                    {
                        var v = doc.Get(f.Name);
                        if (v != null && !payload.ContainsKey(f.Name))
                        {
                            payload[f.Name] = v;
                        }
                    }
                }

                payload["__IndexName"] = ResolveDocIndexName(descriptors, doc);
                var id = descriptors.Select(d => doc.Get(d.IdFieldName)).FirstOrDefault(s => !string.IsNullOrEmpty(s)) ?? string.Empty;

                Dictionary<string, List<string>>? highlights = null;
                if (input.Highlight)
                {
                    var allStoreFields = descriptors.SelectMany(d => d.Fields.Where(x => x.Store));
                    highlights = BuildHighlights(finalQuery, analyzer, doc, allStoreFields);
                }

                results.Add(new SearchHitDto
                {
                    EntityId = id,
                    Score = sd.Score,
                    Payload = payload,
                    Highlights = highlights ?? new()
                });
            }

            return new SearchResultDto(hits.TotalHits, results);
        }
        finally
        {
            foreach (var l in leases)
            {
                l.Dispose();
            }
        }
    }

    [Authorize(ZyknowLucenePermissions.Indexing.Rebuild)]
    public virtual async Task<int> RebuildIndexAsync(string entityName)
    {
        await Task.Yield();
        var descriptor =
            _options.Descriptors.Values.FirstOrDefault(d =>
                d.IndexName.Equals(entityName, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
        {
            throw new BusinessException("Lucene:EntityNotConfigured").WithData("Entity", entityName);
        }

        var indexPath = GetIndexPath(descriptor.IndexName);
        logger.LogInformation("Lucene rebuild index {Index} at path {Path}", descriptor.IndexName, indexPath);
        using var dir = _options.DirectoryFactory(indexPath);
        var analyzer = _options.AnalyzerFactory();
        var config = new IndexWriterConfig(LuceneVersion.LUCENE_48, analyzer);
        using var writer = new IndexWriter(dir, config);
        var beforeMaxDoc = writer.MaxDoc;
        writer.DeleteAll();
        writer.Commit();
        logger.LogInformation("Lucene rebuild cleared {Before} docs from {Index}", beforeMaxDoc, descriptor.IndexName);
        return beforeMaxDoc;
    }

    [Authorize(ZyknowLucenePermissions.Indexing.Rebuild)]
    public virtual async Task<int> RebuildAndIndexAllAsync(string entityName, int batchSize = 1000)
    {
        await Task.Yield();
        var descriptor =
            _options.Descriptors.Values.FirstOrDefault(d =>
                d.IndexName.Equals(entityName, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
        {
            throw new BusinessException("Lucene:EntityNotConfigured").WithData("Entity", entityName);
        }

        // 先重建以确保目录与写入器存在
        await RebuildIndexAsync(entityName);

        // 反射解析仓储 IRepository<TEntity, TKey>
        var entityType = descriptor.EntityType;
        var keyType = entityType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEntity<>))
            ?.GetGenericArguments().FirstOrDefault() ?? typeof(Guid);

        var repoType = typeof(IRepository<,>).MakeGenericType(entityType, keyType);
        object? repository = serviceProvider.GetService(repoType);
        if (repository is null)
        {
            // 回退：尝试解析单泛型仓储 IRepository<TEntity>
            var repoSingleType = typeof(IRepository<>).MakeGenericType(entityType);
            repository = serviceProvider.GetService(repoSingleType);
        }

        if (repository is null)
        {
            logger.LogWarning("Lucene rebuild+index: repository not found for entity {Entity}", entityType.FullName);
            // 无仓储无法同步，返回当前索引文档数（重建后通常为0）
            return await GetIndexDocumentCountInternalAsync(descriptor);
        }

        var synchronizer = serviceProvider.GetRequiredService<LuceneIndexSynchronizer>();

        // 优先尝试调用 SyncAllAndCountAsync<T,TKey> 获取处理条数
        var countMethod = typeof(LuceneIndexSynchronizer)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == nameof(LuceneIndexSynchronizer.SyncAllAndCountAsync)
                                  && m.IsGenericMethodDefinition
                                  && m.GetGenericArguments().Length == 2);
        if (countMethod != null && repoType.IsInstanceOfType(repository))
        {
            try
            {
                var gCount = countMethod.MakeGenericMethod(entityType, keyType);
                logger.LogInformation("Lucene rebuild+index: start SyncAllAndCount for {Entity} with batchSize {Batch}",
                    entityType.FullName, batchSize);
                var processed = await (Task<int>)gCount.Invoke(synchronizer,
                    [repository, batchSize, true, CancellationToken.None])!;
                logger.LogInformation("Lucene rebuild+index: completed for {Entity}, processed={Processed}",
                    entityType.FullName, processed);
                // 直接返回处理条数，避免新索引尚未可见导致的 0 计数
                return processed;
            }
            catch (TargetInvocationException tie)
            {
                logger.LogWarning(tie, "Lucene rebuild+index: SyncAllAndCount failed for {Entity}, fallback to SyncAll", entityType.FullName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Lucene rebuild+index: SyncAllAndCount failed for {Entity}, fallback to SyncAll", entityType.FullName);
            }
        }

        // 回退：调用 SyncAllAsync<T,TKey>
        var method = typeof(LuceneIndexSynchronizer)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .First(m => m.Name == nameof(LuceneIndexSynchronizer.SyncAllAsync) && m.IsGenericMethodDefinition &&
                        m.GetGenericArguments().Length == 2);
        var gmethod = method.MakeGenericMethod(entityType, keyType);
        logger.LogInformation("Lucene rebuild+index: start SyncAll for {Entity} with batchSize {Batch}",
            entityType.FullName, batchSize);
        await (Task)gmethod.Invoke(synchronizer,
            [repository, batchSize, true, CancellationToken.None])!;
        logger.LogInformation("Lucene rebuild+index: completed for {Entity}", entityType.FullName);

        return await GetIndexDocumentCountInternalAsync(descriptor);
    }

    [Authorize(ZyknowLucenePermissions.Indexing.Default)]
    public virtual async Task<int> GetIndexDocumentCountAsync(string entityName)
    {
        await Task.Yield();
        var descriptor =
            _options.Descriptors.Values.FirstOrDefault(d =>
                d.IndexName.Equals(entityName, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
        {
            throw new BusinessException("Lucene:EntityNotConfigured").WithData("Entity", entityName);
        }

        var indexPath = GetIndexPath(descriptor.IndexName);
        // 若目录或索引文件不存在，直接返回 0，避免打开 Reader 抛错
        if (!System.IO.Directory.Exists(indexPath) || System.IO.Directory.GetFiles(indexPath).Length == 0)
        {
            logger.LogInformation("Lucene index {Index} at {Path} has 0 docs (directory empty or not exists)", descriptor.IndexName, indexPath);
            return 0;
        }

        using var dir = _options.DirectoryFactory(indexPath);
        using var reader = DirectoryReader.Open(dir);
        // 修复：使用 NumDocs（排除已删除文档）而非 MaxDoc
        var live = reader.NumDocs;
        logger.LogInformation("Lucene index {Index} at {Path} has {Count} live docs", descriptor.IndexName, indexPath, live);
        return live;
    }

    private static string ResolveDocIndexName(IEnumerable<EntitySearchDescriptor> descriptors, Document doc)
    {
        // 简单策略：匹配哪个描述符的存储字段能唯一识别来源；若无法唯一，则返回 Unknown
        foreach (var d in descriptors)
        {
            var hasAny = d.Fields.Any(f => f.Store && doc.Get(f.Name) != null);
            if (hasAny)
            {
                return d.IndexName;
            }
        }

        return "Unknown";
    }

    // 新增：构建高亮片段
    private Dictionary<string, List<string>> BuildHighlights(Query query, Analyzer analyzer, Document doc, IEnumerable<FieldDescriptor> fields)
    {
        var result = new Dictionary<string, List<string>>();
        try
        {
            var scorer = new QueryScorer(query);
            var formatter = new SimpleHTMLFormatter("<em>", "</em>");
            var highlighter = new Highlighter(formatter, scorer)
            {
                TextFragmenter = new SimpleSpanFragmenter(scorer, 100)
            };

            foreach (var f in fields)
            {
                var val = doc.Get(f.Name);
                if (string.IsNullOrEmpty(val)) continue;

                try
                {
                    using var reader = new StringReader(val);
                    var ts = analyzer.GetTokenStream(f.Name, reader);
                    var frags = highlighter.GetBestTextFragments(ts, val, false, 3);
                    var pieces = frags
                        .Where(x => x != null && x.Score > 0)
                        .Select(x => x.ToString())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList();
                    if (pieces.Count > 0)
                    {
                        result[f.Name] = pieces;
                    }
                }
                catch (Exception ex)
                {
                    // 高亮失败不影响主流程
                    logger.LogDebug(ex, "Lucene highlight failed for field {Field}", f.Name);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Lucene highlight initialization failed");
        }

        return result;
    }

    protected virtual string GetIndexPath(string indexName)
    {
        var root = _options.IndexRootPath;
        if (_options.PerTenantIndex && currentTenant.Id.HasValue)
        {
            return Path.Combine(root, currentTenant.Id.Value.ToString(), indexName);
        }

        return Path.Combine(root, indexName);
    }

    // 内部帮助：读取指定描述符的当前文档数
    private async Task<int> GetIndexDocumentCountInternalAsync(EntitySearchDescriptor descriptor)
    {
        await Task.Yield();
        var indexPath = GetIndexPath(descriptor.IndexName);
        // 若目录或索引文件不存在，直接返回 0
        if (!System.IO.Directory.Exists(indexPath) || System.IO.Directory.GetFiles(indexPath).Length == 0)
        {
            logger.LogInformation("Lucene index {Index} at {Path} has 0 docs (directory empty or not exists)", descriptor.IndexName, indexPath);
            return 0;
        }

        using var dir = _options.DirectoryFactory(indexPath);
        using var reader = DirectoryReader.Open(dir);
        // 修复：使用 NumDocs（排除已删除文档）
        var live = reader.NumDocs;
        logger.LogInformation("Lucene index {Index} at {Path} has {Count} live docs", descriptor.IndexName, indexPath,
            live);
        return live;
    }
}
