using Simple.Localization;
using Volo.Abp.Authorization.Permissions;
using Volo.Abp.Localization;

namespace Simple.Permissions;

public class SimplePermissionDefinitionProvider : PermissionDefinitionProvider
{
    public override void Define(IPermissionDefinitionContext context)
    {
        var myGroup = context.AddGroup(SimplePermissions.GroupName);

        var booksPermission = myGroup.AddPermission(SimplePermissions.Books.Default, L("Permission:Books"));
        booksPermission.AddChild(SimplePermissions.Books.Create, L("Permission:Books.Create"));
        booksPermission.AddChild(SimplePermissions.Books.Edit, L("Permission:Books.Edit"));
        booksPermission.AddChild(SimplePermissions.Books.Delete, L("Permission:Books.Delete"));
        //Define your own permissions here. Example:
        //myGroup.AddPermission(SimplePermissions.MyPermission1, L("Permission:MyPermission1"));
    }

    private static LocalizableString L(string name)
    {
        return LocalizableString.Create<SimpleResource>(name);
    }
}
