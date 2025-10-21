using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;
using Zyknow.Abp.Lucene.Services;

namespace Zyknow.Abp.Lucene;

[DependsOn(
    typeof(ZyknowLuceneDomainSharedModule)
)]
public class ZyknowLuceneDomainModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // 注册 LuceneIndexManager 到领域层，作为索引写入基础能力
        context.Services.AddTransient<LuceneIndexManager>();
    }
}