# Zyknow.Abp.Lucene

[English](README.md) | 简体中文

一个基于 Lucene.NET 的 ABP 模块，用于为系统提供可配置的全文搜索、索引、过滤与高亮能力。

## 特性

- **Fluent Schema（ConfigureLucene）**：按实体配置参与索引的字段（分词字段/关键字字段/数值日期字段）、权重、返回字段（Store）、派生字段（ValueField）等。
- **索引管理**：Upsert/Delete/Rebuild、按字段删除（DeleteByField）、文档数统计。
- **EF Core 自动索引**：捕获 `SaveChanges` 的增删改，聚合到 UoW，并在事务提交后统一刷新到 Lucene。
- **多租户索引隔离**（`PerTenantIndex`）。
- **搜索能力**：
  - 单索引搜索：`/api/lucene/search/{entity}`
  - 多索引聚合搜索：`/api/lucene/search-many`
  - 多字段查询 + 分页 + 高亮片段
- **过滤体系**：
  - Application 层可用 `ILuceneFilterProvider` 插件化扩展过滤
  - **可在 `ConfigureLucene` 中集中配置“默认强制过滤”**：`ForceFilter(...)`（每次查询必定以 `MUST` 叠加，并提供 `IServiceProvider` 支持依赖注入）
- **索引剔除规则**：
  - `ExcludeFromIndexWhen(...)`：命中规则则不写入索引；当实体从“可索引”变为“剔除”时，会尽量按 Id 删除旧文档，避免索引残留

## 兼容性

- **目标包版本**：`10.0.0-preview.2`
- **目标框架**：`net10.0`
- **ABP**：`10.0.0`
- **Lucene.NET**：`4.8.0-beta00017`

## 安装

你可以直接项目引用，也可以通过 NuGet 引用（宿主应用推荐 NuGet）。

典型宿主层级：

- **仅使用领域/应用能力**：引用 `Zyknow.Abp.Lucene.Application`
- **启用 EF Core 自动索引**：引用 `Zyknow.Abp.Lucene.EntityFrameworkCore`
- **启用 HTTP 接口**：引用 `Zyknow.Abp.Lucene.HttpApi`

示例（NuGet）：

```bash
dotnet add package Zyknow.Abp.Lucene.HttpApi --version 10.0.0-preview.2
dotnet add package Zyknow.Abp.Lucene.EntityFrameworkCore --version 10.0.0-preview.2
```

## 集成（ABP 模块）

- 在宿主模块 `DependsOn` 中加入对应模块：
  - `ZyknowLuceneApplicationModule`
  - `ZyknowLuceneEntityFrameworkCoreModule`（需要 EF Core 自动索引时）
  - `ZyknowLuceneHttpApiModule`（需要 REST 接口时）

## EF Core 自动索引（推荐）

如果你希望基于 EF Core 的 `SaveChanges` 自动维护索引，你的 DbContext 应继承 `LuceneAbpDbContext<TDbContext>`，以便自动注入 Lucene 的 EF 变更拦截器：

```csharp
// 宿主应用的 EF Core DbContext
public class MyDbContext : LuceneAbpDbContext<MyDbContext>
{
    public MyDbContext(DbContextOptions<MyDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        // ... 你的实体配置
    }
}
```

说明：

- 基类通过 `DbContextOptionsBuilder.AddInterceptors(...)` 注入 `LuceneEfChangeInterceptor`。
- 自动索引开关：`LuceneOptions.EnableAutoIndexingEvents`（默认 `true`）。

## 快速上手

模块只会索引在 `LuceneOptions.ConfigureLucene(...)` 中注册的实体类型。

### 1) 配置 LuceneOptions

```csharp
// using Zyknow.Abp.Lucene.Options;
Configure<LuceneOptions>(opt =>
{
    opt.IndexRootPath = Path.Combine(AppContext.BaseDirectory, "lucene-index");
    opt.PerTenantIndex = true; // 默认 true
    opt.AnalyzerFactory = AnalyzerFactories.IcuGeneral;
    opt.ConfigureLucene(model =>
    {
        model.Entity<Book>(e =>
        {
            e.Field(x => x.Title, f => f.Store());
            e.Field(x => x.Author, f => f.Store());
            e.Field(x => x.Code, f => f.Keyword().Store()); // 如果希望在 Payload 里返回，记得 Store()
        });
    });
});
```

