using System.Text;
using System.Xml;

namespace Simplepage.Umbraco.SafeMedia.Svg;

/// <summary>
/// Decides whether an SVG is safe to store. Pure and side-effect free, with no Umbraco
/// references, so it can be exercised against known-bad samples without an Umbraco instance.
///
/// WHY THIS EXISTS
///
/// An SVG is not an image the way a JPEG is. It is an XML document the browser will execute:
/// it can carry &lt;script&gt;, inline event handlers, javascript: URLs and embedded HTML.
/// Media is served from the site's own origin, so a hostile SVG uploaded by any editor account
/// is stored cross-site scripting against the public site AND the back office, including the
/// session of the next administrator to open the media library.
///
/// WHY IT REJECTS RATHER THAN SANITISES
///
/// A sanitiser that silently alters a file leaves an editor with a logo that renders
/// differently and no idea why. Rejecting is honest and costs one re-export. It is also what
/// makes strictness cheap: a false positive is an inconvenience, not a defect.
///
/// The model - allowlist the elements rather than blocklist them - comes from
/// cloudflare/svg-hush. A blocklist is only ever as good as its last update; an allowlist
/// fails closed.
///
/// WHY IT PARSES RATHER THAN PATTERN MATCHES
///
/// Grepping for "&lt;script" and "javascript:" is the implementation that gets bypassed,
/// because the attacker picks the encoding and the parser does the decoding:
///
///     href="&amp;#106;avascript:alert(1)"      entity-encoded, no literal match
///     href="java&amp;#x0A;script:alert(1)"     newline inside the scheme
///     &lt;ScRiPt&gt;                            case
///     &lt;![CDATA[ ... ]]&gt;                    wrapped payload
///
/// All four reach the browser as working script and none matches a naive search of the raw
/// bytes. Reading through XmlReader means entity and CDATA decoding has already happened, so
/// the check sees what the browser will see rather than what the file literally says. CSS has
/// its own escape syntax (u\72l, @\69mport), so CSS is unescaped the same way before it is
/// inspected.
/// </summary>
public static class SvgSafetyCheck
{
    private const string SvgNamespace = "http://www.w3.org/2000/svg";

