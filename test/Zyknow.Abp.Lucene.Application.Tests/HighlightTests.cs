using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.MultiTenancy;
using Zyknow.Abp.Lucene.Analyzers;
using Zyknow.Abp.Lucene.Application.Tests.Fakes;
using Zyknow.Abp.Lucene.Options;
using Zyknow.Abp.Lucene.Services;
using Zyknow.Abp.Lucene.Dtos;

namespace Zyknow.Abp.Lucene.Application.Tests;

public class HighlightTests
{
    [Fact]
    public async Task Single_Search_Should_Return_Highlights_When_Enabled()
    {
        var (sp, indexer, search) = Build();

        var books = new List<Book>
        {
            new("1", "Lucene in Action", "Erik Hatcher", "B001"),
            new("2", ".NET with Lucene", "John Doe", "B002")
        };

        await indexer.RebuildAsync(typeof(Book));
        await indexer.IndexRangeAsync(books, true);

        var result = await search.SearchAsync("Book", new SearchQueryInput
        {
            Query = "Lucene",
            Highlight = true,
            SkipCount = 0,
            MaxResultCount = 10
        });

        Assert.True(result.TotalCount >= 1);
        Assert.Contains(result.Items, i =>
            i.Highlights.TryGetValue("Title", out var frags) && frags.Count > 0 &&
            string.Join(' ', frags).Contains("<em>", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Single_Search_Should_Not_Return_Highlights_When_Disabled()
    {
        var (sp, indexer, search) = Build();

        var books = new List<Book>
        {
            new("1", "Lucene in Action", "Erik Hatcher", "B001"),
            new("2", "Pro .NET Lucene", "John Doe", "B002")
        };

        await indexer.RebuildAsync(typeof(Book));
        await indexer.IndexRangeAsync(books, true);

        var result = await search.SearchAsync("Book", new SearchQueryInput
        {
            Query = "Lucene",
            Highlight = false,
            SkipCount = 0,
            MaxResultCount = 10
        });

        Assert.All(result.Items, i => Assert.True(i.Highlights.Count == 0));
    }

    [Fact]
    public async Task Multi_Search_Should_Return_Highlights_For_Stored_Fields()
    {
        var (sp, indexer, search) = Build();

        var books = new List<Book>
        {
            new("1", "Lucene in Action", "Erik Hatcher", "B001"),
            new("2", ".NET with Lucene", "John Doe", "B002")
        };
        var items = new List<Item>
        {
            new("i1", "Lucene-branded mug", "B001"),
            new("i2", "Notebook", "B003")
        };

        await indexer.RebuildAsync(typeof(Book));
        await indexer.IndexRangeAsync(books, true);
        await indexer.RebuildAsync(typeof(Item));
        await indexer.IndexRangeAsync(items, true);

        var result = await search.SearchManyAsync(new MultiSearchInput
        {
            Entities = ["Book", "Item"],
            Query = "Lucene",
            Highlight = true,
            SkipCount = 0,
            MaxResultCount = 10
        });

        Assert.True(result.TotalCount >= 1);
        Assert.Contains(result.Items, i => i.Highlights.Count > 0);
        // 至少一个字段包含 <em>
        Assert.Contains(result.Items, i => string.Join(' ', i.Highlights.SelectMany(kv => kv.Value))
            .Contains("<em>", StringComparison.OrdinalIgnoreCase));
    }

    private static (ServiceProvider sp, LuceneIndexManager indexer, LuceneAppService search) Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentTenant>(new FakeCurrentTenant { Id = null });
        services.Configure<LuceneOptions>(opt =>
        {
            opt.PerTenantIndex = false;
            opt.IndexRootPath = Path.Combine(Path.GetTempPath(), "lucene-index-test-highlights");
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
        services.AddSingleton<LuceneAppService>();

        var sp = services.BuildServiceProvider();
        var indexer = sp.GetRequiredService<LuceneIndexManager>();
        var search = sp.GetRequiredService<LuceneAppService>();
        return (sp, indexer, search);
    }

    private record Book(string Id, string Title, string Author, string Code);
    private record Item(string Id, string Name, string Code);
}

