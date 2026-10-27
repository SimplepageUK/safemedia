using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Simplepage.Umbraco.SafeMedia.Svg;
using Umbraco.Cms.Core.Security;

namespace Simplepage.Umbraco.SafeMedia.Integration;

/// <summary>
/// The first line: refuses a hostile SVG at the temporary-file upload, before it is stored
/// anywhere.
///
/// WHY THIS EXISTS AS WELL AS <see cref="SvgUploadGuard"/>
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
/// So it decides "is this an SVG" by sniffing the first kilobyte, and the editor sees Umbraco's
/// generic upload error rather than our reasons (those are logged). Anything the sniff misses -
/// a .svg file whose root is not &lt;svg&gt; - is caught by the guard, by file name.
/// </summary>
public sealed partial class SvgFileStreamSecurityAnalyzer : IFileStreamSecurityAnalyzer
{
    private const int SniffLength = 1024;

    private readonly IOptionsMonitor<SvgSafetyOptions> _options;
    private readonly ILogger<SvgFileStreamSecurityAnalyzer> _logger;

    public SvgFileStreamSecurityAnalyzer(
        IOptionsMonitor<SvgSafetyOptions> options,
        ILogger<SvgFileStreamSecurityAnalyzer> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Pattern matching is fine HERE because this only routes: a false negative falls through
    /// to the guard, a false positive just means a non-SVG document gets the SVG check.
    /// </summary>
    public bool ShouldHandle(Stream fileStream)
    {
        try
        {
            using var reader = new StreamReader(fileStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: SniffLength, leaveOpen: true);
            var buffer = new char[SniffLength];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            var head = new string(buffer, 0, read).TrimStart();

            return head.StartsWith('<') && SvgRootPattern().IsMatch(head);
        }
        catch (Exception ex) when (ex is IOException or DecoderFallbackException)
        {
            return false;
        }
    }

    public bool IsConsideredSafe(Stream fileStream)
    {
        try
        {
            var result = SvgSafetyCheck.Check(fileStream, _options.CurrentValue);

            if (!result.IsSafe)
            {
                _logger.LogWarning(
                    "SafeMedia refused an SVG upload before it was stored. Reasons: {Reasons}",
                    string.Join("; ", result.Problems));
            }

            return result.IsSafe;
        }
        catch (Exception ex)
        {
            // Fail CLOSED: an unreadable file is also an unchecked one.
            _logger.LogError(ex, "SafeMedia could not inspect an SVG upload, so it was refused.");
            return false;
        }
    }

    [GeneratedRegex(@"<(?:[A-Za-z_][\w.-]*:)?svg[\s/>]|<!doctype\s+svg", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SvgRootPattern();
}
