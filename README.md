# Zyknow.Abp.Lucene

[简体中文](README.zh.md) | English

An ABP module integrating Lucene.NET to provide configurable full-text search, indexing, filtering and highlighting.

## Features

- **Fluent schema (ConfigureLucene)**: declare per-entity indexed fields, keyword fields, numeric/date fields, boost, stored payload fields, derived fields (ValueField), etc.
- **Index management**: upsert/delete/rebuild, delete-by-field, count.
- **Automatic indexing (EF Core)**: capture SaveChanges changes and flush to Lucene on UoW completion.
- **Multi-tenant index isolation** (`PerTenantIndex`).
- **Search APIs**:
  - Single index search (`/api/lucene/search/{entity}`)
  - Multi-index aggregation search (`/api/lucene/search-many`)
  - Multi-field query + paging + highlight snippets
- **Filtering**:
  - Plug-in filters via `ILuceneFilterProvider` (Application layer)
  - **Default forced filters configured in `ConfigureLucene`** via `ForceFilter(...)` (always applied as `MUST`, and supports DI via `IServiceProvider`)
- **Index exclusion rules**:
  - Configure `ExcludeFromIndexWhen(...)` to prevent indexing certain entities (and try to delete old docs by id when an entity becomes excluded)

## Compatibility

- **Package version**: `10.0.0-preview.2`
- **Target framework**: `net10.0`
- **ABP**: `10.0.0`
- **Lucene.NET**: `4.8.0-beta00017`

## Installation

You can reference projects directly, or use NuGet packages (recommended in host applications).

Typical host layers:

- **Domain / Application only**: reference `Zyknow.Abp.Lucene.Application`
- **EF Core integration**: reference `Zyknow.Abp.Lucene.EntityFrameworkCore`
- **HTTP endpoints**: reference `Zyknow.Abp.Lucene.HttpApi`

Example (NuGet):

```bash
dotnet add package Zyknow.Abp.Lucene.HttpApi --version 10.0.0-preview.2
dotnet add package Zyknow.Abp.Lucene.EntityFrameworkCore --version 10.0.0-preview.2
```

## Integration (ABP modules)

- Add the module(s) to your host module `DependsOn`:
  - `ZyknowLuceneApplicationModule`
  - `ZyknowLuceneEntityFrameworkCoreModule` (if using EF Core auto indexing)
  - `ZyknowLuceneHttpApiModule` (if exposing REST endpoints)

## EF Core auto indexing (recommended)

If you want automatic indexing based on EF Core `SaveChanges`, your DbContext should inherit `LuceneAbpDbContext<TDbContext>` so the EF interceptor is wired automatically:

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

- The base context adds `LuceneEfChangeInterceptor` via `DbContextOptionsBuilder.AddInterceptors(...)`.
- Auto indexing is controlled by `LuceneOptions.EnableAutoIndexingEvents` (default `true`).

## Quick Start

The module only indexes entity types registered in `LuceneOptions.ConfigureLucene(...)`.

### 1) Configure Lucene options

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
            e.Field(x => x.Code, f => f.Keyword().Store()); // store if you want it in Payload
        });
    });
});
```

### 2) Query via HTTP API

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
  - `Payload`: stored field key-value pairs. In multi-entity aggregation, it also contains `Payload["__IndexName"]` to indicate the source index name.

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

### Default forced filters (ConfigureLucene)

If you prefer to centralize filters together with the entity schema, you can use `ForceFilter(...)`.

- Always applied as `Occur.MUST`
- Receives `IServiceProvider`, so you can resolve dependencies (e.g., `ICurrentUser`, permission stores, tenant providers, etc.)

Example: force filter by allowed LibraryId list and exclude missing entries:

```csharp
using Lucene.Net.Search;
using Zyknow.Abp.Lucene.Filtering;

model.Entity<Medium>(e =>
{
    // Filterable fields must be searchable (e.g., Keyword()).
    e.Field(x => x.LibraryId, f => f.Keyword());
    e.Field(x => x.IsMissing, f => f.Keyword());

    e.ForceFilter<SearchQueryInput>((sp, ctx) =>
    {
        var permissionStore = sp.GetRequiredService<IMyLibraryPermissionService>();
        var allowedIds = permissionStore.GetAllowedLibraryIds();

        var must = new BooleanQuery
        {
            { LuceneQueries.In(nameof(Medium.LibraryId), allowedIds), Occur.MUST },
            // if you index bool as string, use "False"/"True" accordingly
            { LuceneQueries.Term(nameof(Medium.IsMissing), "False"), Occur.MUST }
        };
        return must;
    });
});
```

### Index exclusion rules (ConfigureLucene)

Use `ExcludeFromIndexWhen(...)` to skip indexing certain entities (and try to delete any existing doc by id when an entity becomes excluded):

```csharp
model.Entity<Medium>(e =>
{
    e.Field(x => x.Title, f => f.Store());
    e.ExcludeFromIndexWhen((sp, x) => x.IsDeleted);
});
```

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
