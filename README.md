# Zyknow.Abp.Lucene

[简体中文](README.zh.md) | English

An ABP module based on Lucene.NET that provides configurable full-text search capabilities for your system.

## Features

- Unified ICU general analyzer (multilingual friendly)
- Fluent API: choose indexed fields, keyword fields, boost, autocomplete
- Optional multi-tenant index isolation
- Index management: add/update/delete/rebuild
- Search service: multi-field query, paging, stored fields (Payload)

## Installation & Integration

- Add project references to `Zyknow.Abp.Lucene.*` in your host application
- Include this module in your host module's `DependsOn`
- In your EF Core layer, your DbContext must inherit `LuceneAbpDbContext<TDbContext>` so the Lucene EF change interceptor is wired automatically. Example:

```csharp
// Host application's EF Core DbContext
public class MyDbContext : LuceneAbpDbContext<MyDbContext>
{
    public MyDbContext(DbContextOptions<MyDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        // ... your entity configurations
    }
}
```

Notes:
- The base context registers an EF Core interceptor to publish entity change events used by the Lucene index synchronizer.
- If your solution replaces module DbContexts (e.g., Identity/TenantManagement), still keep your concrete DbContext deriving from `LuceneAbpDbContext<TDbContext>`.

## Quick Start

The module subscribes to ABP local entity events internally and only listens to entity types registered in `ConfigureLucene`, achieving automatic index maintenance.

- Configuration example (e.g., in your HttpApi or Web module):

```csharp
// using Zyknow.Abp.Lucene.Options;
Configure<LuceneOptions>(opt =>
{
    opt.IndexRootPath = Path.Combine(AppContext.BaseDirectory, "lucene-index");
    opt.PerTenantIndex = true; // default true
    opt.AnalyzerFactory = AnalyzerFactories.IcuGeneral;
    opt.ConfigureLucene(model =>
    {
        model.Entity<Book>(e =>
        {
            e.Field(x => x.Title, f => f.Store());
            e.Field(x => x.Author, f => f.Store());
            e.Field(x => x.Code, f => f.Keyword());
        });
    });
});
```

### HTTP API

- GET `api/lucene/search/{entity}`
- GET `api/lucene/search-many`
- POST `api/lucene/rebuild/{entity}`
- POST `api/lucene/rebuild-and-index/{entity}?batchSize=1000`
- GET `api/lucene/count/{entity}`

## Search API Response

