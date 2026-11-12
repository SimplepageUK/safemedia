namespace Simplepage.Umbraco.SafeMedia.Integration;

/// <summary>
/// Bound from <c>Simplepage:SafeMedia:Serving</c>. Secure with no configuration.
///
/// What a browser does with a file depends on the headers it is served with, and files that
/// visitors upload through Umbraco Forms are served inline from the same origin as the back
/// office. So an editor who opens a hostile SVG from a Forms entry runs its script as
/// themselves. Scanning refuses these at upload; this limits the damage if one is ever stored
/// anyway, including a file stored before SafeMedia was installed.
///
/// Applies only to the folders in <see cref="Paths"/>, by default the Forms upload folder. It
/// does not touch the media library or anything a front end page uses.
/// </summary>
public sealed class ServingOptions
{
    public const string SectionName = "Simplepage:SafeMedia:Serving";

    /// <summary>
    /// Turn the whole thing off. Nothing else here has any effect when this is false.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// URL folders to apply this to, such as <c>/media/forms/upload</c>. Leave empty for the
    /// Forms upload folder, which SafeMedia works out from Umbraco's media path. Setting it
    /// replaces that default.
    /// </summary>
    public string[]? Paths { get; set; }

    /// <summary>How SVG files (.svg and .svgz) in those folders are served.</summary>
    public ServedFileMode Svg { get; set; } = ServedFileMode.Sandbox;

    /// <summary>
    /// How PDF files in those folders are served. Off by default: a sandbox stops the browser's
    /// built-in PDF viewer, and a PDF that runs script is already refused at upload.
    /// </summary>
    public ServedFileMode Pdf { get; set; } = ServedFileMode.None;
}

/// <summary>What the browser is told to do with a file it has been sent.</summary>
public enum ServedFileMode
{
    /// <summary>
    /// The file still displays, but its scripts cannot run and it has no access to the site's
    /// cookies or the back office. Sent as <c>Content-Security-Policy: sandbox</c>.
    /// </summary>
    Sandbox,

    /// <summary>
    /// The file downloads rather than displaying when opened directly. Sent as
    /// <c>Content-Disposition: attachment</c>. Strictest, but editors can no longer preview it.
    /// </summary>
    Attachment,

    /// <summary>Leave the file as it is served today.</summary>
    None,
}
