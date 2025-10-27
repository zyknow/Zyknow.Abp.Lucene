using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;
using Zyknow.Abp.Lucene.Services;

namespace Zyknow.Abp.Lucene;

[DependsOn(
    typeof(ZyknowLuceneDomainModule),
    typeof(ZyknowLuceneDomainSharedModule)
)]
public class ZyknowLuceneApplicationModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddTransient<ILuceneService, LuceneAppService>();
        context.Services.AddSingleton<ILuceneSearcherProvider, LuceneSearcherProvider>();
    }

}
