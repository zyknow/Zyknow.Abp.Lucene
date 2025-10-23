using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Account;
using Volo.Abp.Modularity;
using Volo.Abp.PermissionManagement;
using Volo.Abp.SettingManagement;
using Volo.Abp.VirtualFileSystem;
using Volo.Abp.FeatureManagement;
using Volo.Abp.Identity;
using Volo.Abp.TenantManagement;
using Zyknow.Abp.Lucene; // 引入 Lucene 模块以获取 Application.Contracts 程序集

namespace Simple;

[DependsOn(
    typeof(SimpleApplicationContractsModule),
    typeof(AbpPermissionManagementHttpApiClientModule),
    typeof(AbpFeatureManagementHttpApiClientModule),
    typeof(AbpAccountHttpApiClientModule),
    typeof(AbpIdentityHttpApiClientModule),
    typeof(AbpTenantManagementHttpApiClientModule),
    typeof(AbpSettingManagementHttpApiClientModule),
    typeof(ZyknowLuceneApplicationContractsModule) // 追加 Lucene 合同模块以生成代理
)]
public class SimpleHttpApiClientModule : AbpModule
{
    public const string RemoteServiceName = "Default";

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddHttpClientProxies(
            typeof(SimpleApplicationContractsModule).Assembly,
            RemoteServiceName
        );
        // 为 Lucene 端点生成客户端代理
        context.Services.AddHttpClientProxies(
            typeof(ZyknowLuceneApplicationContractsModule).Assembly,
            RemoteServiceName
        );

        Configure<AbpVirtualFileSystemOptions>(options =>
        {
            options.FileSets.AddEmbedded<SimpleHttpApiClientModule>();
        });
    }
}
