using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

namespace Simplepage.Umbraco.SafeMedia.Pdf;

/// <summary>
/// Decides whether an uploaded PDF is safe to store. No Umbraco references, so it runs against
/// the corpus without an Umbraco instance.
///
/// WHAT THE THREAT IS
///
/// Browsers' built-in PDF viewers mostly ignore scripts. The people at risk are the ones who
/// download a PDF from the site and open it in a desktop reader such as Adobe Acrobat, which will
/// run JavaScript, launch programs, submit form data and open embedded files. The site is the
/// delivery route, and it is trusted because it is the organisation's own.
///
/// TWO VIEWS, BOTH MUST PASS
///
/// 1. The structured view, through PdfPig: what a reader sees when it follows the document's
///    index. Compressed object streams are unpacked and #xx escapes in names decoded
///    (/J#61vaScript is /JavaScript) before anything is checked - the same reason the SVG check
///    parses rather than searching bytes. Actions are allowlisted wherever they appear.
/// 2. The exhaustive view, <see cref="PdfRawScan"/>: every byte, ignoring the index, including
///    object streams the index never mentions. It catches what a repairing reader could find
///    that the index does not show.
///
/// Method 1 runs first because its reasons are the most specific; method 2 runs only when
/// method 1 is clean. Objects outside the index are accepted with a warning when the exhaustive
/// scan finds nothing dangerous in them - real PDFs that have been edited many times carry them.
///
/// The approach follows Didier Stevens' pdfid, the standard triage for hostile PDFs, which looks
/// for the same names.
/// </summary>
public static class PdfSafetyCheck
{
    /// <summary>Action types that only navigate or change what is shown. ALLOWLIST: anything else is refused.</summary>
    private static readonly HashSet<string> AllowedActions = new(StringComparer.Ordinal)
    {
        "GoTo", "URI", "Named", "Thread", "ResetForm", "Hide", "SetOCGState", "Trans",
    };

    private static readonly Dictionary<string, string> RefusedActions = new(StringComparer.Ordinal)
    {
        ["JavaScript"] = "contains JavaScript",
        ["Launch"] = "launches a program or another file",
        ["SubmitForm"] = "submits form data to a web address",
        ["ImportData"] = "imports data from another file",
        ["GoToR"] = "opens another PDF file",
        ["GoToE"] = "opens a file embedded in it",
        ["Rendition"] = "plays embedded media",
        ["Movie"] = "plays embedded media",
        ["Sound"] = "plays embedded media",
        ["RichMediaExecute"] = "plays embedded media",
    };

    /// <summary>Names that are dangerous wherever they appear, as a key or a value.</summary>
    private static readonly Dictionary<string, string> RefusedNames = new(StringComparer.Ordinal)
    {
        ["JavaScript"] = "contains JavaScript",
        ["JS"] = "contains JavaScript",
        ["Launch"] = "launches a program or another file",
        ["SubmitForm"] = "submits form data to a web address",
        ["ImportData"] = "imports data from another file",
        ["GoToR"] = "opens another PDF file",
        ["GoToE"] = "opens a file embedded in it",
        ["XFA"] = "contains an XFA form, which can run scripts",
        ["RichMedia"] = "contains embedded rich media",
        ["RichMediaExecute"] = "plays embedded media",
        ["Rendition"] = "plays embedded media",
        ["Encrypt"] = "is encrypted, so its contents cannot be checked",
    };

    private static readonly HashSet<string> AttachmentNames = new(StringComparer.Ordinal)
    {
        "EmbeddedFile", "EmbeddedFiles", "FileAttachment",
    };

    private const string Attachment = "contains an attached file";

    private static readonly string[] AllowedLinkSchemes = { "http:", "https:", "mailto:" };

    public static PdfSafetyResult Check(Stream pdf) => Check(pdf, new PdfSafetyOptions());

