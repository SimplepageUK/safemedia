using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Simplepage.Umbraco.SafeMedia.Svg;

namespace Simplepage.Umbraco.SafeMedia.Integration;

/// <summary>
/// Refuses a hostile SVG at the back office's temporary-file upload AND tells the editor why.
///
/// <see cref="SvgFileStreamSecurityAnalyzer"/> already refuses at this point, but Umbraco's
/// analyzer contract is a bool, so the editor only ever sees "The temporary file was blocked
/// by a validator" and a per-file "An error occurred". This filter runs just before Umbraco's
/// CreateTemporaryFileController action, where the file name is known, and answers with a
/// problem response carrying the same explanation the save-time guard gives. Nothing has been
/// stored at this point, so refusing here costs nothing.
///
/// Matched by controller name rather than type so the package does not take a dependency on
/// Umbraco.Cms.Api.Management. If a future Umbraco renames the controller this filter simply
/// stops matching, and the analyzer still refuses the file - the editor just gets the vaguer
/// message again.
/// </summary>
public sealed class SvgTemporaryFileUploadFilter : IAsyncActionFilter
{
    private const string CreateTemporaryFileController =
        "Umbraco.Cms.Api.Management.Controllers.TemporaryFile.CreateTemporaryFileController";

    private readonly IOptionsMonitor<SvgSafetyOptions> _options;
    private readonly ILogger<SvgTemporaryFileUploadFilter> _logger;

    public SvgTemporaryFileUploadFilter(
        IOptionsMonitor<SvgSafetyOptions> options,
        ILogger<SvgTemporaryFileUploadFilter> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is ControllerActionDescriptor action
            && action.ControllerTypeInfo.FullName == CreateTemporaryFileController
            && context.HttpContext.Request.HasFormContentType)
        {
            var form = await context.HttpContext.Request.ReadFormAsync(context.HttpContext.RequestAborted);

            foreach (var file in form.Files)
            {
                var result = Check(file);

                if (result is { IsSafe: false })
                {
                    _logger.LogWarning(
                        "SafeMedia refused SVG upload '{FileName}' before it was stored. Reasons: {Reasons}",
                        file.FileName, string.Join("; ", result.Problems));

                    // The back office toast shows the problem title and ignores the detail, so the
                    // whole explanation goes in the title.
                    context.Result = new BadRequestObjectResult(new ProblemDetails
                    {
                        Title = result.ToMessage(),
                        Detail = result.ToMessage(),
                        Status = StatusCodes.Status400BadRequest,
                        Type = "Error",
                        Extensions = { ["operationStatus"] = "UploadBlocked" },
                    });

                    return;
                }
            }
        }

        await next();
    }

    private SvgSafetyResult? Check(IFormFile file)
    {
        if (file.FileName.EndsWith(".svgz", StringComparison.OrdinalIgnoreCase))
        {
            return SvgSafetyResult.Unsafe(new[] { "is a compressed SVG (.svgz), which cannot be checked" });
        }

        if (!file.FileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using var stream = file.OpenReadStream();
            return SvgSafetyCheck.Check(stream, _options.CurrentValue);
        }
        catch (Exception ex)
        {
            // Fail CLOSED, as everywhere else.
            _logger.LogError(ex, "SafeMedia could not inspect SVG upload '{FileName}'.", file.FileName);
            return SvgSafetyResult.Unsafe(new[] { "could not be read for checking, so it has not been saved" });
        }
    }
}