    /// <summary>
    /// Elements an SVG may contain. ALLOWLIST: anything absent is refused, so a new attack
    /// element added to SVG in future fails closed rather than sailing through.
    ///
    /// Deliberately includes the structural, paint-server and filter elements that real
    /// exported artwork uses - defs, use, symbol, the gradients, clipPath, mask, pattern,
    /// marker, and the filter primitives behind a Figma drop shadow - so this does not reject
    /// ordinary logos. Deliberately EXCLUDES script, foreignObject, iframe, embed, object,
    /// handler, feImage (loads a resource), and the animation elements (set, animate,
    /// animateTransform), which can assign a value to any attribute at runtime and so defeat
    /// attribute checking entirely.
    ///
    /// style, a and image are not here: they are gated by <see cref="SvgSafetyOptions"/>.
    /// </summary>
    private static readonly HashSet<string> AllowedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "svg", "g", "defs", "symbol", "use", "switch",
        "title", "desc", "metadata",
        "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
        "text", "tspan", "textPath",
        "linearGradient", "radialGradient", "stop",
        "clipPath", "mask", "pattern", "marker",
        "filter", "feBlend", "feColorMatrix", "feComponentTransfer", "feComposite",
        "feConvolveMatrix", "feDiffuseLighting", "feDisplacementMap", "feDistantLight",
        "feDropShadow", "feFlood", "feFuncA", "feFuncB", "feFuncG", "feFuncR",
        "feGaussianBlur", "feMerge", "feMergeNode", "feMorphology", "feOffset",
        "fePointLight", "feSpecularLighting", "feSpotLight", "feTile", "feTurbulence",
    };

    /// <summary>
    /// Namespaces design tools write their own bookkeeping into (Inkscape's namedview, RDF
    /// licence metadata, Illustrator's private data). Browsers treat elements in a namespace
    /// they do not render as inert, but this is still an allowlist: XHTML and MathML elements
    /// DO render, so anything not listed here is refused. Matched by prefix.
    /// </summary>
    private static readonly string[] EditorNamespaces =
    {
        "http://www.inkscape.org/namespaces/inkscape",
        "http://sodipodi.sourceforge.net/DTD/sodipodi-0.dtd",
        "http://www.w3.org/1999/02/22-rdf-syntax-ns#",
        "http://creativecommons.org/ns#",
        "http://purl.org/dc/elements/1.1/",
        "http://purl.org/dc/terms/",
        "http://ns.adobe.com/",
        "adobe:ns:meta/",
        "http://www.bohemiancoding.com/sketch/ns",
        "http://www.serif.com/",
    };

    private static readonly string[] ForbiddenSchemes = { "javascript:", "vbscript:", "data:" };

    /// <summary>The only data: URLs an &lt;image&gt; may carry, and only when enabled.</summary>
    private static readonly string[] EmbeddedRasterPrefixes =
    {
        "data:image/png;base64,", "data:image/jpeg;base64,", "data:image/jpg;base64,",
        "data:image/gif;base64,", "data:image/webp;base64,",
    };

    /// <summary>Attributes that carry a URL, in any namespace.</summary>
    private static readonly HashSet<string> UrlAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "src", "action", "formaction",
    };

    /// <summary>CSS functions that fetch a resource without going through url().</summary>
    private static readonly string[] CssFetchingFunctions = { "image-set(", "image(", "cross-fade(", "src(" };

    public static SvgSafetyResult Check(Stream svg) => Check(svg, new SvgSafetyOptions());

    public static SvgSafetyResult Check(Stream svg, SvgSafetyOptions options)
    {
        ArgumentNullException.ThrowIfNull(svg);
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();

        var settings = new XmlReaderSettings
        {
            // Prohibit, not Ignore. An SVG has no legitimate need for a DTD, and permitting
            // one opens XXE (reading files off the server) and billion-laughs. Ignore would
            // skip the declaration and carry on; Prohibit refuses the document.
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };

        try
        {
            using var reader = XmlReader.Create(svg, settings);

            // CSS inside a <style> element arrives as one or more text/CDATA nodes. It is
            // collected here and checked at the closing tag, rather than read ahead with
            // ReadElementContentAsString - reading ahead leaves the reader on the following
            // node, which the loop then skips, so whatever came after a <style> went unchecked.
            StringBuilder? styleContent = null;

            // How much the file draws once references are expanded - see SvgReferenceGraph.
            var graph = new SvgReferenceGraph();

            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        if (styleContent is not null)
                        {
                            problems.Add("contains markup inside a <style> element");
                        }

                        if (reader.Depth > SvgReferenceGraph.MaxDepth)
                        {
                            // Stop reading: everything deeper would only repeat this.
                            return SvgSafetyResult.Unsafe(problems.Append("nests elements more deeply than any real image does").ToList());
                        }

                        CheckElement(reader, options, problems);
                        graph.OpenElement(reader);

                        if (IsStyleElement(reader.LocalName) && !reader.IsEmptyElement)
                        {
                            styleContent = new StringBuilder();
                        }
                        break;

                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.SignificantWhitespace:
                        styleContent?.Append(reader.Value);
                        break;

                    case XmlNodeType.EndElement:
                        if (styleContent is not null && IsStyleElement(reader.LocalName))
                        {
                            // <style> is allowed because exported artwork routinely uses CSS
                            // classes, but its body still gets checked: CSS can reach outward
                            // with @import and url(), and old engines honour expression().
                            var css = styleContent.ToString();
                            CheckCss(css, problems);
                            graph.AddStylesheet(css);
                            styleContent = null;
                        }

                        graph.CloseElement();
                        break;

                    case XmlNodeType.ProcessingInstruction:
                        // <?xml-stylesheet href="..."?> pulls in an external stylesheet.
                        if (reader.Name.Equals("xml-stylesheet", StringComparison.OrdinalIgnoreCase))
                        {
                            problems.Add("contains an xml-stylesheet instruction");
                        }
                        break;
                }

                if (problems.Count >= 10)
                {
                    break; // enough to reject; no value in listing more
                }
            }

            if (problems.Count == 0)
            {
                switch (graph.Evaluate(options.MaxRenderedElements))
                {
                    case SvgReferenceGraph.Verdict.Loop:
                        problems.Add("contains references that loop back on themselves");
                        break;

                    case SvgReferenceGraph.Verdict.TooLarge:
                        problems.Add($"would draw more than {options.MaxRenderedElements:N0} shapes when displayed, "
                            + "which can hang the browser (an SVG bomb)");
                        break;
                }
            }
        }
        catch (XmlException ex)
        {
            // A malformed file is refused rather than tolerated. If our parser cannot agree
            // with the browser's on what the document says, we cannot reason about it - and
            // browsers are far more forgiving of broken SVG than XmlReader is, which is
            // precisely the gap an attacker aims for.
            return SvgSafetyResult.Unsafe(new[] { DescribeXmlError(ex) });
        }
        catch (ArgumentException)
        {
            // DecoderFallbackException: bytes that are not valid in the declared encoding.
            return SvgSafetyResult.Unsafe(new[] { "is not valid text in its declared encoding" });
        }

        return problems.Count == 0 ? SvgSafetyResult.Safe() : SvgSafetyResult.Unsafe(problems);
    }

    private static void CheckElement(XmlReader reader, SvgSafetyOptions options, List<string> problems)
    {
        var namespaceUri = reader.NamespaceURI;

        if (namespaceUri.Length == 0 || namespaceUri == SvgNamespace)
        {
            if (!IsAllowedElement(reader.LocalName, options))
            {
                problems.Add($"contains a <{reader.LocalName}> element, which is not allowed in an image");
            }
        }
        else if (!EditorNamespaces.Any(ns => namespaceUri.StartsWith(ns, StringComparison.Ordinal)))
        {
            // Covers XHTML and MathML, both of which the browser renders - an XHTML <script>
            // or <iframe> inside an SVG is as live as one in a web page.
            problems.Add($"contains a <{reader.Name}> element from a namespace that is not allowed in an image");
        }

        if (reader.HasAttributes)
        {
            var element = reader.LocalName;

            while (reader.MoveToNextAttribute())
            {
                CheckAttribute(reader.LocalName, reader.Value, problems, element, options);
            }

            reader.MoveToElement();
        }
    }

    private static bool IsAllowedElement(string localName, SvgSafetyOptions options) =>
        AllowedElements.Contains(localName)
        || (IsStyleElement(localName) && options.AllowStyleElement)
        || (localName.Equals("a", StringComparison.OrdinalIgnoreCase) && options.AllowLinks)
        || (localName.Equals("image", StringComparison.OrdinalIgnoreCase) && options.AllowEmbeddedImages);

    private static bool IsStyleElement(string localName) =>
        localName.Equals("style", StringComparison.OrdinalIgnoreCase);

    private static void CheckAttribute(string name, string value, List<string> problems, string element, SvgSafetyOptions options)
    {
        // Any on* attribute. Matched by prefix rather than against a list of known event
        // names, because that list grows and every miss is a hole.
        if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) && name.Length > 2)
        {
            problems.Add($"contains an inline event handler ({name})");
            return;
        }

        var normalised = Normalise(value);
        var isUrlAttribute = UrlAttributes.Contains(name);

        if (isUrlAttribute
            && options.AllowEmbeddedImages
            && element.Equals("image", StringComparison.OrdinalIgnoreCase)
            && EmbeddedRasterPrefixes.Any(p => normalised.StartsWith(p, StringComparison.Ordinal)))
        {
            return;
        }

        foreach (var scheme in ForbiddenSchemes)
        {
            if (normalised.StartsWith(scheme, StringComparison.Ordinal))
            {
                problems.Add($"contains a {scheme.TrimEnd(':')}: URL in {name}");
                return;
            }
        }

        if (isUrlAttribute)
        {
            // Links are the one place a URL to somewhere else is the point. Everywhere else -
            // use, textPath, gradients, image - a reference must stay inside the file.
            var isLink = element.Equals("a", StringComparison.OrdinalIgnoreCase);

            if (!isLink && normalised.Length > 0 && normalised[0] != '#')
            {
                problems.Add(IsExternal(normalised)
                    ? $"references an external URL in {name}"
                    : $"references a file outside the image in {name}");
            }

            return;
        }

        if (name.Equals("style", StringComparison.OrdinalIgnoreCase))
        {
            CheckCss(value, problems);
            return;
        }

        // Presentation attributes (fill, stroke, clip-path, filter, mask...) are parsed as
        // CSS values, so fill="url(https://...)" loads a resource just as a stylesheet would.
        CheckCssReferences(Normalise(DecodeCssEscapes(value)), problems);
    }

    private static void CheckCss(string css, List<string> problems)
    {
        var normalised = Normalise(DecodeCssEscapes(css));

        if (normalised.Contains("@import", StringComparison.Ordinal))
        {
            problems.Add("contains a CSS @import");
        }

        if (normalised.Contains("expression(", StringComparison.Ordinal))
        {
            problems.Add("contains a CSS expression()");
        }

        if (normalised.Contains("behavior:", StringComparison.Ordinal)
            || normalised.Contains("-moz-binding", StringComparison.Ordinal))
        {
            problems.Add("contains a CSS behaviour binding");
        }

        if (normalised.Contains("javascript:", StringComparison.Ordinal))
        {
            problems.Add("contains a javascript: URL in CSS");
        }

        CheckCssReferences(normalised, problems);
    }

    /// <summary>
    /// Every url() must point at a #fragment inside the file. Allowlisting the one legitimate
    /// shape is stricter than looking for http, //, data: and relative paths one by one.
    /// </summary>
    private static void CheckCssReferences(string normalised, List<string> problems)
    {
        var index = 0;

        while ((index = normalised.IndexOf("url(", index, StringComparison.Ordinal)) >= 0)
        {
            index += 4;
            var argument = normalised.AsSpan(index).TrimStart("'\"");

            if (argument.Length == 0 || argument[0] != '#')
            {
                problems.Add("loads an external resource from CSS");
                return;
            }
        }

        foreach (var function in CssFetchingFunctions)
        {
            if (normalised.Contains(function, StringComparison.Ordinal))
            {
                problems.Add("loads an external resource from CSS");
                return;
            }
        }
    }

    /// <summary>
    /// Resolves CSS escapes (\75 or \u) to the characters they stand for, as the browser's CSS
    /// tokeniser does, so "u\72l(" is inspected as "url(" and "@\69mport" as "@import".
    /// Comments are deliberately left in place: in CSS a comment separates tokens, so it can
    /// break a keyword apart but never hide one.
    /// </summary>
    internal static string DecodeCssEscapes(string css)
    {
        if (!css.Contains('\\'))
        {
            return css;
        }

        var builder = new StringBuilder(css.Length);

        for (var i = 0; i < css.Length; i++)
        {
            if (css[i] != '\\')
            {
                builder.Append(css[i]);
                continue;
            }

            var j = i + 1;
            var codePoint = 0;

            while (j < css.Length && j - i <= 6 && Uri.IsHexDigit(css[j]))
            {
                codePoint = (codePoint * 16) + Convert.ToInt32(css[j].ToString(), 16);
                j++;
            }

            if (j > i + 1)
            {
                // A hex escape swallows one following whitespace character.
                if (j < css.Length && char.IsWhiteSpace(css[j]))
                {
                    j++;
                }

                var valid = codePoint is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF);
                builder.Append(valid ? char.ConvertFromUtf32(codePoint) : "\uFFFD");
                i = j - 1;
            }
            else if (j < css.Length)
            {
                builder.Append(css[j]);
                i = j;
            }
        }

        return builder.ToString();
    }

    private static string DescribeXmlError(XmlException ex) =>
        // The framework's DTD message tells the reader how to ENABLE DTD processing, which is
        // the wrong advice to show an editor. Detected by message because XmlException has no
        // more specific type for it; a miss only costs the friendlier wording.
        ex.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase)
            ? "contains a DOCTYPE declaration, which is not allowed (Illustrator's Save As adds one; use Export As instead)"
            : $"is not well-formed XML (line {ex.LineNumber}, position {ex.LinePosition})";

    /// <summary>
    /// Lower-cases and strips whitespace and control characters before comparison.
    ///
    /// Browsers ignore tabs, newlines and NUL inside a URL scheme, so "java\tscript:" and
    /// "java&#x0A;script:" both execute. Removing them first means the comparison sees the
    /// string the browser will act on, not the one on disk.
    /// </summary>
    private static string Normalise(string value)
    {
        Span<char> buffer = value.Length <= 512 ? stackalloc char[value.Length] : new char[value.Length];
        var length = 0;

        foreach (var c in value)
        {
            if (!char.IsWhiteSpace(c) && !char.IsControl(c))
            {
                buffer[length++] = char.ToLowerInvariant(c);
            }
        }

        return new string(buffer[..length]);
    }

    private static bool IsExternal(string normalised) =>
        normalised.StartsWith("http://", StringComparison.Ordinal)
        || normalised.StartsWith("https://", StringComparison.Ordinal)
        // Protocol-relative: resolves to the page's scheme and is just as external.
        || normalised.StartsWith("//", StringComparison.Ordinal);
}
