using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.MultiTenancy;
using Zyknow.Abp.Lucene.Analyzers;
using Zyknow.Abp.Lucene.Application.Tests.Fakes;
using Zyknow.Abp.Lucene.Dtos;
using Zyknow.Abp.Lucene.Indexing;
using Zyknow.Abp.Lucene.Options;
using Zyknow.Abp.Lucene.Services;

namespace Zyknow.Abp.Lucene.Application.Tests;

public class DeleteByFieldTests
{
    [Fact]
    public async Task DeleteByField_SingleValue_Should_Remove_Matching_Documents()
    {
        var services = new ServiceCollection();
        var indexName = "Book_DeleteByField_Single";
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
                }, indexName);
            });
        });
        services.AddSingleton<LuceneIndexManager>();
        // 新增：注册 SearcherProvider 以供 LuceneAppService 查询使用
        services.AddSingleton<ILuceneSearcherProvider, LuceneSearcherProvider>();
        services.AddSingleton<LuceneAppService>();
        services.AddSingleton<ICurrentTenant>(new FakeCurrentTenant { Id = null });
        services.AddLogging();

        var sp = services.BuildServiceProvider();
        var indexer = sp.GetRequiredService<LuceneIndexManager>();
        var search = sp.GetRequiredService<LuceneAppService>();

        await indexer.RebuildAsync(typeof(Book));
        await indexer.IndexRangeAsync(new List<Book>
        {
            new("1", "Lucene in Action", "B001"),
            new("2", "Pro .NET Lucene", "B002"),
            new("3", "Another Lucene Book", "B003")
        }, true);

        var before = await search.SearchAsync(indexName, new SearchQueryInput
        {
            Query = "Lucene",
            MaxResultCount = 10,
            SkipCount = 0
        });
        Assert.Equal(3, before.TotalCount);

        await indexer.DeleteByFieldAsync<Book>("Code", "B002");

        var after = await search.SearchAsync(indexName, new SearchQueryInput
        {
            Query = "Lucene",
            MaxResultCount = 10,
            SkipCount = 0
        });
        Assert.Equal(2, after.TotalCount);
    }

    [Fact]
    public async Task DeleteByField_MultipleValues_Should_Remove_All_Matches()
    {
        var services = new ServiceCollection();
        var indexName2 = "Book_DeleteByField_Multi";
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
                }, indexName2);
            });
        });
        services.AddSingleton<LuceneIndexManager>();
        // 新增：注册 SearcherProvider 以供 LuceneAppService 查询使用
        services.AddSingleton<ILuceneSearcherProvider, LuceneSearcherProvider>();
        services.AddSingleton<LuceneAppService>();
        services.AddSingleton<ICurrentTenant>(new FakeCurrentTenant { Id = null });
        services.AddLogging();

        var sp = services.BuildServiceProvider();
        var indexer = sp.GetRequiredService<LuceneIndexManager>();
        var search = sp.GetRequiredService<LuceneAppService>();

        await indexer.RebuildAsync(typeof(Book));
        await indexer.IndexRangeAsync(new List<Book>
        {
            new("1", "Lucene in Action", "B001"),
            new("2", "Pro .NET Lucene", "B002"),
            new("3", "Another Lucene Book", "B003")
        }, true);

        var before = await search.SearchAsync(indexName2, new SearchQueryInput
        {
            Query = "Lucene",
            MaxResultCount = 10,
            SkipCount = 0
        });
        Assert.Equal(3, before.TotalCount);

        await indexer.DeleteByFieldAsync<Book>("Code", new object[] { "B001", "B003" });

        var after = await search.SearchAsync(indexName2, new SearchQueryInput
        {
            Query = "Lucene",
            MaxResultCount = 10,
            SkipCount = 0
        });
        Assert.Equal(1, after.TotalCount);
    }

    [Fact]
    public async Task Delete_By_Field_Should_Reduce_Count()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentTenant>(new FakeCurrentTenant { Id = null });
        services.Configure<LuceneOptions>(opt =>
        {
            opt.PerTenantIndex = false;
            opt.IndexRootPath = Path.Combine(Path.GetTempPath(), "lucene-index-test-del");
            opt.AnalyzerFactory = AnalyzerFactories.IcuGeneral;
            opt.ConfigureLucene(model =>
            {
                model.Entity<Book>(e =>
                {
                    e.Field(x => x.Title, f => f.Store());
                    e.Field(x => x.Code, f => f.Keyword());
                });
            });
        });
        services.AddSingleton<LuceneIndexManager>();
        services.AddSingleton<ILuceneSearcherProvider, LuceneSearcherProvider>();
        services.AddSingleton<LuceneAppService>();

        var sp = services.BuildServiceProvider();
        var indexer = sp.GetRequiredService<LuceneIndexManager>();
        var app = sp.GetRequiredService<LuceneAppService>();

        await indexer.RebuildAsync(typeof(Book));
        var docs = Enumerable.Range(0, 10).Select(i => new Book(i.ToString(), $"T-{i}", $"C{i:0000}")).ToList();
        await indexer.IndexRangeAsync(docs, replace: false);

        var before = await app.GetIndexDocumentCountAsync("Book");
        Assert.Equal(10, before);

        await indexer.DeleteByFieldAsync<Book>(nameof(Book.Code), docs.Take(3).Select(d => d.Code));

        var after = await app.GetIndexDocumentCountAsync("Book");
        Assert.Equal(7, after);
    }

    private record Book(string Id, string Title, string Code);
}
