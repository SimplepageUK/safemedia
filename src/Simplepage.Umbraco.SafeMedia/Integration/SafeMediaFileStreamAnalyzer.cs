using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Simplepage.Umbraco.SafeMedia.Pdf;
using Simplepage.Umbraco.SafeMedia.Svg;
using Umbraco.Cms.Core.Security;

namespace Simplepage.Umbraco.SafeMedia.Integration;

/// <summary>
/// Refuses a hostile SVG or PDF at the temporary-file upload, before it is stored anywhere, for
/// uploads that do not come through the back office endpoint <see cref="SafeMediaUploadFilter"/>
/// watches.
///
/// WHY THIS EXISTS AS WELL AS <see cref="SafeMediaSaveGuard"/>
///
/// In v14+ the media save maps the property value BEFORE MediaSavingNotification fires, and
/// that mapping (FileUploadPropertyValueEditor.FromEditor) copies the temporary file into media
/// storage. So by the time the guard can cancel the save, the file is already on disk at a
/// public /media URL. Tested against 17.7.2: a cancelled save left /media/xxxx/evil.svg served
/// as image/svg+xml with its payload intact.
///
/// Umbraco runs every IFileStreamSecurityAnalyzer from TemporaryFileService.CreateAsync, and
/// again in FromEditor before the copy, so refusing here means nothing is written at all.
///
/// The trade-off: the analyzer sees a stream, never a file name, and can only answer yes or no.
/// So it decides what a file is by sniffing its start - an SVG root element, or a PDF header -
/// and the editor sees Umbraco's generic upload error rather than our reasons (those are
/// logged). Anything the sniff misses, such as a .pdf that does not start like a PDF, is caught
/// by the upload filter and the guard, by file name.
/// </summary>
public sealed partial class SafeMediaFileStreamAnalyzer : IFileStreamSecurityAnalyzer
{
    private const int SniffLength = 1024;

    private readonly IOptionsMonitor<SvgSafetyOptions> _svgOptions;
    private readonly IOptionsMonitor<PdfSafetyOptions> _pdfOptions;
    private readonly ILogger<SafeMediaFileStreamAnalyzer> _logger;

    public SafeMediaFileStreamAnalyzer(
        IOptionsMonitor<SvgSafetyOptions> svgOptions,
        IOptionsMonitor<PdfSafetyOptions> pdfOptions,
        ILogger<SafeMediaFileStreamAnalyzer> logger)
    {
        _svgOptions = svgOptions;
        _pdfOptions = pdfOptions;
        _logger = logger;
    }

    /// <summary>
    /// Pattern matching is fine HERE because this only routes: a false negative falls through
    /// to the filter and guard, a false positive just means another document gets a check.
    /// </summary>
    public bool ShouldHandle(Stream fileStream) => Sniff(fileStream) is not null;

    public bool IsConsideredSafe(Stream fileStream)
    {
        var kind = Sniff(fileStream);
        fileStream.Seek(0, SeekOrigin.Begin);

        try
        {
            var (isSafe, problems) = kind == "PDF"
                ? Pdf(PdfSafetyCheck.Check(fileStream, _pdfOptions.CurrentValue))
                : Svg(SvgSafetyCheck.Check(fileStream, _svgOptions.CurrentValue));

            if (!isSafe)
            {
                _logger.LogWarning(
                    "SafeMedia refused a {Kind} upload before it was stored. Reasons: {Reasons}",
                    kind, string.Join("; ", problems));
            }

            return isSafe;
        }
        catch (Exception ex)
        {
            // Fail CLOSED: an unreadable file is also an unchecked one.
            _logger.LogError(ex, "SafeMedia could not inspect a {Kind} upload, so it was refused.", kind);
            return false;
        }

        static (bool, IReadOnlyList<string>) Pdf(PdfSafetyResult r) => (r.IsSafe, r.Problems);
        static (bool, IReadOnlyList<string>) Svg(SvgSafetyResult r) => (r.IsSafe, r.Problems);
    }

    private static string? Sniff(Stream fileStream)
    {
        try
        {
            var bytes = new byte[SniffLength];
            var read = fileStream.ReadAtLeast(bytes, SniffLength, throwOnEndOfStream: false);

            if (bytes.AsSpan(0, read).StartsWith("%PDF-"u8))
            {
                return "PDF";
            }

            using var reader = new StreamReader(new MemoryStream(bytes, 0, read), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var head = reader.ReadToEnd().TrimStart();

            return head.StartsWith('<') && SvgRootPattern().IsMatch(head) ? "SVG" : null;
        }
        catch (Exception ex) when (ex is IOException or DecoderFallbackException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"<(?:[A-Za-z_][\w.-]*:)?svg[\s/>]|<!doctype\s+svg", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SvgRootPattern();
}
