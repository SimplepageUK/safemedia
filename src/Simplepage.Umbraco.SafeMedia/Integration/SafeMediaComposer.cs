using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Simplepage.Umbraco.SafeMedia.Svg;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Security;

namespace Simplepage.Umbraco.SafeMedia.Integration;

/// <summary>
/// Installing the package is enough: no configuration is needed to be secure. Settings live
/// under <c>Simplepage:SafeMedia:Svg</c> and only ever widen what is accepted.
/// </summary>
public sealed class SafeMediaComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services
            .AddOptions<SvgSafetyOptions>()
            .BindConfiguration(SvgSafetyOptions.SectionName);

        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IFileStreamSecurityAnalyzer, SvgFileStreamSecurityAnalyzer>());

        // Three layers, outermost first: the upload filter refuses with a message the editor can
        // act on; the analyzer refuses anything that reaches the temporary file service another
        // way; the save guard catches what neither saw and cleans up after it.
        builder.Services.Configure<MvcOptions>(options => options.Filters.Add<SvgTemporaryFileUploadFilter>());

        builder.AddNotificationHandler<MediaSavingNotification, SvgUploadGuard>();

        // Used by SvgMediaHealthCheck, which Umbraco discovers by its [HealthCheck] attribute.
        builder.Services.AddTransient<SvgMediaScanner>();
    }
}
