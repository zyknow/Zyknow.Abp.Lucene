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
    public override void PreConfigureServices(ServiceConfigurationContext context)
    {
        PreConfigure<AbpDbContextOptions>(options =>
        {
            options.Configure(ctx =>
            {
                // 统一从容器拉取所有已注册的 EF Core 拦截器，并确保包含 Lucene 拦截器
                var interceptors = ctx.ServiceProvider.GetServices<IInterceptor>();
                if (interceptors != null && interceptors.Any())
                {
                    ctx.DbContextOptions.AddInterceptors(interceptors);
                }
            });
        });
    }

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // 作为具体类型与 IInterceptor 双重注册，确保通过 IInterceptor 枚举可发现
        context.Services.AddSingleton<LuceneEfChangeInterceptor>();
        context.Services.AddSingleton<IInterceptor>(sp => sp.GetRequiredService<LuceneEfChangeInterceptor>());
    }
}


