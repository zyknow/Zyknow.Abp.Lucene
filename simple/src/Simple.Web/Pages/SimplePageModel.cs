using Simple.Localization;
using Volo.Abp.AspNetCore.Mvc.UI.RazorPages;

namespace Simple.Web.Pages;

public abstract class SimplePageModel : AbpPageModel
{
    protected SimplePageModel()
    {
        LocalizationResourceType = typeof(SimpleResource);
    }
}
