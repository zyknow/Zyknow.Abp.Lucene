using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.MultiTenancy;
using Zyknow.Abp.Lucene.Analyzers;
using Zyknow.Abp.Lucene.Application.Tests.Fakes;
using Zyknow.Abp.Lucene.Indexing;
using Zyknow.Abp.Lucene.Options;
using Zyknow.Abp.Lucene.Services;

namespace Zyknow.Abp.Lucene.Application.Tests;

public class ConcurrentIndexingTests
{
    private static string CreateIsolatedIndexRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "lucene-index-concurrent-tests", Guid.NewGuid().ToString("N"));
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
            });
        });
        services.AddSingleton<LuceneIndexManager>();
        services.AddSingleton<LuceneAppService>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Parallel_Add_Update_Delete_Should_Keep_Correct_Document_Count()
    {
        // Arrange
        var indexRoot = CreateIsolatedIndexRoot();
        using var sp = BuildServiceProvider(indexRoot);
        var indexer = sp.GetRequiredService<LuceneIndexManager>();
        var app = sp.GetRequiredService<LuceneAppService>();

        await indexer.RebuildAsync(typeof(Book));

        var totalAdds = 200;
        var deleteCount = 40; // 删除前 40 个
        var updateCount = 60; // 更新前 60 个

        var books = Enumerable.Range(0, totalAdds)
            .Select(i => new Book(i.ToString(), $"Title-{i}", "Author-0", $"C{i:0000}"))
            .ToList();

        var toDelete = books.Take(deleteCount).Select(b => b.Id).ToList();
        var toUpdate = books.Take(updateCount)
            .Select(b => new Book(b.Id, b.Title + "-v2", b.Author, b.Code))
            .ToList();

        // Phase 1: 并发新增（分片批量），确保全部写入完成
        var addChunks = books
            .Select((b, i) => new { b, i })
            .GroupBy(x => x.i % Math.Max(2, Environment.ProcessorCount))
            .Select(g => g.Select(x => x.b).ToList())
            .ToList();

        var addTasks = addChunks
            .Select(chunk => Task.Run(async () => { await indexer.IndexRangeAsync(chunk, replace: false); }))
            .ToArray();
        await Task.WhenAll(addTasks);

        // Phase 2: 并发更新（单条），不改变数量
        var updateTasks = toUpdate
            .Select(b => Task.Run(async () => { await indexer.IndexAsync(b); }))
            .ToArray();
        await Task.WhenAll(updateTasks);

        // Phase 3: 并发删除（单条），最终数量应减少 deleteCount
        var deleteTasks = toDelete
            .Select(id => Task.Run(async () => { await indexer.DeleteAsync<Book>(id); }))
            .ToArray();
        await Task.WhenAll(deleteTasks);

        // Assert: 期望文档数 = 新增总数 - 删除数（更新不改变数量）
        var expected = totalAdds - deleteCount;
        var count = await app.GetIndexDocumentCountAsync("Book");

        Assert.Equal(expected, count);
    }

    private record Book(string Id, string Title, string Author, string Code);
}
