using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Simplepage.Umbraco.SafeMedia.Integration;

/// <summary>
/// Refuses a hostile SVG or PDF posted through an Umbraco Forms file upload field, judged by
/// file name as well as content.
///
/// WHY THIS EXISTS AS WELL AS <see cref="SafeMediaFileStreamAnalyzer"/>
///
/// Forms runs every IFileStreamSecurityAnalyzer on an upload, so the analyzer already refuses
/// most of what this does. But the analyzer is handed a stream and no file name, so it can only
/// decide what a file is by sniffing it. A page of HTML named .svg, or a .pdf with anything
/// before its header, is not sniffed as either and is stored. On media uploads
/// <see cref="SafeMediaUploadFilter"/> and the save guard catch those by name; Forms has
/// neither, so this does the same job here.
///
/// HOW IT REFUSES
///
/// Runs just before Forms' HandleForm action, once its antiforgery check has passed, and adds
/// an error to the model state under the file field's id. Forms stores nothing while the model
/// state is invalid, and shows the error against that field. Errors are not cleared by Forms
/// between this filter and the action.
///
/// THE SUBMITTER SEES A GENERIC MESSAGE. These are anonymous public uploads, so the reasons
/// (which say exactly which check a file failed) go to the log, not the page.
///
/// MATCHED BY CONTROLLER NAME, NOT TYPE. Not a style choice: this package must not name a Forms
/// type anywhere. Umbraco scans every type in an installed package at boot, and a type that
/// refers to an assembly the site does not have makes that scan throw, which stops the site
/// starting. Tested: a handler for Forms' own FormValidateNotification did exactly that on a
/// site without Forms. With the name match, a site without Forms never matches and pays one
/// string comparison per action. If a future Forms renames the controller this filter simply
/// stops matching, and the analyzer still refuses what it can sniff.
/// </summary>
public sealed class SafeMediaFormsFilter : IAsyncActionFilter
{
    private const string FormsController = "Umbraco.Forms.Web.Controllers.UmbracoFormsController";
    private const string SubmitAction = "HandleForm";

    // Matches the default of Forms' own ErrorMessageForFailedSecurityCheck, so a refusal here
    // reads the same as one from the analyzer.
    internal const string RefusedMessage = "The file uploaded is not allowed.";

    private readonly MediaFileInspector _inspector;
    private readonly ILogger<SafeMediaFormsFilter> _logger;

    public SafeMediaFormsFilter(MediaFileInspector inspector, ILogger<SafeMediaFormsFilter> logger)
    {
        _inspector = inspector;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is ControllerActionDescriptor action
            && action.ControllerTypeInfo.FullName == FormsController
            && action.ActionName == SubmitAction
            && context.HttpContext.Request.HasFormContentType)
        {
            var form = await context.HttpContext.Request.ReadFormAsync(context.HttpContext.RequestAborted);

            foreach (var file in form.Files)
            {
                // Forms posts a file field under its field id, which is also the key it uses for
                // that field's errors.
                if (!Guid.TryParse(file.Name, out _) || !MediaFileInspector.Handles(file.FileName))
                {
                    continue;
                }

                var inspection = Inspect(file);

                if (inspection is { IsSafe: false })
                {
                    _logger.LogWarning(
                        "SafeMedia refused {Kind} upload '{FileName}' from a Forms submission before it was stored. Reasons: {Reasons}",
                        inspection.Kind, file.FileName, string.Join("; ", inspection.Problems));

                    context.ModelState.AddModelError(file.Name, RefusedMessage);
                }
            }
        }

        await next();
    }

    private MediaInspection? Inspect(Microsoft.AspNetCore.Http.IFormFile file)
    {
        try
        {
            using var stream = file.OpenReadStream();
            return _inspector.Inspect(file.FileName, stream);
        }
        catch (Exception ex)
        {
            // Fail CLOSED, as everywhere else.
            _logger.LogError(ex, "SafeMedia could not inspect Forms upload '{FileName}'.", file.FileName);
            return MediaFileInspector.Unreadable(file.FileName);
        }
    }
}
