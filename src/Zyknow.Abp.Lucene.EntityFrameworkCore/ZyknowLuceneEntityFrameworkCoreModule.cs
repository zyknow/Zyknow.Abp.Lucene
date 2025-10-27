using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.Modularity;
using Zyknow.Abp.Lucene.Interceptors;

namespace Zyknow.Abp.Lucene;

[DependsOn(
    typeof(AbpEntityFrameworkCoreModule),
    typeof(ZyknowLuceneDomainModule),
    typeof(ZyknowLuceneDomainSharedModule)
)]
public class ZyknowLuceneEntityFrameworkCoreModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // 作为具体类型与 IInterceptor 双重注册，确保通过 IInterceptor 枚举可发现
        context.Services.AddSingleton<LuceneEfChangeInterceptor>();
        context.Services.AddSingleton<IInterceptor>(sp => sp.GetRequiredService<LuceneEfChangeInterceptor>());
    }
}