- Both endpoints return a paged `SearchResultDto`: `TotalCount` and `Items`.
- Each hit (`SearchHitDto`) contains:
  - `EntityId`: document primary key (from each entity descriptor's `IdFieldName`)
  - `Score`: relevance score
  - `Payload`: stored field key-value pairs. In multi-entity aggregation, it also contains `Payload["__IndexName"]` to indicate the source entity index name.

Notes

- Global query behavior is affected by `LuceneOptions.LuceneQuery`:
  - `MultiFieldMode`: `AND`/`OR` (default `OR`).
  - `FuzzyMaxEdits`: edit distance for fuzzy matching (default 1).
- With multi-tenancy enabled (`PerTenantIndex`), requests search under the current tenant's index directory.

## Advanced

### Field configuration methods (FieldBuilder)

Within `EntitySearchBuilder<T>.Field(expr, configure)` or `ValueField(selector, configure)`, you can use the following methods to control how the field is indexed and retrieved:

- `Store(bool enabled = true)`

  - Purpose: whether to store the original field value in the index (for result display or further processing).
  - Default: `false`. Call `f.Store()` to enable.
  - Effect: when enabled, `LuceneSearchAppService` can read this value from the hit document and put it into the result `Payload`.

- `Name(string name)`

  - Purpose: explicitly set the field name.
  - Default: property fields use the property name; `ValueField` defaults to `"Value"`.
  - Effect: index and queries use this name, helpful for consistent display/conventions.

- `Keyword()`

  - Purpose: index as a keyword (no tokenization), suitable for identifiers, codes, exact tags, etc.
  - Default: off. When called, uses `StringField`; otherwise uses `TextField` (tokenized).

- `Autocomplete(int minGram = 1, int maxGram = 20)`

  - Purpose: declare hint parameters for autocomplete (NGram/EdgeNGram).
  - Default: not set.
  - Note: currently a descriptor-level hint; to take effect, combine with a compatible analyzer/indexing strategy (e.g., an analyzer using NGram).

- `StoreTermVectors(bool positions = true, bool offsets = true)`

  - Purpose: store term vector info (positions/offsets), often used for highlighting and other advanced features.
  - Default: not set.
  - Note: currently a configuration hint; to enable highlighting/snippet generation, combine with `TermVector` at search time or extend index writing.

- `Depends(Expression<Func<object>> member)`
  - Purpose: declare dependencies for `ValueField` so the synchronizer can load only necessary columns and compute derived text.
  - Default: not set.
  - Effect: the synchronizer prioritizes automatic projection to load only declared columns; if missing or non-projectable delegates exist, it falls back to full entity loading and logs at Info level.

#### Example configuration

```csharp
model.Entity<Book>(e =>
{
    // Property field: tokenized and store original value
    e.Field(x => x.Title, f => f.Store());

    // Keyword field: no tokenization, exact match, renamed
    e.Field(x => x.Code, f => f.Keyword().Name("BookCode"));

    // Derived field: declare dependencies to enable automatic projection
    e.ValueField(x => $"{x.Author} - {x.Title}", f =>
        f.Name("AuthorTitle").Store()
         .Depends(() => x.Author)
         .Depends(() => x.Title));
});
```

### Filter Provider & LINQ mapping

You can plug custom filters into the search pipeline via `ILuceneFilterProvider` in the Application layer:

- Interface: `Task<Query?> BuildAsync(SearchFilterContext ctx)`
- Context: `{ string EntityName, EntitySearchDescriptor Descriptor, SearchQueryInput Input }`
- Composition: the returned `Query` is added with `Occur.MUST` to the final query

A lightweight LINQ → Lucene mapping helper is provided to write filters with simple LINQ expressions and convert them into Lucene queries:

- Equality: `x => x.Field == value` → `TermQuery`
- IN: `values.Contains(x.Field)` → multiple `TermQuery` with `BooleanQuery SHOULD + MinimumNumberShouldMatch=1`
- Prefix: `x => x.Field.StartsWith("ab")` → `PrefixQuery`
- Wildcard suffix: `x => x.Field.EndsWith(".jpg")` → `WildcardQuery("*.jpg")`
- Wildcard contains: `x => x.Field.Contains("foo")` → `WildcardQuery("*foo*")`
- Boolean composition: `AndAlso` → `MUST`, `OrElse` → `SHOULD`

#### Example: filter by LibraryId list

```csharp
using System.Linq.Expressions;
using Lucene.Net.Search;
using Volo.Abp.DependencyInjection;
using Zyknow.Abp.Lucene.Filtering;

public class LibraryFilterProvider : ILuceneFilterProvider, IScopedDependency
{
    public Task<Query?> BuildAsync(SearchFilterContext ctx)
    {
        // Assume you get the list of library ids from your request or dependency
        var libraryIds = new [] { Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222") };

        Expression<Func<Project, bool>> expr = x => libraryIds.Contains(x.LibraryId);
        var query = LinqLucene.Where(ctx.Descriptor, expr);
        return Task.FromResult<Query?>(query);
    }

    private sealed class Project
    {
        public Guid LibraryId { get; set; }
    }
}
```

Tip: If `ctx.Expression` is already provided by the caller and this provider returns `null`, the pipeline will try to convert that expression to a Lucene `Query` and merge it with `MUST`.

#### Range queries (TermRangeQuery)

Term-based range comparisons are lexicographical. Normalize numbers/dates at indexing time (zero-pad, `yyyyMMddHHmmss`).

### Concurrency & UnitOfWork (important)

When you run multiple writes in parallel within the same HTTP request, not using `requiresNew` in child tasks may reuse the same UoW and cause errors.

Solution (choose one):
- Independent transaction per task (recommended): `requiresNew: true`
- Suppress execution context flow and use regular `Begin`

Parallel insert example for Book (no rebuild): see below.

```csharp
var tasks = Enumerable.Range(0, threads).Select(async t =>
{
    using var uow = uowManager.Begin(new AbpUnitOfWorkOptions { IsTransactional = true }, requiresNew: true);
    for (var i = 0; i < perThread; i++)
        await bookRepo.InsertAsync(new Book(GuidGenerator.Create(), $"Title-{t}-{i}", $"Author-{t}"), autoSave: true);
    await uow.CompleteAsync();
});
await Task.WhenAll(tasks);
```

Notes:
- If you do concurrency via background jobs/queue instead of inside a single HTTP request, each job naturally has its own scope and UoW, so no special handling is required.
- The module internally fixes the issue where inserts within a UoW could overwrite each other if IDs are not yet generated. However, UoW boundaries and concurrent transaction isolation must still be handled by the caller as shown above.
