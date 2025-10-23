using Volo.Abp.Ui.Branding;
using Volo.Abp.DependencyInjection;
using Microsoft.Extensions.Localization;
using Simple.Localization;

namespace Simple.Web;

[Dependency(ReplaceServices = true)]
public class SimpleBrandingProvider : DefaultBrandingProvider
{
    private IStringLocalizer<SimpleResource> _localizer;

    public SimpleBrandingProvider(IStringLocalizer<SimpleResource> localizer)
    {
        _localizer = localizer;
    }

    public override string AppName => _localizer["AppName"];
}
