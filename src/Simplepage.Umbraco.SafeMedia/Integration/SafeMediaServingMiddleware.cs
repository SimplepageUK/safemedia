using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.IO;

namespace Simplepage.Umbraco.SafeMedia.Integration;

/// <summary>
/// Adds response headers to files served from the Forms upload folder, so a hostile SVG that
/// somehow got stored cannot run script as the editor who opens it. See
/// <see cref="ServingOptions"/> for what and why.
///
/// Sets the headers as the response starts, so it works whatever serves the file and needs no
/// particular position among the static file handling. Costs one path comparison for any
/// request outside the covered folders.
///
/// Only files served by this site. If uploads go to cloud storage and are served from its own
/// URL, those requests never reach here and the storage's headers apply.
/// </summary>
public sealed class SafeMediaServingMiddleware
{
    // Where Forms puts uploads, relative to the media file system (FileUpload.ConvertToRecord).
    private const string FormsUploadFolder = "forms/upload";

    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<ServingOptions> _options;
    private readonly string? _defaultPath;

    public SafeMediaServingMiddleware(RequestDelegate next, IOptionsMonitor<ServingOptions> options, MediaFileManager mediaFileManager)
    {
        _next = next;
        _options = options;

        // Asked of the media file system, so a custom media path is followed. Cloud storage
        // answers with an absolute URL on another host, which this site never serves.
        var url = mediaFileManager.FileSystem.GetUrl(FormsUploadFolder);
        _defaultPath = url.StartsWith('/') ? url : null;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var options = _options.CurrentValue;

        if (options.Enabled && IsCovered(context.Request.Path, options))
        {
            context.Response.OnStarting(
                static state =>
                {
                    var (httpContext, servingOptions) = ((HttpContext, ServingOptions))state;
                    Apply(httpContext, servingOptions);
                    return Task.CompletedTask;
                },
                (context, options));
        }

        return _next(context);
    }

    private bool IsCovered(PathString requestPath, ServingOptions options)
    {
        if (options.Paths is { Length: > 0 } paths)
        {
            foreach (var path in paths)
            {
                if (!string.IsNullOrWhiteSpace(path) && requestPath.StartsWithSegments(Normalise(path)))
                {
                    return true;
                }
            }

            return false;
        }

        return _defaultPath is not null && requestPath.StartsWithSegments(Normalise(_defaultPath));
    }

    private static PathString Normalise(string path) => new("/" + path.Trim().Trim('/'));

    private static void Apply(HttpContext context, ServingOptions options)
    {
        var headers = context.Response.Headers;

        // Without this a browser may decide a file is something other than the type it was sent as.
        headers["X-Content-Type-Options"] = "nosniff";

        var extension = Path.GetExtension(context.Request.Path.Value) ?? string.Empty;

        var mode = extension.ToLowerInvariant() switch
        {
            ".svg" or ".svgz" => options.Svg,
            ".pdf" => options.Pdf,
            _ => ServedFileMode.None,
        };

        switch (mode)
        {
            case ServedFileMode.Sandbox:
                headers.Append("Content-Security-Policy", "sandbox");
                break;

            case ServedFileMode.Attachment:
                headers["Content-Disposition"] = "attachment";
                break;
        }
    }
}

/// <summary>
/// Puts <see cref="SafeMediaServingMiddleware"/> at the start of the pipeline without asking the
/// site to change its Program.cs.
/// </summary>
public sealed class SafeMediaServingStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<SafeMediaServingMiddleware>();
        next(app);
    };
}