    public static PdfSafetyResult Check(Stream pdf, PdfSafetyOptions options)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(options);

        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            pdf.CopyTo(buffer);
            bytes = buffer.ToArray();
        }

        // A PDF starts with its header. Something that does not - an HTML page renamed .pdf, or a
        // file with content in front of the header - is refused rather than guessed at.
        if (!bytes.AsSpan().StartsWith("%PDF-"u8))
        {
            return PdfSafetyResult.Unsafe("does not start with a PDF header, so it may not be a PDF");
        }

        // ---- Method 1: the structured view.
        HashSet<int> indexed;
        var problems = new List<string>();

        try
        {
            // Lenient: real PDFs often have small structural faults that every reader repairs.
            // Being lenient about structure is safe because method 2 checks the raw bytes anyway.
            using var document = PdfDocument.Open(bytes, new ParsingOptions { UseLenientParsing = true });

            if (document.IsEncrypted)
            {
                return PdfSafetyResult.Unsafe("is encrypted, so its contents cannot be checked");
            }

            var references = document.Structure.CrossReferenceTable.ObjectOffsets.Keys.ToList();
            indexed = references.Select(r => (int)r.ObjectNumber).ToHashSet();

            var actionReferences = new HashSet<IndirectReference>();

            foreach (var reference in references)
            {
                Walk(document.Structure.GetObject(reference).Data, null, options, problems, actionReferences);
            }

            // Actions held by reference (/OpenAction 12 0 R) are checked as actions too, following
            // /Next chains. Each reference is checked once, so a loop in the chain terminates.
            var seen = new HashSet<IndirectReference>();
            var pending = new Stack<IndirectReference>(actionReferences);

            while (pending.Count > 0)
            {
                var reference = pending.Pop();
                if (!seen.Add(reference))
                {
                    continue;
                }

                var found = new HashSet<IndirectReference>();
                if (document.Structure.GetObject(reference).Data is DictionaryToken action)
                {
                    CheckAction(action, options, problems, found);
                }

                foreach (var next in found)
                {
                    pending.Push(next);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Fail closed: what cannot be read has not been checked. The exhaustive scan still runs,
            // only to give the editor a more specific reason than "could not be read".
            var fallback = PdfRawScan.Scan(bytes);

            if (fallback.Names.Contains("Encrypt"))
            {
                return PdfSafetyResult.Unsafe("is encrypted, so its contents cannot be checked");
            }

            return fallback.Problems.Count > 0
                ? PdfSafetyResult.Unsafe(fallback.Problems.Distinct().ToList())
                : PdfSafetyResult.Unsafe("could not be read as a PDF");
        }

        if (problems.Count > 0)
        {
            return PdfSafetyResult.Unsafe(problems.Distinct().ToList());
        }

        // ---- Method 2: the exhaustive view.
        var raw = PdfRawScan.Scan(bytes);
        problems.AddRange(raw.Problems);

        foreach (var name in raw.Names)
        {
            if (RefusedNames.TryGetValue(name, out var reason))
            {
                problems.Add(reason);
            }
            else if (!options.AllowAttachments && AttachmentNames.Contains(name))
            {
                problems.Add(Attachment);
            }
        }

        foreach (var address in raw.LinkAddresses)
        {
            CheckLinkAddress(address, options, problems);
        }

        if (problems.Count > 0)
        {
            return PdfSafetyResult.Unsafe(problems.Distinct().ToList());
        }

        // Objects outside the index that came through the exhaustive scan clean.
        var leftovers = raw.ObjectNumbers.Count(n => !indexed.Contains(n));

        return leftovers == 0
            ? PdfSafetyResult.Safe()
            : PdfSafetyResult.Safe(new[]
            {
                $"contains {(leftovers == 1 ? "1 leftover object" : $"{leftovers} leftover objects")} from earlier edits "
                + "that are not part of the document. They were checked and contain nothing unsafe",
            });
    }

    /// <summary>
    /// Walks one object. <paramref name="key"/> is the key the token was found under, which is
    /// what tells an action dictionary apart from the many other dictionaries that have an /S.
    /// </summary>
    private static void Walk(
        IToken token, string? key, PdfSafetyOptions options, List<string> problems, HashSet<IndirectReference> actionReferences)
    {
        switch (token)
        {
            case DictionaryToken dict:
                if (IsActionKey(key) || IsActionType(dict))
                {
                    CheckAction(dict, options, problems, actionReferences);
                }

                foreach (var (childKey, value) in dict.Data)
                {
                    CheckName(childKey, options, problems);

                    if (childKey == "AA" && value is DictionaryToken triggers)
                    {
                        // Additional actions: every value is an action, whatever its trigger key.
                        foreach (var trigger in triggers.Data.Values)
                        {
                            CollectAction(trigger, options, problems, actionReferences);
                        }
                    }
                    else if (IsActionKey(childKey) && value is IndirectReferenceToken reference)
                    {
                        actionReferences.Add(reference.Data);
                    }

                    Walk(value, childKey, options, problems, actionReferences);
                }
                break;

            case ArrayToken array:
                foreach (var item in array.Data)
                {
                    if (key == "Next" && item is IndirectReferenceToken next)
                    {
                        actionReferences.Add(next.Data);
                    }

                    Walk(item, key, options, problems, actionReferences);
                }
                break;

            case StreamToken stream:
                Walk(stream.StreamDictionary, key, options, problems, actionReferences);
                break;

            case NameToken name:
                CheckName(name.Data, options, problems);
                break;
        }
    }

    private static void CollectAction(
        IToken token, PdfSafetyOptions options, List<string> problems, HashSet<IndirectReference> actionReferences)
    {
        if (token is IndirectReferenceToken reference)
        {
            actionReferences.Add(reference.Data);
        }
        else if (token is DictionaryToken action)
        {
            CheckAction(action, options, problems, actionReferences);
        }
    }

    private static void CheckAction(
        DictionaryToken action, PdfSafetyOptions options, List<string> problems, HashSet<IndirectReference> actionReferences)
    {
        // /OpenAction can be a destination array rather than an action; only dictionaries with
        // an /S are actions.
        if (!action.Data.TryGetValue("S", out var subtype) || subtype is not NameToken type)
        {
            return;
        }

        if (RefusedActions.TryGetValue(type.Data, out var reason))
        {
            problems.Add(reason);
        }
        else if (!AllowedActions.Contains(type.Data))
        {
            problems.Add("contains an action of a type that is not allowed");
        }

        if (type.Data == "URI")
        {
            var address = action.Data.TryGetValue("URI", out var uri) ? uri switch
            {
                StringToken s => s.Data,
                HexToken h => h.Data,
                _ => string.Empty,
            } : string.Empty;

            CheckLinkAddress(address, options, problems);
        }

        if (action.Data.TryGetValue("Next", out var next))
        {
            if (next is ArrayToken chain)
            {
                foreach (var item in chain.Data)
                {
                    CollectAction(item, options, problems, actionReferences);
                }
            }
            else
            {
                CollectAction(next, options, problems, actionReferences);
            }
        }
    }

    private static void CheckName(string name, PdfSafetyOptions options, List<string> problems)
    {
        if (RefusedNames.TryGetValue(name, out var reason))
        {
            problems.Add(reason);
        }
        else if (!options.AllowAttachments && AttachmentNames.Contains(name))
        {
            problems.Add(Attachment);
        }
    }

    private static void CheckLinkAddress(string address, PdfSafetyOptions options, List<string> problems)
    {
        if (!options.AllowLinks)
        {
            problems.Add("contains a link, which this site does not allow");
            return;
        }

        // As for SVG: browsers and readers ignore whitespace and control characters inside a
        // scheme, so "java\tscript:" is javascript:. Strip them before comparing.
        var normalised = new string(address.Where(ch => !char.IsWhiteSpace(ch) && !char.IsControl(ch)).ToArray())
            .ToLowerInvariant();

        if (!AllowedLinkSchemes.Any(scheme => normalised.StartsWith(scheme, StringComparison.Ordinal)))
        {
            problems.Add("contains a link to something other than a web or email address");
        }
    }

    private static bool IsActionKey(string? key) => key is "A" or "OpenAction" or "Next";

    private static bool IsActionType(DictionaryToken dict) =>
        dict.Data.TryGetValue("Type", out var type) && type is NameToken { Data: "Action" };
}