### 2) HTTP API

- GET `api/lucene/search/{entity}`
- GET `api/lucene/search-many`
- POST `api/lucene/rebuild/{entity}`
- POST `api/lucene/rebuild-and-index/{entity}?batchSize=1000`
- GET `api/lucene/count/{entity}`

## 搜索接口返回值

- 两个端点均返回 `SearchResultDto`（分页）：`TotalCount` 与 `Items`。
- 命中项 `SearchHitDto` 包含：
  - `EntityId`：文档主键（来自各实体描述器的 `IdFieldName`）
  - `Score`：相关性分值
  - `Payload`：存储字段键值对。多实体聚合下还包含 `Payload["__IndexName"]` 用于标识文档来源索引名。

注意

- 全局查询行为受 `LuceneOptions.LuceneQuery` 影响：
  - `MultiFieldMode`：`AND`/`OR`（默认为 `OR`）。
  - `FuzzyMaxEdits`：模糊查询编辑距离（默认 1）。
- 若启用了多租户（`PerTenantIndex`），请求在当前租户上下文内检索对应租户的索引目录。

## 高级

### 字段配置方法详解（FieldBuilder）

在 `EntitySearchBuilder<T>.Field(expr, configure)` 或 `ValueField(selector, configure)` 的 `configure` 委托中，可使用以下方法配置该字段的写入与检索行为：

- `Store(bool enabled = true)`

  - 作用：是否在索引中“存储”字段原始值（用于搜索结果的展示或二次处理）。
  - 默认：`false`。调用 `f.Store()` 即开启。
  - 影响：开启后，`LuceneSearchAppService` 能从命中文档取出该字段值并放入结果的 `Payload`。

- `Name(string name)`

  - 作用：显式设置字段名。
  - 默认：属性字段使用属性名，`ValueField` 默认名为 `"Value"`。
  - 影响：索引与查询都以此名称为准，便于统一展示或约定。

- `Keyword()`

  - 作用：将字段按“关键字”索引（不分词），适合精确匹配的标识符、代码、标签等。
  - 默认：关闭。调用 `f.Keyword()` 使用 `StringField` 写入；否则使用 `TextField` 写入（参与分词）。

- `Autocomplete(int minGram = 1, int maxGram = 20)`

  - 作用：声明该字段用于自动补全的提示参数（NGram/EdgeNGram）。
  - 默认：不设置。
  - 说明：目前作为描述符层面的提示参数；要生效需配合兼容分词器/索引策略（如使用 NGram 的 `AnalyzerFactory`）。

- `StoreTermVectors(bool positions = true, bool offsets = true)`

  - 作用：存储词条向量信息（positions/offsets），常用于高亮等高级功能。
  - 默认：不设置。

- `Depends(Expression<Func<object>> member)`
  - 作用：为 `ValueField` 声明依赖的实体属性列，使同步器仅加载必要列并计算派生文本。

#### 配置示例

```csharp
model.Entity<Book>(e =>
{
    e.Field(x => x.Title, f => f.Store());
    e.Field(x => x.Code, f => f.Keyword().Name("BookCode"));
    e.ValueField(x => $"{x.Author} - {x.Title}", f =>
        f.Name("AuthorTitle").Store()
         .Depends(() => x.Author)
         .Depends(() => x.Title));
});
```

### 过滤扩展（FilterProvider）与 LINQ 映射

- 接口：`Task<Query?> BuildAsync(SearchFilterContext ctx)`
- 上下文：`{ string EntityName, EntitySearchDescriptor Descriptor, SearchQueryInput Input }`
- 返回的 `Query` 以 `Occur.MUST` 合并到最终查询

LINQ → Lucene 映射支持：等值、IN、StartsWith（前缀）、EndsWith/Contains（通配）、AndAlso/OrElse 组合。

#### 示例：按 LibraryId 列表过滤

