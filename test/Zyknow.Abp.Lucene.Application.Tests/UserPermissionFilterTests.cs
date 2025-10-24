using System.Linq.Expressions;
using Lucene.Net.Search;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.MultiTenancy;
using Volo.Abp.Users;
using Zyknow.Abp.Lucene.Analyzers;
using Zyknow.Abp.Lucene.Application.Tests.Fakes;
using Zyknow.Abp.Lucene.Dtos;
using Zyknow.Abp.Lucene.Filtering;
using Zyknow.Abp.Lucene.Indexing;
using Zyknow.Abp.Lucene.Options;
using Zyknow.Abp.Lucene.Services;
using System.Security.Claims;

namespace Zyknow.Abp.Lucene.Application.Tests;

public class UserPermissionFilterTests
{
    [Fact]
    public async Task Filter_By_CurrentUser_Library_Permissions_Should_Work()
    {
        var userId = Guid.NewGuid();
        var permStore = new InMemoryLibraryPermissionStore();
        permStore.Permissions.Add(new(userId, "L1"));
        permStore.Permissions.Add(new(userId, "L3"));

        var services = new ServiceCollection();
        services.Configure<LuceneOptions>(opt =>
        {
            opt.PerTenantIndex = false;
            opt.IndexRootPath = Path.Combine(Path.GetTempPath(), "lucene-index-perm-tests", Guid.NewGuid().ToString("N"));
            opt.AnalyzerFactory = AnalyzerFactories.IcuGeneral;
            opt.ConfigureLucene(model =>
            {
                model.Entity<Medium>(e =>
                {
                    e.Field(x => x.Name, f => f.Store());
                    e.Field(x => x.LibraryId, f => f.Keyword());
                });
                model.Entity<Book>(e =>
                {
                    e.Field(x => x.Title, f => f.Store());
                    e.Field(x => x.LibraryId, f => f.StoreOnly());
                });
            });
        });
        services.AddSingleton<LuceneIndexManager>();
        services.AddSingleton<ILuceneSearcherProvider, LuceneSearcherProvider>();
        services.AddSingleton<LuceneAppService>();
        services.AddSingleton<ICurrentTenant>(new FakeCurrentTenant { Id = null });
        services.AddSingleton<ICurrentUser>(new FakeCurrentUser(userId));
        services.AddSingleton(permStore);
        services.AddSingleton<ILuceneFilterProvider>(sp => new LibraryAccessFilterProvider(
            ["Medium"], permStore, sp.GetRequiredService<ICurrentUser>()));
        services.AddLogging();

        var sp = services.BuildServiceProvider();
        var indexer = sp.GetRequiredService<LuceneIndexManager>();
        var search = sp.GetRequiredService<LuceneAppService>();

        // 索引 Medium 文档（仅 L1 应返回）
        await indexer.RebuildAsync(typeof(Medium));
        await indexer.IndexRangeAsync(new List<Medium>
        {
            new("m1", "hello", "L1"),
            new("m2", "hello", "L2"),
            new("m3", "world", "L3")
        }, true);

        // 索引 Book 文档（不过滤，应返回 2 条）
        await indexer.RebuildAsync(typeof(Book));
        await indexer.IndexRangeAsync(new List<Book>
        {
            new("b1", "hello", "L1"),
            new("b2", "hello", "L2")
        }, true);

        // 对 Medium 应按权限过滤（仅 L1、L3，但查询 hello，所以 1 条）
        var mediumResult = await search.SearchAsync("Medium", new SearchQueryInput
        {
            Query = "hello",
            MaxResultCount = 10,
            SkipCount = 0
        });
        Assert.Equal(1, mediumResult.TotalCount);

        // 对 Book 不过滤（Provider 返回 null），应返回 2 条
        var bookResult = await search.SearchAsync("Book", new SearchQueryInput
        {
            Query = "hello",
            MaxResultCount = 10,
            SkipCount = 0
        });
        Assert.Equal(2, bookResult.TotalCount);
    }

    private class LibraryAccessFilterProvider(
        IEnumerable<string> mediumEntityNames,
        InMemoryLibraryPermissionStore permissionStore,
        ICurrentUser currentUser) : ILuceneFilterProvider
    {
        private readonly HashSet<string> _names = new HashSet<string>(mediumEntityNames, StringComparer.OrdinalIgnoreCase);
        private readonly ICurrentUser _currentUser = currentUser;

        public Task<Query?> BuildAsync(SearchFilterContext ctx)
        {
            if (!_names.Contains(ctx.EntityName))
            {
                return Task.FromResult<Query?>(null);
            }

            // 读取当前用户可访问的库
            var currentUserId = _currentUser.Id ?? Guid.Empty;
            var allowed = permissionStore.Permissions
                .Where(x => x.UserId == currentUserId)
                .Select(x => x.LibraryId)
                .ToHashSet();

            Expression<Func<MediumProj, bool>> exp = x => allowed.Contains(x.LibraryId);
            var q = LinqLucene.Where(ctx.Descriptor, exp);
            return Task.FromResult<Query?>(q);
        }

        private class MediumProj
        {
            public string LibraryId { get; set; } = string.Empty;
        }
    }

    private record Medium(string Id, string Name, string LibraryId);
    private record Book(string Id, string Title, string LibraryId);

    private class InMemoryLibraryPermissionStore
    {
        public List<LibraryPermission> Permissions { get; } = new();
    }

    private record LibraryPermission(Guid UserId, string LibraryId);

    private class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? Id => userId;
        public string? UserName => null;
        public string? Name => null;
        public string? SurName => null;
        public string? PhoneNumber => null;
        public bool PhoneNumberVerified => false;
        public string? Email => null;
        public bool EmailVerified => false;
        public Guid? TenantId => null;
        public bool IsAuthenticated => true;
        public string[] Roles => Array.Empty<string>();
        public DateTime? BirthDate => null;
        public string? Gender => null;
        public string? Culture => null;
        public string? TimeZone => null;
        public Claim? FindClaim(string claimType) => null;
        public Claim[] FindClaims(string claimType) => Array.Empty<Claim>();
        public Claim[] GetAllClaims() => Array.Empty<Claim>();
        public string? FindClaimValue(string claimType) => null;
        public bool IsInRole(string roleName) => false;
    }
}
