using System.Threading.Tasks;
using Simple.Localization;
using Simple.Permissions;
using Simple.MultiTenancy;
using Volo.Abp.SettingManagement.Web.Navigation;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Identity.Web.Navigation;
using Volo.Abp.UI.Navigation;
using Volo.Abp.TenantManagement.Web.Navigation;

namespace Simple.Web.Menus;

public class SimpleMenuContributor : IMenuContributor
{
    public async Task ConfigureMenuAsync(MenuConfigurationContext context)
    {
        if (context.Menu.Name == StandardMenus.Main)
        {
            await ConfigureMainMenuAsync(context);
        }
    }

    private static Task ConfigureMainMenuAsync(MenuConfigurationContext context)
    {
        var l = context.GetLocalizer<SimpleResource>();

        //Home
        context.Menu.AddItem(
            new ApplicationMenuItem(
                SimpleMenus.Home,
                l["Menu:Home"],
                "~/",
                icon: "fa fa-home",
                order: 1
            )
        );


        //Administration
        var administration = context.Menu.GetAdministration();
        administration.Order = 6;

        //Administration->Identity
        administration.SetSubItemOrder(IdentityMenuNames.GroupName, 1);
    
        if (MultiTenancyConsts.IsEnabled)
        {
            administration.SetSubItemOrder(TenantManagementMenuNames.GroupName, 1);
        }
        else
        {
            administration.TryRemoveMenuItem(TenantManagementMenuNames.GroupName);
        }
        
        administration.SetSubItemOrder(SettingManagementMenuNames.GroupName, 3);

        //Administration->Settings
        administration.SetSubItemOrder(SettingManagementMenuNames.GroupName, 7);
    
        context.Menu.AddItem(
            new ApplicationMenuItem(
                "BooksStore",
                l["Menu:Simple"],
                icon: "fa fa-book"
            ).AddItem(
            new ApplicationMenuItem(
                "BooksStore.Books",
                l["Menu:Books"],
                url: "/Books"
                ).RequirePermissions(SimplePermissions.Books.Default) 
            )
        );
        
        return Task.CompletedTask;
    }
}
