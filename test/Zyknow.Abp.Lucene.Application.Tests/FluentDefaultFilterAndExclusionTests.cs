using Lucene.Net.Index;
using Lucene.Net.Search;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.MultiTenancy;
using Zyknow.Abp.Lucene.Analyzers;
using Zyknow.Abp.Lucene.Application.Tests.Fakes;
using Zyknow.Abp.Lucene.Dtos;
using Zyknow.Abp.Lucene.Filtering;
using Zyknow.Abp.Lucene.Indexing;
using Zyknow.Abp.Lucene.Options;
using Zyknow.Abp.Lucene.Services;

namespace Zyknow.Abp.Lucene.Application.Tests;

public class FluentDefaultFilterAndExclusionTests
{
    private interface IAllowedCodeProvider
    {
        string AllowedCode { get; }
    }

    private sealed class AllowedCodeProvider(string allowedCode) : IAllowedCodeProvider
    {
        public string AllowedCode { get; } = allowedCode;
    }

    [Fact]
    public async Task ConfigureLucene_ForceFilter_Should_Always_Apply_And_Can_Use_ServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentTenant>(new FakeCurrentTenant { Id = null });
        services.AddSingleton<IAllowedCodeProvider>(new AllowedCodeProvider("B001"));

        services.Configure<LuceneOptions>(opt =>
        {
            opt.PerTenantIndex = false;
            opt.IndexRootPath = Path.Combine(Path.GetTempPath(), "lucene-index-tests", Guid.NewGuid().ToString("N"));
            opt.AnalyzerFactory = AnalyzerFactories.IcuGeneral;
            opt.ConfigureLucene(model =>
            {
                model.Entity<Book>(e =>
                {
                    e.Field(x => x.Title, f => f.Store());
                    e.Field(x => x.Code, f => f.Keyword().Store());

                    // 关键：强制过滤（每次查询 MUST），并可用 ServiceProvider 注入依赖
                    e.ForceFilter<SearchQueryInput>((sp, ctx) =>
                    {
                        var allowed = sp.GetRequiredService<IAllowedCodeProvider>().AllowedCode;
                        return new TermQuery(new Term(nameof(Book.Code), allowed));
                    });
                });
            });
        });

        services.AddSingleton<LuceneIndexManager>();
        services.AddSingleton<ILuceneSearcherProvider, LuceneSearcherProvider>();
        services.AddSingleton<LuceneAppService>();

        var sp = services.BuildServiceProvider();
        var indexer = sp.GetRequiredService<LuceneIndexManager>();
        var search = sp.GetRequiredService<LuceneAppService>();

        await indexer.RebuildAsync(typeof(Book));
        await indexer.IndexRangeAsync(new List<Book>
        {
            new("1", "Lucene in Action", "B001"),
            new("2", "Lucene for .NET", "B002")
        }, true);

