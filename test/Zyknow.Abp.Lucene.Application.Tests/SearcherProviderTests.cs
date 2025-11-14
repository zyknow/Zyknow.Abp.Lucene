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

        // 追加断言：验证每条命中的 __IndexName 精确对应
        foreach (var hit in res.Items)
        {
            var hasTitle = hit.Payload.ContainsKey(nameof(Book.Title));
            var hasName = hit.Payload.ContainsKey(nameof(Item.Name));
            Assert.True(hit.Payload.ContainsKey("__IndexName"));
            if (hasTitle)
            {
                Assert.Equal("Book", hit.Payload["__IndexName"]);
            }
            if (hasName && !hasTitle)
            {
                Assert.Equal("Item", hit.Payload["__IndexName"]);
            }
        }
    }

    [Fact]
    public async Task MultiIndex_SearchMany_Should_Set_Correct_IndexName_In_Payload()
    {
        var root = CreateIsolatedIndexRoot();
        using var sp = BuildServiceProvider(root);
        var indexer = sp.GetRequiredService<LuceneIndexManager>();
        var app = sp.GetRequiredService<LuceneAppService>();

        await indexer.RebuildAsync(typeof(Book));
        await indexer.RebuildAsync(typeof(Item));

        await indexer.IndexRangeAsync(new[]
        {
            new Book("b1","Lucene Basics","Alan","B010"),
            new Book("b2","Lucene Internals","Beth","B011")
        }, replace: true);

        await indexer.IndexRangeAsync(new[]
        {
            new Item("i1","Lucene sticker","B010"),
            new Item("i2","Pen","B099")
        }, replace: true);

        var res = await app.SearchManyAsync(new()
        {
            Entities = ["Book","Item"],
            Query = "Lucene",
            SkipCount = 0,
            MaxResultCount = 20
        });

        Assert.NotEmpty(res.Items);
        foreach (var hit in res.Items)
        {
            Assert.True(hit.Payload.TryGetValue("__IndexName", out var idx));
            if (hit.Payload.ContainsKey(nameof(Book.Title)))
            {
                Assert.Equal("Book", idx);
            }
            else if (hit.Payload.ContainsKey(nameof(Item.Name)))
            {
                Assert.Equal("Item", idx);
            }
        }
    }

    [Fact]
    public async Task MultiIndex_SearchMany_IndexName_Should_Not_Depend_On_Entities_Order()
    {
        var root = CreateIsolatedIndexRoot();
        using var sp = BuildServiceProvider(root);
        var indexer = sp.GetRequiredService<LuceneIndexManager>();
        var app = sp.GetRequiredService<LuceneAppService>();

        await indexer.RebuildAsync(typeof(Book));
        await indexer.RebuildAsync(typeof(Item));

        await indexer.IndexRangeAsync(new[]
        {
            new Book("b1","Lucene Guide","A","B100"),
        }, replace: true);
        await indexer.IndexRangeAsync(new[]
        {
            new Item("i1","Lucene T-Shirt","B100"),
        }, replace: true);

        // 顺序：Book, Item
        var res1 = await app.SearchManyAsync(new()
        {
            Entities = ["Book","Item"],
            Query = "Lucene",
            SkipCount = 0,
            MaxResultCount = 10
        });

        // 顺序：Item, Book
        var res2 = await app.SearchManyAsync(new()
        {
            Entities = ["Item","Book"],
            Query = "Lucene",
            SkipCount = 0,
            MaxResultCount = 10
        });

        // 对于包含 Title 的文档，__IndexName 应为 Book；包含 Name 的文档，__IndexName 为 Item
        void AssertIndexNamesCorrect(Zyknow.Abp.Lucene.Dtos.SearchResultDto r)
        {
            foreach (var hit in r.Items)
            {
                var ok = hit.Payload.TryGetValue("__IndexName", out var nm);
                Assert.True(ok);
                if (hit.Payload.ContainsKey(nameof(Book.Title)))
                {
                    Assert.Equal("Book", nm);
                }
                else if (hit.Payload.ContainsKey(nameof(Item.Name)))
                {
                    Assert.Equal("Item", nm);
                }
            }
        }

        AssertIndexNamesCorrect(res1);
        AssertIndexNamesCorrect(res2);
    }

    private record Book(string Id, string Title, string Author, string Code);
    private record Item(string Id, string Name, string Code);
}

