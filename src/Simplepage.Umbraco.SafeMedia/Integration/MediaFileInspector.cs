using Microsoft.Extensions.Options;
using Simplepage.Umbraco.SafeMedia.Pdf;
using Simplepage.Umbraco.SafeMedia.Svg;

namespace Simplepage.Umbraco.SafeMedia.Integration;

/// <summary>
/// The one place that knows which file types SafeMedia checks and which validator handles each.
/// The upload filter, the save guard and the media scanner all go through here, so adding a
/// file type is one change rather than four.
/// </summary>
public sealed class MediaFileInspector
{
    private readonly IOptionsMonitor<SvgSafetyOptions> _svgOptions;
    private readonly IOptionsMonitor<PdfSafetyOptions> _pdfOptions;

    public MediaFileInspector(IOptionsMonitor<SvgSafetyOptions> svgOptions, IOptionsMonitor<PdfSafetyOptions> pdfOptions)
    {
        _svgOptions = svgOptions;
        _pdfOptions = pdfOptions;
    }

    /// <summary>True when the file name is one SafeMedia checks.</summary>
    public static bool Handles(string fileName) => KindOf(fileName) is not null;

    /// <summary>A cheap pre-filter for a raw property value, before resolving it to a file.</summary>
    internal static bool MightReferToCheckedFile(string rawValue) =>
        rawValue.Contains(".svg", StringComparison.OrdinalIgnoreCase)
        || rawValue.Contains(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Checks a file. Returns null when SafeMedia does not handle this file type.</summary>
    public MediaInspection? Inspect(string fileName, Stream content)
    {
        switch (KindOf(fileName))
        {
            case "SVGZ":
                // Compressed, so it cannot be inspected without decompressing it, and a browser
                // renders one served with the right headers. Refused rather than waved through.
                return MediaInspection.Refused("SVG", SvgSafetyResult.Unsafe(new[]
                {
                    "is a compressed SVG (.svgz), which cannot be checked",
                }).ToMessage(), "is a compressed SVG (.svgz), which cannot be checked");

            case "SVG":
            {
                var result = SvgSafetyCheck.Check(content, _svgOptions.CurrentValue);
                return new MediaInspection("SVG", result.IsSafe, result.Problems, Array.Empty<string>(),
                    result.IsSafe ? string.Empty : result.ToMessage(), null);
            }

            case "PDF":
            {
                var result = PdfSafetyCheck.Check(content, _pdfOptions.CurrentValue);
                return new MediaInspection("PDF", result.IsSafe, result.Problems, result.Warnings,
                    result.IsSafe ? string.Empty : result.ToMessage(),
                    result.Warnings.Count > 0 ? result.ToWarningMessage() : null);
            }

            default:
                return null;
        }
    }

    /// <summary>For a file that could not be read at all: fail closed, in the right words for its type.</summary>
    public static MediaInspection Unreadable(string fileName)
    {
        const string problem = "could not be read for checking, so it has not been saved";

        return KindOf(fileName) == "PDF"
            ? MediaInspection.Refused("PDF", PdfSafetyResult.Unsafe(problem).ToMessage(), problem)
            : MediaInspection.Refused("SVG", SvgSafetyResult.Unsafe(new[] { problem }).ToMessage(), problem);
    }

    private static string? KindOf(string fileName)
    {
        if (fileName.EndsWith(".svgz", StringComparison.OrdinalIgnoreCase)) return "SVGZ";
        if (fileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return "SVG";
        if (fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return "PDF";
        return null;
    }
}

/// <summary>
/// The outcome of checking one file. <see cref="WarningMessage"/> is set when the file is safe
/// to store but the editor should be told something about it.
/// </summary>
public sealed record MediaInspection(
    string Kind,
    bool IsSafe,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings,
    string Message,
    string? WarningMessage)
{
    public string RefusedTitle => $"{Kind} rejected";

    public string WarningTitle => $"{Kind} saved with a warning";

    internal static MediaInspection Refused(string kind, string message, string problem) =>
        new(kind, false, new[] { problem }, Array.Empty<string>(), message, null);
}
