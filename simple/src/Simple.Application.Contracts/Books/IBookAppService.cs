using System;
using System.Threading.Tasks;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;

namespace Simple.Books;

public interface IBookAppService :
    ICrudAppService< //Defines CRUD methods
        BookDto, //Used to show books
        Guid, //Primary key of the book entity
        PagedAndSortedResultRequestDto, //Used for paging/sorting
        CreateUpdateBookDto> //Used to create/update a book
{
    /// <summary>
    /// 并发 + UnitOfWork 测试 Lucene 索引一致性（仅 Book 实体）。
    /// </summary>
    /// <param name="threads">并发线程数，默认 4。</param>
    /// <param name="perThread">每线程插入数量，默认 50。</param>
    /// <param name="rebuildIndex">是否先重建 Book 索引，默认 true。</param>
    Task<LuceneConcurrentTestResultDto> ConcurrentUowLuceneTestAsync(int threads = 4, int perThread = 50, bool rebuildIndex = true);

    Task ClearAsync();
    Task<int> GetIndexCountAsync();
}