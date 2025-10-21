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

## Quick Start

The module subscribes to ABP local entity events internally and only listens to entity types registered in `ConfigureLucene`, achieving automatic index maintenance.

- Configuration example

```csharp
Configure<ZyknowLuceneOptions>(opt =>
{
    opt.IndexRootPath = Path.Combine(AppContext.BaseDirectory, "lucene-index");
    opt.PerTenantIndex = true;
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
  - Purpose: declare dependencies for `ValueField` (derived text) so the synchronizer can load only necessary columns and compute the derived text.
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

Notes:

- Fields used in filtering must be indexed (prefer `Keyword()` for exact matching).
- Field name resolution uses `Descriptor.Fields`; expression member names must match descriptor field names.
- Performance: prefer prefix/keyword filters; use broad wildcards with caution.

#### Example: filter by LibraryId

```csharp
public class MediumLuceneFilter : ILuceneFilterProvider, IScopedDependency
{
    public Task<Query?> BuildAsync(SearchFilterContext ctx)
    {
        var ids = new [] { guid1, guid2 };
        Expression<Func<MediumProj, bool>> expr = x => ids.Contains(x.LibraryId);
        return Task.FromResult(LinqLucene.Where(ctx.Descriptor, expr));
    }
    private sealed class MediumProj { public Guid LibraryId { get; set; } }
}
```

#### Range queries (TermRangeQuery)

Term-based range comparisons are lexicographical. Normalize numbers/dates at indexing time to ensure correct ordering.

- Numbers: use zero-padded fixed-width strings (e.g., 8 digits)

```csharp
// Index as zero-padded strings like "00001234"
Expression<Func<ItemProj, bool>> expr = x => x.ReadCount >= "00000010" && x.ReadCount < "00000100";
var q = LinqLucene.Where(ctx.Descriptor, expr);
```

- Dates: use a sortable format like `yyyyMMddHHmmss`

```csharp
// Index CreatedAt as "20250101000000" style strings
Expression<Func<ItemProj, bool>> expr = x => x.CreatedAt >= "20250101000000" && x.CreatedAt < "20260101000000";
var q = LinqLucene.Where(ctx.Descriptor, expr);
```

Notes:

- Prefer `Keyword()/LowerCaseKeyword()` for exact/normalized fields.
- For true numeric/date ranges, consider extending to Numeric/Points queries.

#### Case-insensitive keyword (culture)

Use `LowerCaseKeyword()` or `LowerCaseKeyword(CultureInfo)` to normalize values at index/write time and in query mapping：

```csharp
model.Entity<Tag>(e =>
{
    e.Field(x => x.Name, f => f.LowerCaseKeyword(new System.Globalization.CultureInfo("tr-TR")));
});
```

This applies lowercasing during indexing and in LINQ → Lucene mapping (Term/Prefix/Wildcard/IN) to ensure consistent matching.
