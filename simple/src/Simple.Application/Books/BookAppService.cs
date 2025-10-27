using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Simple.Permissions;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using System.Linq.Dynamic.Core;
using Volo.Abp.Uow;
using Zyknow.Abp.Lucene;

namespace Simple.Books;

[Authorize(SimplePermissions.Books.Default)]
public class BookAppService : ApplicationService, IBookAppService
{
    private readonly IRepository<Book, Guid> _repository;
    private readonly ILuceneService _luceneService;
    private readonly IServiceScopeFactory _scopeFactory;

    public BookAppService(IRepository<Book, Guid> repository, ILuceneService luceneService,
        IServiceScopeFactory scopeFactory)
    {
        _repository = repository;
        _luceneService = luceneService;
        _scopeFactory = scopeFactory;
    }

    public async Task<BookDto> GetAsync(Guid id)
    {
        var book = await _repository.GetAsync(id);
        return ObjectMapper.Map<Book, BookDto>(book);
    }

    public async Task<PagedResultDto<BookDto>> GetListAsync(PagedAndSortedResultRequestDto input)
    {
        var queryable = await _repository.GetQueryableAsync();
        var query = queryable
            .OrderBy(input.Sorting.IsNullOrWhiteSpace() ? "Name" : input.Sorting)
            .Skip(input.SkipCount)
            .Take(input.MaxResultCount);

        var books = await AsyncExecuter.ToListAsync(query);
        var totalCount = await AsyncExecuter.CountAsync(queryable);

        return new PagedResultDto<BookDto>(
            totalCount,
            ObjectMapper.Map<List<Book>, List<BookDto>>(books)
        );
    }

    [Authorize(SimplePermissions.Books.Create)]
    public async Task<BookDto> CreateAsync(CreateUpdateBookDto input)
    {
        var book = ObjectMapper.Map<CreateUpdateBookDto, Book>(input);
        await _repository.InsertAsync(book);
        return ObjectMapper.Map<Book, BookDto>(book);
    }

    [Authorize(SimplePermissions.Books.Edit)]
    public async Task<BookDto> UpdateAsync(Guid id, CreateUpdateBookDto input)
    {
        var book = await _repository.GetAsync(id);
        ObjectMapper.Map(input, book);
        await _repository.UpdateAsync(book);
        return ObjectMapper.Map<Book, BookDto>(book);
    }

    [Authorize(SimplePermissions.Books.Delete)]
    public async Task DeleteAsync(Guid id)
    {
        await _repository.DeleteAsync(id);
    }

    public async Task<int> GetIndexCountAsync()
    {
        var l  = LazyServiceProvider.LazyGetRequiredService<ILuceneService>();
        return await l.GetIndexDocumentCountAsync("book");
    }

    public async Task ClearAsync()
    {
        var books = await _repository.GetListAsync();
        await _repository.DeleteManyAsync(books);
    }
    
    /// <summary>
    /// 并发 + UnitOfWork 测试 Lucene 索引一致性（仅 Book）。
    /// </summary>
    public async Task<LuceneConcurrentTestResultDto> ConcurrentUowLuceneTestAsync(int threads = 4, int perThread = 50,
        bool rebuildIndex = true)
    {
        var result = new LuceneConcurrentTestResultDto();
        var errors = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 参数校验与限制
        threads = threads <= 0 ? 4 : Math.Min(threads, 32);
        perThread = perThread <= 0 ? 50 : Math.Min(perThread, 10000);

        try
        {
            if (rebuildIndex)
            {
                await _luceneService.RebuildIndexAsync("Book");
            }

            var tasks = new List<Task>();
            for (var i = 0; i < threads; i++)
            {
                var shard = i;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var repo = scope.ServiceProvider.GetRequiredService<IRepository<Book, Guid>>();
                        var uowMgr = scope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();

                        using var uow = uowMgr.Begin(new AbpUnitOfWorkOptions
                        {
                            IsTransactional = true
                        }, requiresNew: true);

                        List<Book> books = [];

                        for (var j = 0; j < perThread; j++)
                        {
                            var book = new Book
                            {
                                Name = $"Book-{shard}-{j}-{Guid.NewGuid():N}",
                                Type = BookType.Science,
                                PublishDate = Clock.Now,
                                Price = (float)(shard * 1000 + j)
                            };
                            books.Add(book);
                        }

                        await repo.InsertManyAsync(books);

                        await uow.CompleteAsync();
                    }
                    catch (Exception ex)
                    {
                        lock (errors)
                        {
                            errors.Add($"Thread-{shard} failed: {ex.Message}");
                        }
                    }
                }));
            }

            await Task.WhenAll(tasks);

            // 统计 DB 与索引数量
            var dbCount = await _repository.GetCountAsync();
            var idxCount = await _luceneService.GetIndexDocumentCountAsync("Book");

            result = new LuceneConcurrentTestResultDto
            {
                Threads = threads,
                PerThread = perThread,
                Inserted = threads * perThread,
                DbCount = dbCount,
                IndexCount = idxCount,
                Errors = errors,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            result.Errors = errors;
        }
        finally
        {
            sw.Stop();
        }

        return result;
    }
}