```csharp
using System.Linq.Expressions;
using Lucene.Net.Search;
using Volo.Abp.DependencyInjection;
using Zyknow.Abp.Lucene.Filtering;

public class LibraryFilterProvider : ILuceneFilterProvider, IScopedDependency
{
    public Task<Query?> BuildAsync(SearchFilterContext ctx)
    {
        // 假设从调用方注入或上下文获取要过滤的 LibraryId 集合
        var libraryIds = new [] { Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222") };

        Expression<Func<Project, bool>> expr = x => libraryIds.Contains(x.LibraryId);
        // 将 LINQ 表达式映射为 Lucene Query（字段名需在实体描述器中已配置）
        var query = LinqLucene.Where(ctx.Descriptor, expr);
        return Task.FromResult<Query?>(query);
    }

    private sealed class Project
    {
        public Guid LibraryId { get; set; }
    }
}
```

提示：若调用端已通过 `ctx.Expression` 提供了表达式，而此 Provider 返回 `null`，管线会尝试自动将 `ctx.Expression` 转换为 Lucene Query 并合并（MUST）。

### 默认强制过滤（在 ConfigureLucene 中集中配置）

如果你希望把过滤规则和 Schema 放在一起集中配置，可以使用 `ForceFilter(...)`：

- **必定生效**：每次查询都会以 `Occur.MUST` 叠加
- **支持依赖注入**：回调会拿到 `IServiceProvider`，可解析 `ICurrentUser`、权限仓储、租户信息等依赖

示例：按允许的 `LibraryId` 列表过滤，并默认过滤掉缺失条目 `IsMissing==false`：

```csharp
using Lucene.Net.Search;
using Zyknow.Abp.Lucene.Filtering;

model.Entity<Medium>(e =>
{
    // 注意：要参与过滤的字段必须“可搜索”，例如 Keyword()；
    // 仅 StoreOnly() 是不可搜索的，无法用于过滤。
    e.Field(x => x.LibraryId, f => f.Keyword());
    e.Field(x => x.IsMissing, f => f.Keyword());

    e.ForceFilter<SearchQueryInput>((sp, ctx) =>
    {
        var permissionService = sp.GetRequiredService<IMyLibraryPermissionService>();
        var allowedIds = permissionService.GetAllowedLibraryIds();

        var must = new BooleanQuery
        {
            { LuceneQueries.In(nameof(Medium.LibraryId), allowedIds), Occur.MUST },
            // bool 会按 ToString() 写入，通常是 "True"/"False"
            { LuceneQueries.Term(nameof(Medium.IsMissing), "False"), Occur.MUST }
        };
        return must;
    });
});
```

### 索引剔除规则（在 ConfigureLucene 中集中配置）

使用 `ExcludeFromIndexWhen(...)` 配置剔除逻辑：命中则不写入；当实体由“可索引”变为“剔除”时，会尽量按 Id 删除旧索引文档：

```csharp
model.Entity<Medium>(e =>
{
    e.Field(x => x.Title, f => f.Store());
    e.ExcludeFromIndexWhen((sp, x) => x.IsDeleted);
});
```

#### 范围查询（TermRangeQuery）

基于字符串的范围比较需在索引阶段规范化数值/日期（零填充/`yyyyMMddHHmmss`）。

### 并发与 UnitOfWork（重要）

在同一 HTTP 请求中并发写入：

- 若在子任务中调用 `Begin()` 且未 `requiresNew`，会复用同一 UoW，可能抛出 “This unit of work has already been initialized.”，且批次合并导致索引波动。

解决方案（二选一）：

- 每个任务单独事务：

```csharp
using var uow = uowManager.Begin(new AbpUnitOfWorkOptions { IsTransactional = true }, requiresNew: true);
// 写入...
await uow.CompleteAsync();
```

- 抑制执行上下文流动：

```csharp
var afc = ExecutionContext.SuppressFlow();
try
{
    await Task.Run(async () =>
    {
        using (var uow = uowManager.Begin(new AbpUnitOfWorkOptions { IsTransactional = true }))
        {
            await uow.CompleteAsync();
        }
    });
}
finally { afc.Undo(); }
```

并发插入 Book 示例（不重建索引）：

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

补充：后台作业天然隔离作用域/事务；模块内部虽改进了新增覆盖问题，但 UoW 边界与并发隔离需由调用方保证。
