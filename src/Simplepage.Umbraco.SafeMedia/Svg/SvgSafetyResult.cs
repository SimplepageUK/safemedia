namespace Simplepage.Umbraco.SafeMedia.Svg;

public sealed record SvgSafetyResult(bool IsSafe, IReadOnlyList<string> Problems)
{
    public static SvgSafetyResult Safe() => new(true, Array.Empty<string>());

    public static SvgSafetyResult Unsafe(IReadOnlyList<string> problems) => new(false, problems);

    /// <summary>Something an editor can act on, not a stack trace.</summary>
    public string ToMessage() =>
        "This SVG was not saved because it "
        + string.Join("; ", Problems)
        + ". SVG files can carry code that runs in the browser, so anything containing script, "
        + "event handlers or external references is refused. Re-export it from your design tool "
        + "as a plain SVG, or ask your site administrator to check it.";
}
