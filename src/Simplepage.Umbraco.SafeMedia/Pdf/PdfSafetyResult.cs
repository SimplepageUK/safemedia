namespace Simplepage.Umbraco.SafeMedia.Pdf;

/// <summary>
/// Warnings are for a file that is safe to store but that an editor should know about, such as
/// leftover objects from earlier edits that were checked and found clean.
/// </summary>
public sealed record PdfSafetyResult(bool IsSafe, IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings)
{
    public static PdfSafetyResult Safe(IReadOnlyList<string>? warnings = null) =>
        new(true, Array.Empty<string>(), warnings ?? Array.Empty<string>());

    public static PdfSafetyResult Unsafe(IReadOnlyList<string> problems) =>
        new(false, problems, Array.Empty<string>());

    public static PdfSafetyResult Unsafe(string problem) => Unsafe(new[] { problem });

    /// <summary>Something an editor can act on, not a stack trace.</summary>
    public string ToMessage() =>
        "This PDF was not saved because it "
        + string.Join("; ", Problems)
        + ". PDFs can carry scripts, attachments and actions that run when the file is opened in a PDF reader, "
        + "so anything containing them is refused. Re-save it from the original application using Export as PDF "
        + "or Print to PDF, or ask your site administrator to check it.";

    public string ToWarningMessage() =>
        "This PDF was saved, but it "
        + string.Join("; ", Warnings)
        + ". If you can, re-save it from the original application using Export as PDF to produce a clean copy.";
}
