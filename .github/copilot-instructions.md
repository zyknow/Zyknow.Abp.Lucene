# GitHub Copilot Instructions (Zyknow.Abp.Lucene)

## Project Overview
- ABP 9.x on .NET 9 with Lucene.NET 4.8.
- Capabilities: index maintenance, full‑text search, filtering, paging, optional highlighting.
- Modules: Domain.Shared (analyzers/descriptors/options/localization), Domain (index writing and directory), Application (service, event‑driven sync, filter mapping), HttpApi (controllers).

## Key Contracts and Endpoints
- Interface `ILuceneService`: `SearchAsync`, `SearchManyAsync`, `RebuildIndexAsync`, `RebuildAndIndexAllAsync`, `GetIndexDocumentCountAsync`.
- Controller routes:
  - `GET api/lucene/search/{entity}`
  - `GET api/lucene/search-many`
  - `POST api/lucene/rebuild/{entity}`
  - `POST api/lucene/rebuild-and-index/{entity}?batchSize=1000`
  - `GET api/lucene/count/{entity}`

## Configuration Essentials
- `LuceneOptions`:
  - `AnalyzerFactory` defaults to `AnalyzerFactories.IcuGeneral()`; alternatives include `EdgeNGram(min,max)` and `Keyword()`.
  - `IndexRootPath` defaults to `{AppContext.BaseDirectory}/lucene-index`.
  - `PerTenantIndex` enables tenant‑isolated index directories (true by default).
  - `LuceneQuery`: `MultiFieldMode` (OR/AND), `FuzzyMaxEdits`, `PrefixEnabled`.
  - `LuceneHighlight`: present for future tuning; current highlighting is controlled by request input (`SearchQueryInput.Highlight`).
  - Register descriptors via `ConfigureLucene(builder => builder.Entity<T>(e => e.Field(...)))`.
- Field DSL (`FieldBuilder`):
  - `Store()`, `Boost(float)`, `Name(string)`
  - `Keyword()` / `LowerCaseKeyword(culture)` (exact matching and case normalization)
  - `Autocomplete(min,max)` (combine with an NGram analyzer)
  - `StoreTermVectors(positions,offsets)` (can help advanced highlighting)
  - Numerics/dates: `AsInt32` / `AsInt64` / `AsDateEpochMillis` / `AsDateEpochSeconds`

## Filtering and LINQ Mapping
- Custom filters: implement `ILuceneFilterProvider.BuildAsync(SearchFilterContext)`, return a `Query` merged with `MUST`.
- LINQ→Lucene mapping supports equality, IN, `StartsWith` (prefix), `EndsWith`/`Contains` (wildcard), and `AndAlso`/`OrElse` composition.
- Range queries:
  - If a field declares `NumericKind`, numeric ranges are emitted; otherwise a text `TermRange` is used.
  - For text ranges, normalize values at indexing time (e.g., zero‑pad numbers, use `yyyyMMddHHmmss` for dates).

## Copilot Generation Guidelines
- Use ABP DI and modular patterns; prefer scoped/transient services and avoid manual singletons for Lucene resources.
- Build and query through `LuceneOptions` and entity descriptors; do not hardcode analyzers or field names.
- Use `ILogger<T>` for logs; implement async `Task` APIs; respect ABP paging/sorting.
- Multi‑tenancy: select index directories via `ICurrentTenant`.
- Highlighting: clients can request pieces by passing `Highlight=true`; snippets are wrapped with `<em>`. Ensure target fields are `Store()`‑enabled. Term vectors may improve quality if you extend the pipeline.
- Tests live under `test/*` with xUnit; cover index writing and query mapping.

## Common Prompt Examples
- "Add a search descriptor for entity Book: `Title` and `Author` with `Store()`, `Code` as `Keyword()`."
- "Implement a filter provider that filters by a `LibraryId` list using `LinqLucene.Where`."
- "Enable EdgeNGram autocomplete: set `AnalyzerFactory = AnalyzerFactories.EdgeNGram(1,20)` and call `Autocomplete(1,20)` on the field."

## Commits and Style
- Follow Conventional Commits (e.g., `feat: add Book search descriptor`, `fix: normalize keyword field`).
- Style: `LangVersion latest`, nullable enabled, implicit `using` on; keep naming and module boundaries consistent.