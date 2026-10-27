namespace Simplepage.Umbraco.SafeMedia.Svg;

/// <summary>
/// The few places where "safe" depends on what a site actually needs. Every default is the
/// strict one except <see cref="AllowStyleElement"/>, so the package is secure with no
/// configuration at all. Bound from <c>Simplepage:SafeMedia:Svg</c>.
///
/// Each switch widens the surface by one named thing, rather than a Strict/Relaxed mode that
/// hides which risk was accepted. Turning one on never relaxes the other checks: a link still
/// cannot be javascript:, an embedded image still cannot be another SVG.
/// </summary>
public sealed class SvgSafetyOptions
{
    public const string SectionName = "Simplepage:SafeMedia:Svg";

    /// <summary>
    /// Allow &lt;style&gt; elements. On by default because Illustrator, Figma and Inkscape all
    /// export classes into a &lt;style&gt; block, and refusing those would refuse most real logos.
    /// The CSS is still checked for @import, expression(), behaviours and any url() that is not
    /// a #fragment. Turn off to remove the widest part of the surface.
    /// </summary>
    public bool AllowStyleElement { get; set; } = true;

    /// <summary>
    /// Allow &lt;a&gt; elements, so an SVG can carry a hyperlink. Links may point at http(s),
    /// mailto, tel or a relative URL; javascript:, vbscript: and data: are still refused.
    /// </summary>
    public bool AllowLinks { get; set; }

    /// <summary>
    /// Allow &lt;image&gt; elements carrying an embedded raster (data:image/png, jpeg, gif or
    /// webp). External image URLs and embedded SVG are still refused.
    /// </summary>
    public bool AllowEmbeddedImages { get; set; }
}
