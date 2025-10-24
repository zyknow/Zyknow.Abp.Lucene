using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.MultiTenancy;
using Zyknow.Abp.Lucene.Analyzers;
using Zyknow.Abp.Lucene.Application.Tests.Fakes;
using Zyknow.Abp.Lucene.Indexing;
using Zyknow.Abp.Lucene.Options;
using Zyknow.Abp.Lucene.Services;

namespace Zyknow.Abp.Lucene.Application.Tests;

public class SearcherProviderTests
{
    private static string CreateIsolatedIndexRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "lucene-index-searcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static ServiceProvider BuildServiceProvider(string indexRoot)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentTenant>(new FakeCurrentTenant { Id = null });
        services.Configure<LuceneOptions>(opt =>
        {
            opt.PerTenantIndex = false;
            opt.IndexRootPath = indexRoot;
            opt.AnalyzerFactory = AnalyzerFactories.IcuGeneral;
            opt.ConfigureLucene(model =>
            {
                model.Entity<Book>(e =>
                {
                    e.Field(x => x.Title, f => f.Name(nameof(Book.Title)).Store());
                    e.Field(x => x.Author, f => f.Name(nameof(Book.Author)).Store());
                    e.Field(x => x.Code, f => f.Name(nameof(Book.Code)).Keyword());
                });
                model.Entity<Item>(e =>
                {
                    e.Field(x => x.Name, f => f.Store());
                    e.Field(x => x.Code, f => f.Keyword());
                });
            });
        });
        services.AddSingleton<LuceneIndexManager>();
        services.AddSingleton<ILuceneSearcherProvider, LuceneSearcherProvider>();
        services.AddSingleton<LuceneAppService>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task SingleIndex_Should_See_New_Commits_After_MaybeRefresh()
    {
        var root = CreateIsolatedIndexRoot();
        using var sp = BuildServiceProvider(root);
        var indexer = sp.GetRequiredService<LuceneIndexManager>();
        var app = sp.GetRequiredService<LuceneAppService>();

        await indexer.RebuildAsync(typeof(Book));

        var batch1 = Enumerable.Range(0, 5).Select(i => new Book(i.ToString(), $"Alpha-{i}", "A", $"C{i:0000}")).ToList();
        await indexer.IndexRangeAsync(batch1, replace: false);

        var r1 = await app.SearchAsync("Book", new() { Query = "Alpha", SkipCount = 0, MaxResultCount = 100 });
        Assert.Equal(5, r1.TotalCount);

        var batch2 = Enumerable.Range(5, 3).Select(i => new Book(i.ToString(), $"Alpha-{i}", "A", $"C{i:0000}")).ToList();
        await indexer.IndexRangeAsync(batch2, replace: false);

        // 再次搜索，期望无需重建 Reader 也能看到新提交（MaybeRefresh）
        var r2 = await app.SearchAsync("Book", new() { Query = "Alpha", SkipCount = 0, MaxResultCount = 100 });
        Assert.Equal(8, r2.TotalCount);
    }

    [Fact]
    public async Task MultiIndex_SearchMany_Should_Aggregate_Across_Indexes()
    {
        var root = CreateIsolatedIndexRoot();
        using var sp = BuildServiceProvider(root);
        var indexer = sp.GetRequiredService<LuceneIndexManager>();
        var app = sp.GetRequiredService<LuceneAppService>();

        await indexer.RebuildAsync(typeof(Book));
        await indexer.RebuildAsync(typeof(Item));

        var books = new List<Book>
        {
            new("b1","Lucene in Action","Erik","B001"),
            new("b2","Advanced Lucene","John","B002")
        };
        var items = new List<Item>
        {
            new("i1","Lucene-branded mug","B001"),
            new("i2","Notebook","B003")
        };
        await indexer.IndexRangeAsync(books, replace: true);
        await indexer.IndexRangeAsync(items, replace: true);

        var res = await app.SearchManyAsync(new()
        {
            Entities = ["Book","Item"],
            Query = "Lucene",
            SkipCount = 0,
            MaxResultCount = 10
        });

        Assert.True(res.TotalCount >= 2);
        Assert.Contains(res.Items, i => i.Payload.Values.Any(v => v.Contains("Lucene")));
    }

    [Fact]
    public async Task Concurrent_Search_Should_Be_Stable_And_Consistent()
    {
        var root = CreateIsolatedIndexRoot();
        using var sp = BuildServiceProvider(root);
        var indexer = sp.GetRequiredService<LuceneIndexManager>();
        var app = sp.GetRequiredService<LuceneAppService>();

        await indexer.RebuildAsync(typeof(Book));

        var docs = Enumerable.Range(0, 100).Select(i => new Book(i.ToString(), $"X-{i}", "AU", $"CX{i:0000}")).ToList();
        await indexer.IndexRangeAsync(docs, replace: false);

        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            var local = await app.SearchAsync("Book", new() { Query = "X", SkipCount = 0, MaxResultCount = 200 });
            return local.TotalCount;
        });

        var counts = await Task.WhenAll(tasks);
        Assert.All(counts, c => Assert.Equal(100, c));
    }

    private record Book(string Id, string Title, string Author, string Code);
    private record Item(string Id, string Name, string Code);
}

