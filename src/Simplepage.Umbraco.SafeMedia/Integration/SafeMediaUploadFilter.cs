using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Simplepage.Umbraco.SafeMedia.Integration;

/// <summary>
/// Refuses a hostile SVG or PDF at the back office's temporary-file upload AND tells the editor
/// why.
///
/// <see cref="SafeMediaFileStreamAnalyzer"/> already refuses at this point, but Umbraco's
/// analyzer contract is a bool, so the editor only ever sees "The temporary file was blocked
/// by a validator" and a per-file "An error occurred". This filter runs just before Umbraco's
/// CreateTemporaryFileController action, where the file name is known, and answers with a
/// problem response carrying the same explanation the save guard gives. Nothing has been
/// stored at this point, so refusing here costs nothing.
///
/// Matched by controller name rather than type so the package does not take a dependency on
/// Umbraco.Cms.Api.Management. If a future Umbraco renames the controller this filter simply
/// stops matching, and the analyzer still refuses the file - the editor just gets the vaguer
/// message again.
///
/// Warnings are not shown here: an accepted upload returns no message to the back office. The
/// save guard shows them when the media item is saved.
/// </summary>
public sealed class SafeMediaUploadFilter : IAsyncActionFilter
{
    private const string CreateTemporaryFileController =
        "Umbraco.Cms.Api.Management.Controllers.TemporaryFile.CreateTemporaryFileController";

    private readonly MediaFileInspector _inspector;
    private readonly ILogger<SafeMediaUploadFilter> _logger;

    public SafeMediaUploadFilter(MediaFileInspector inspector, ILogger<SafeMediaUploadFilter> logger)
    {
        _inspector = inspector;
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
                var inspection = Inspect(file);

                if (inspection is { IsSafe: false })
                {
                    _logger.LogWarning(
                        "SafeMedia refused {Kind} upload '{FileName}' before it was stored. Reasons: {Reasons}",
                        inspection.Kind, file.FileName, string.Join("; ", inspection.Problems));

                    // The back office toast shows the problem title and ignores the detail, so the
                    // whole explanation goes in the title.
                    context.Result = new BadRequestObjectResult(new ProblemDetails
                    {
                        Title = inspection.Message,
                        Detail = inspection.Message,
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

    private MediaInspection? Inspect(IFormFile file)
    {
        if (!MediaFileInspector.Handles(file.FileName))
        {
            return null;
        }

        try
        {
            using var stream = file.OpenReadStream();
            return _inspector.Inspect(file.FileName, stream);
        }
        catch (Exception ex)
        {
            // Fail CLOSED, as everywhere else.
            _logger.LogError(ex, "SafeMedia could not inspect upload '{FileName}'.", file.FileName);
            return MediaFileInspector.Unreadable(file.FileName);
        }
    }
}
