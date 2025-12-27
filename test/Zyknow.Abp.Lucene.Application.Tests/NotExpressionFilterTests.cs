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

namespace Zyknow.Abp.Lucene.Application.Tests;

public class NotExpressionFilterTests
{
    [Fact]
    public async Task Not_Boolean_Field_In_Filter_Should_Work()
    {
        var userId = Guid.NewGuid();

        // allowed libraries for current user
        var permStore = new InMemoryLibraryPermissionStore();
        permStore.Permissions.Add(new(userId, "L1"));
        permStore.Permissions.Add(new(userId, "L3"));

        var services = new ServiceCollection();
        services.Configure<LuceneOptions>(opt =>
        {
            opt.PerTenantIndex = false;
            opt.IndexRootPath = Path.Combine(Path.GetTempPath(), "lucene-index-not-tests", Guid.NewGuid().ToString("N"));
            opt.AnalyzerFactory = AnalyzerFactories.IcuGeneral;
            opt.ConfigureLucene(model =>
            {
                model.Entity<Medium>(e =>
                {
                    e.Field(x => x.Name, f => f.Store());
                    e.Field(x => x.LibraryId, f => f.Keyword());
                    e.Field(x => x.IsMissing, f => f.Keyword());
                });
            });
        });

        services.AddSingleton<LuceneIndexManager>();
        services.AddSingleton<ILuceneSearcherProvider, LuceneSearcherProvider>();
        services.AddSingleton<LuceneAppService>();
        services.AddSingleton<ICurrentTenant>(new FakeCurrentTenant { Id = null });
        services.AddSingleton<ICurrentUser>(new FakeCurrentUser(userId));
        services.AddSingleton(permStore);
        services.AddSingleton<ILuceneFilterProvider>(sp => new MissingAwareLibraryAccessProvider(
            permStore, sp.GetRequiredService<ICurrentUser>()));
        services.AddLogging();

        var sp = services.BuildServiceProvider();
        var indexer = sp.GetRequiredService<LuceneIndexManager>();
        var search = sp.GetRequiredService<LuceneAppService>();

        await indexer.RebuildAsync(typeof(Medium));
        await indexer.IndexRangeAsync(new List<Medium>
        {
            new("m1", "hello", "L1", IsMissing: false), // should pass
            new("m2", "hello", "L1", IsMissing: true),  // blocked by !IsMissing
            new("m3", "hello", "L2", IsMissing: false), // blocked by library permission
            new("m4", "world", "L3", IsMissing: false)  // pass permission, but query != hello
        }, true);

        var result = await search.SearchAsync("Medium", new SearchQueryInput
        {
            Query = "hello",
            MaxResultCount = 10,
            SkipCount = 0
        });

        Assert.Equal(1, result.TotalCount);
    }

    private class MissingAwareLibraryAccessProvider(
        InMemoryLibraryPermissionStore permissionStore,
        ICurrentUser currentUser) : ILuceneFilterProvider
    {
        private readonly ICurrentUser _currentUser = currentUser;

        public Task<Query?> BuildAsync(SearchFilterContext ctx)
        {
            if (!string.Equals(ctx.EntityName, "Medium", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<Query?>(null);
            }

            var currentUserId = _currentUser.Id ?? Guid.Empty;
            var allowed = permissionStore.Permissions
                .Where(x => x.UserId == currentUserId)
                .Select(x => x.LibraryId)
                .ToList();

            Expression<Func<MediumProj, bool>> exp = x => allowed.Contains(x.LibraryId) && !x.IsMissing;
            return Task.FromResult<Query?>(LinqLucene.Where(ctx.Descriptor, exp));
        }

        private class MediumProj
        {
            public string LibraryId { get; set; } = string.Empty;
            public bool IsMissing { get; set; }
        }
    }

    private record Medium(string Id, string Name, string LibraryId, bool IsMissing);

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
        public System.Security.Claims.Claim? FindClaim(string claimType) => null;
        public System.Security.Claims.Claim[] FindClaims(string claimType) => Array.Empty<System.Security.Claims.Claim>();
        public System.Security.Claims.Claim[] GetAllClaims() => Array.Empty<System.Security.Claims.Claim>();
        public string? FindClaimValue(string claimType) => null;
        public bool IsInRole(string roleName) => false;
    }
}


