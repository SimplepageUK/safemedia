namespace Simplepage.Umbraco.SafeMedia.Pdf;

/// <summary>
/// Bound from <c>Simplepage:SafeMedia:Pdf</c>. Secure with no configuration: the one default
/// that is not the strictest is <see cref="AllowLinks"/>, because nearly every brochure,
/// report and form links to a website, and a web link is not a script.
/// </summary>
public sealed class PdfSafetyOptions
{
    public const string SectionName = "Simplepage:SafeMedia:Pdf";

    /// <summary>
    /// Allow link actions to http, https and mailto addresses. Any other scheme (javascript:,
    /// file:, a custom protocol) is refused regardless. Turn off to refuse PDFs with any links.
    /// </summary>
    public bool AllowLinks { get; set; } = true;

    /// <summary>
    /// Allow files embedded in the PDF. Off by default: an attachment is a second file that
    /// nothing has checked, delivered inside one that has been.
    /// </summary>
    public bool AllowAttachments { get; set; }
}