        var result = await search.SearchAsync("Book", new SearchQueryInput
        {
            Query = "Lucene",
            MaxResultCount = 10,
            SkipCount = 0
        });

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("1", result.Items.Single().EntityId);
        Assert.Equal("B001", result.Items.Single().Payload[nameof(Book.Code)]);
    }

    [Fact]
    public async Task ConfigureLucene_ExcludeFromIndex_When_Becomes_Excluded_Should_Remove_From_Index()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentTenant>(new FakeCurrentTenant { Id = null });

        services.Configure<LuceneOptions>(opt =>
        {
            opt.PerTenantIndex = false;
            opt.IndexRootPath = Path.Combine(Path.GetTempPath(), "lucene-index-tests", Guid.NewGuid().ToString("N"));
            opt.AnalyzerFactory = AnalyzerFactories.IcuGeneral;
            opt.ConfigureLucene(model =>
            {
                model.Entity<SoftBook>(e =>
                {
                    e.Field(x => x.Title, f => f.Store());

                    // 关键：命中剔除规则后，不写入并尽量从索引删除
                    e.ExcludeFromIndexWhen((sp, x) => x.IsDeleted);
                });
            });
        });

        services.AddSingleton<LuceneIndexManager>();
        services.AddSingleton<ILuceneSearcherProvider, LuceneSearcherProvider>();
        services.AddSingleton<LuceneAppService>();

        var sp = services.BuildServiceProvider();
        var indexer = sp.GetRequiredService<LuceneIndexManager>();
        var app = sp.GetRequiredService<LuceneAppService>();

        await indexer.RebuildAsync(typeof(SoftBook));
        await indexer.IndexAsync(new SoftBook("1", "Lucene in Action", false));
        Assert.Equal(1, await app.GetIndexDocumentCountAsync("SoftBook"));

        // 同一 ID 变成被剔除：应从索引删除
        await indexer.IndexAsync(new SoftBook("1", "Lucene in Action", true));
        Assert.Equal(0, await app.GetIndexDocumentCountAsync("SoftBook"));
    }

    [Fact]
    public async Task ConfigureLucene_ExcludeFromIndex_When_BatchUpdate_To_Excluded_Should_Remove_From_Index()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentTenant>(new FakeCurrentTenant { Id = null });

        services.Configure<LuceneOptions>(opt =>
        {
            opt.PerTenantIndex = false;
            opt.IndexRootPath = Path.Combine(Path.GetTempPath(), "lucene-index-tests", Guid.NewGuid().ToString("N"));
            opt.AnalyzerFactory = AnalyzerFactories.IcuGeneral;
            opt.ConfigureLucene(model =>
            {
                model.Entity<SoftBook>(e =>
                {
                    e.Field(x => x.Title, f => f.Store());
                    e.ExcludeFromIndexWhen((sp, x) => x.IsDeleted);
                });
            });
        });

        services.AddSingleton<LuceneIndexManager>();
        services.AddSingleton<ILuceneSearcherProvider, LuceneSearcherProvider>();
        services.AddSingleton<LuceneAppService>();

        var sp = services.BuildServiceProvider();
        var indexer = sp.GetRequiredService<LuceneIndexManager>();
        var app = sp.GetRequiredService<LuceneAppService>();

        await indexer.RebuildAsync(typeof(SoftBook));

        await indexer.IndexRangeAsync(new List<SoftBook>
        {
            new("1", "Lucene in Action", false)
        }, replace: false);
        Assert.Equal(1, await app.GetIndexDocumentCountAsync("SoftBook"));

        // 批量更新到“被剔除”，应删除旧文档
        await indexer.IndexRangeAsync(new List<SoftBook>
        {
            new("1", "Lucene in Action", true)
        }, replace: false);
        Assert.Equal(0, await app.GetIndexDocumentCountAsync("SoftBook"));
    }

    [Fact]
    public async Task ConfigureLucene_ForceFilter_In_EmptyList_Should_Return_NoHits()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentTenant>(new FakeCurrentTenant { Id = null });

        services.Configure<LuceneOptions>(opt =>
        {
            opt.PerTenantIndex = false;
            opt.IndexRootPath = Path.Combine(Path.GetTempPath(), "lucene-index-tests", Guid.NewGuid().ToString("N"));
            opt.AnalyzerFactory = AnalyzerFactories.IcuGeneral;
            opt.ConfigureLucene(model =>
            {
                model.Entity<Book>(e =>
                {
                    e.Field(x => x.Title, f => f.Store());
                    e.Field(x => x.Code, f => f.Keyword());

                    // 空权限：默认应查不到（LuceneQueries.In 空集合默认返回“永不匹配”）
                    e.ForceFilter<SearchQueryInput>((sp, ctx) =>
                        LuceneQueries.In(nameof(Book.Code), Array.Empty<string>()));
                });
            });
        });

        services.AddSingleton<LuceneIndexManager>();
        services.AddSingleton<ILuceneSearcherProvider, LuceneSearcherProvider>();
        services.AddSingleton<LuceneAppService>();

        var sp = services.BuildServiceProvider();
        var indexer = sp.GetRequiredService<LuceneIndexManager>();
        var search = sp.GetRequiredService<LuceneAppService>();

        await indexer.RebuildAsync(typeof(Book));
        await indexer.IndexRangeAsync(new List<Book>
        {
            new("1", "Lucene in Action", "B001"),
            new("2", "Lucene for .NET", "B002")
        }, true);

        var result = await search.SearchAsync("Book", new SearchQueryInput
        {
            Query = "Lucene",
            MaxResultCount = 10,
            SkipCount = 0
        });

        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public async Task ConfigureLucene_ForceFilter_SearchMany_Should_Apply_PerIndex()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentTenant>(new FakeCurrentTenant { Id = null });

        services.Configure<LuceneOptions>(opt =>
        {
            opt.PerTenantIndex = false;
            opt.IndexRootPath = Path.Combine(Path.GetTempPath(), "lucene-index-tests", Guid.NewGuid().ToString("N"));
            opt.AnalyzerFactory = AnalyzerFactories.IcuGeneral;
            opt.ConfigureLucene(model =>
            {
                model.Entity<Book>(e =>
                {
                    e.Field(x => x.Title, f => f.Store());
                    e.Field(x => x.Code, f => f.Keyword().Store());
                    // 只允许 B001
                    e.ForceFilter<MultiSearchInput>((sp, ctx) =>
                        new TermQuery(new Term(nameof(Book.Code), "B001")));
                });

                model.Entity<Item>(e =>
                {
                    e.Field(x => x.Name, f => f.Store());
                    e.Field(x => x.Code, f => f.Keyword().Store());
                    // 只允许 B003
                    e.ForceFilter<MultiSearchInput>((sp, ctx) =>
                        new TermQuery(new Term(nameof(Item.Code), "B003")));
                });
            });
        });

        services.AddSingleton<LuceneIndexManager>();
        services.AddSingleton<ILuceneSearcherProvider, LuceneSearcherProvider>();
        services.AddSingleton<LuceneAppService>();

        var sp = services.BuildServiceProvider();
        var indexer = sp.GetRequiredService<LuceneIndexManager>();
        var search = sp.GetRequiredService<LuceneAppService>();

        await indexer.RebuildAsync(typeof(Book));
        await indexer.IndexRangeAsync(new List<Book>
        {
            new("b1", "Lucene in Action", "B001"),
            new("b2", "Lucene for .NET", "B002")
        }, true);

        await indexer.RebuildAsync(typeof(Item));
        await indexer.IndexRangeAsync(new List<Item>
        {
            new("i1", "Lucene mug", "B003"),
            new("i2", "Lucene notebook", "B004")
        }, true);

        var result = await search.SearchManyAsync(new MultiSearchInput
        {
            Entities = ["Book", "Item"],
            Query = "Lucene",
            SkipCount = 0,
            MaxResultCount = 10
        });

        Assert.Equal(2, result.TotalCount);
        Assert.Contains(result.Items, x => x.Payload["__IndexName"] == "Book" && x.Payload[nameof(Book.Code)] == "B001");
        Assert.Contains(result.Items, x => x.Payload["__IndexName"] == "Item" && x.Payload[nameof(Item.Code)] == "B003");
    }

    private record Book(string Id, string Title, string Code);

    private record SoftBook(string Id, string Title, bool IsDeleted);

    private record Item(string Id, string Name, string Code);
}


