using System.Net;
using System.Text;
using Umbraco.Cms.Core.HealthChecks;

namespace Simplepage.Umbraco.SafeMedia.Integration;

/// <summary>
/// Settings > Health Check > Security: lists SVGs and PDFs already in the media library that
/// SafeMedia would refuse today.
///
/// EVERYTHING IN THE MESSAGE IS HTML-ENCODED
///
/// The v14+ back office renders a health check message with unsafeHTML. Media names are typed
/// by editors, so a media item named &lt;img src=x onerror=...&gt; would otherwise run in the
/// administrator's browser - a stored XSS scanner that adds a stored XSS.
/// </summary>
[HealthCheck(
    "a50d83e7-40e6-451f-b639-53bace106da2",
    "Unsafe files in the media library",
    Description = "Checks every SVG and PDF already stored in the media library, including the recycle bin, against SafeMedia's upload rules.",
    Group = "Security")]
public sealed class UnsafeMediaHealthCheck : HealthCheck
{
    /// <summary>Enough to act on; the log has the rest.</summary>
    private const int MaxListed = 50;

    private readonly MediaScanner _scanner;

    public UnsafeMediaHealthCheck(MediaScanner scanner) => _scanner = scanner;

    public override Task<IEnumerable<HealthCheckStatus>> GetStatusAsync() =>
        Task.FromResult<IEnumerable<HealthCheckStatus>>(new[] { Report(_scanner.Scan()) });

    public override HealthCheckStatus ExecuteAction(HealthCheckAction action) =>
        throw new InvalidOperationException("This health check only reports; it has no actions.");

    private static HealthCheckStatus Report(MediaScanResult scan)
    {
        if (scan.CheckedCount == 0)
        {
            return new HealthCheckStatus("There are no SVG or PDF files in the media library.")
            {
                ResultType = StatusResultType.Success,
            };
        }

        if (scan.Refused.Count == 0)
        {
            return new HealthCheckStatus($"Checked {Plural(scan.CheckedCount, "SVG or PDF file")}. All of them would be accepted by SafeMedia today.")
            {
                ResultType = StatusResultType.Success,
            };
        }

        var html = new StringBuilder();
        html.Append(WebUtility.HtmlEncode(
            $"{scan.Refused.Count} of {Plural(scan.CheckedCount, "SVG or PDF file")} in the media library would be refused if uploaded today. "
            + "They were stored before SafeMedia was installed, or while it was configured to allow more. "
            + "Each is still served from this site, so treat it as untrusted: replace it with a clean re-export, or delete it."));

        html.Append("<ul>");

        foreach (var item in scan.Refused.Take(MaxListed))
        {
            html.Append("<li>");

            if (item.Key is { } key)
            {
                html.Append("<a href=\"")
                    .Append(WebUtility.HtmlEncode($"/umbraco/section/media/workspace/media/edit/{key}"))
                    .Append("\"><strong>")
                    .Append(WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(item.Name) ? "(no name)" : item.Name))
                    .Append("</strong></a> ")
                    .Append(WebUtility.HtmlEncode(item.Path));
            }
            else
            {
                html.Append("<strong>")
                    .Append(WebUtility.HtmlEncode(item.Path))
                    .Append("</strong>")
                    .Append(WebUtility.HtmlEncode(" - on disk but not attached to any media item, so it can only be removed from the file system"));
            }

            if (item.InRecycleBin)
            {
                html.Append(WebUtility.HtmlEncode(" (in the recycle bin - the file stays on disk until the bin is emptied)"));
            }

            html.Append("<br>").Append(WebUtility.HtmlEncode("It " + string.Join("; ", item.Problems) + ".")).Append("</li>");
        }

        html.Append("</ul>");

        if (scan.Refused.Count > MaxListed)
        {
            html.Append(WebUtility.HtmlEncode($"…and {scan.Refused.Count - MaxListed} more. The full list is in the log."));
        }

        return new HealthCheckStatus(html.ToString())
        {
            ResultType = StatusResultType.Error,
        };
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
