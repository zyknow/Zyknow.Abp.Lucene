using Volo.Abp.PermissionManagement;
using Volo.Abp.SettingManagement;
using Volo.Abp.Account;
using Volo.Abp.Identity;
using Volo.Abp.Mapperly;
using Volo.Abp.FeatureManagement;
using Volo.Abp.Modularity;
using Volo.Abp.TenantManagement;
using Zyknow.Abp.Lucene; // 引入 Lucene 模块

namespace Simple;

[DependsOn(
    typeof(AbpMapperlyModule),
    typeof(SimpleDomainModule),
    typeof(SimpleApplicationContractsModule),
    typeof(AbpPermissionManagementApplicationModule),
    typeof(AbpFeatureManagementApplicationModule),
    typeof(AbpIdentityApplicationModule),
    typeof(AbpAccountApplicationModule),
    typeof(AbpTenantManagementApplicationModule),
    typeof(AbpSettingManagementApplicationModule),
    typeof(ZyknowLuceneApplicationModule) // 追加 Lucene 应用模块
)]
public class SimpleApplicationModule : AbpModule
{
}