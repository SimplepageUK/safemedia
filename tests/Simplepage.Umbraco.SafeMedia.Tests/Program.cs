using System.Text;
using Simplepage.Umbraco.SafeMedia.Svg;

// Known-bad and known-good corpus. Compiles the validator from src/ via a linked Compile item,
// so this cannot drift from what ships.
//
// Every reject case names the reason it must be rejected FOR. A sample that is refused for the
// wrong reason - usually because it is malformed XML - proves nothing about the rule it was
// written to test, so that counts as a failure.

var pass = 0;
var fail = 0;

void Expect(string name, string svg, bool shouldBeSafe, string? because = null, SvgSafetyOptions? options = null)
{
    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svg));
    Record(name, SvgSafetyCheck.Check(stream, options ?? new SvgSafetyOptions()), shouldBeSafe, because);
}

void Record(string name, SvgSafetyResult result, bool shouldBeSafe, string? because)
{
    var rightVerdict = result.IsSafe == shouldBeSafe;
    var rightReason = because is null
        || result.Problems.Any(p => p.Contains(because, StringComparison.OrdinalIgnoreCase));
    var ok = rightVerdict && rightReason;

    if (ok) { pass++; } else { fail++; }

    var verdict = result.IsSafe ? "ACCEPTED" : "REJECTED";
    var expected = shouldBeSafe ? "accept" : $"reject: {because}";
    Console.WriteLine($"{(ok ? "  ok  " : "  FAIL")}  {name,-44} {verdict,-9} (expected {expected})");

    if (!result.IsSafe)
    {
        Console.WriteLine($"          -> {string.Join("; ", result.Problems)}");
    }
}

const string Ns = "xmlns=\"http://www.w3.org/2000/svg\"";
const string XlinkNs = "xmlns:xlink=\"http://www.w3.org/1999/xlink\"";

var links = new SvgSafetyOptions { AllowLinks = true };
var images = new SvgSafetyOptions { AllowEmbeddedImages = true };
var noStyle = new SvgSafetyOptions { AllowStyleElement = false };

Console.WriteLine("\nMUST REJECT\n");

Expect("script element", $"<svg {Ns}><script>alert(1)</script></svg>", false, "<script>");
Expect("script, mixed case", $"<svg {Ns}><ScRiPt>alert(1)</ScRiPt></svg>", false, "<ScRiPt>");
Expect("script in CDATA", $"<svg {Ns}><script><![CDATA[alert(1)]]></script></svg>", false, "<script>");
Expect("onload handler", $"<svg {Ns} onload=\"alert(1)\"></svg>", false, "event handler");
Expect("onclick on child", $"<svg {Ns}><rect onclick=\"alert(1)\"/></svg>", false, "event handler");
Expect("ONMOUSEOVER upper case", $"<svg {Ns}><rect ONMOUSEOVER=\"alert(1)\"/></svg>", false, "event handler");

// The cases a regex over raw bytes misses. XmlReader has already decoded these by the time
// the value is inspected, which is the whole argument for parsing. Run with links allowed, so
// the scheme check is what refuses them rather than the <a> element itself.
Expect("javascript:, entity-encoded", $"<svg {Ns} {XlinkNs}><a xlink:href=\"&#106;avascript:alert(1)\"/></svg>", false, "javascript:", links);
Expect("javascript:, hex entity", $"<svg {Ns} {XlinkNs}><a xlink:href=\"&#x6a;avascript:alert(1)\"/></svg>", false, "javascript:", links);
Expect("javascript:, newline in scheme", $"<svg {Ns} {XlinkNs}><a xlink:href=\"java&#x0A;script:alert(1)\"/></svg>", false, "javascript:", links);
Expect("javascript:, tab in scheme", $"<svg {Ns} {XlinkNs}><a xlink:href=\"java&#9;script:alert(1)\"/></svg>", false, "javascript:", links);
Expect("JaVaScRiPt: casing", $"<svg {Ns} {XlinkNs}><a xlink:href=\"JaVaScRiPt:alert(1)\"/></svg>", false, "javascript:", links);

// The original sample here had an unquoted src=x, so it was refused as malformed XML and never
// reached the element rule. Now well-formed, and must be refused FOR the foreignObject.
Expect("foreignObject with HTML", $"<svg {Ns}><foreignObject><body xmlns=\"http://www.w3.org/1999/xhtml\"><img src=\"x\" onerror=\"alert(1)\"/></body></foreignObject></svg>", false, "<foreignObject>");
Expect("iframe", $"<svg {Ns}><iframe src=\"https://evil.test\"/></svg>", false, "<iframe>");
Expect("embed", $"<svg {Ns}><embed src=\"x.swf\"/></svg>", false, "<embed>");
Expect("animate sets href", $"<svg {Ns}><animate attributeName=\"href\" to=\"javascript:alert(1)\"/></svg>", false, "<animate>");
Expect("set element", $"<svg {Ns}><set attributeName=\"onload\" to=\"alert(1)\"/></svg>", false, "<set>");
Expect("handler element", $"<svg {Ns}><handler type=\"text/javascript\">alert(1)</handler></svg>", false, "<handler>");

Expect("external image href", $"<svg {Ns} {XlinkNs}><image xlink:href=\"https://evil.test/x.png\"/></svg>", false, "external URL", images);
Expect("protocol-relative href", $"<svg {Ns} {XlinkNs}><use xlink:href=\"//evil.test/x.svg#a\"/></svg>", false, "external URL");
Expect("data: URL", $"<svg {Ns} {XlinkNs}><image xlink:href=\"data:text/html;base64,PHNjcmlwdD4=\"/></svg>", false, "data:", images);
Expect("vbscript:", $"<svg {Ns} {XlinkNs}><a xlink:href=\"vbscript:msgbox(1)\"/></svg>", false, "vbscript:", links);

Expect("CSS @import", $"<svg {Ns}><style>@import url('https://evil.test/x.css');</style></svg>", false, "@import");
Expect("CSS external url()", $"<svg {Ns}><style>.a{{background:url(https://evil.test/x.png)}}</style></svg>", false, "external resource");
Expect("style attribute, external url", $"<svg {Ns}><rect style=\"fill:url(https://evil.test/x.png)\"/></svg>", false, "external resource");
Expect("xml-stylesheet PI", $"<?xml-stylesheet href=\"https://evil.test/x.xsl\"?><svg {Ns}></svg>", false, "xml-stylesheet");

// DTD is prohibited outright: XXE and billion-laughs.
Expect("DTD / XXE attempt", "<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><svg " + Ns + "><text>&xxe;</text></svg>", false, "DOCTYPE");
Expect("malformed XML", $"<svg {Ns}><rect></svg>", false, "well-formed");
Expect("empty file", "", false, "well-formed");

Console.WriteLine("\nMUST REJECT - added for the package\n");

// Regression: the original read <style> content with ReadElementContentAsString, which left the
// reader on the next node for the loop to skip. Whatever followed a <style> was never checked.
Expect("script straight after style", $"<svg {Ns}><style>.a{{fill:red}}</style><script>alert(1)</script></svg>", false, "<script>");
Expect("handler straight after style", $"<svg {Ns}><style>.a{{fill:red}}</style><rect onclick=\"alert(1)\"/></svg>", false, "event handler");
Expect("markup inside style", $"<svg {Ns}><style>.a{{}}<rect/></style></svg>", false, "markup inside");

Expect("XHTML script", $"<svg {Ns}><script xmlns=\"http://www.w3.org/1999/xhtml\">alert(1)</script></svg>", false, "namespace");
Expect("XHTML iframe in a group", $"<svg {Ns}><g><h:iframe xmlns:h=\"http://www.w3.org/1999/xhtml\" src=\"https://evil.test\"/></g></svg>", false, "namespace");
Expect("MathML element", $"<svg {Ns}><math xmlns=\"http://www.w3.org/1998/Math/MathML\"><mi>x</mi></math></svg>", false, "namespace");
Expect("unknown namespace", $"<svg {Ns}><x:thing xmlns:x=\"urn:evil\"/></svg>", false, "namespace");
Expect("feImage", $"<svg {Ns}><filter id=\"f\"><feImage href=\"https://evil.test/x.png\"/></filter></svg>", false, "<feImage>");

// CSS has its own escapes. These reach the browser as url( and @import.
Expect("CSS escape in url()", $"<svg {Ns}><style>.a{{background:u\\72l(https://evil.test/x.png)}}</style></svg>", false, "external resource");
Expect("CSS escape in @import", $"<svg {Ns}><style>@\\69mport 'https://evil.test/x.css';</style></svg>", false, "@import");
Expect("CSS escape, 6 hex digits", $"<svg {Ns}><style>@\\000069mport 'https://evil.test/x.css';</style></svg>", false, "@import");
Expect("CSS escape in style attribute", $"<svg {Ns}><rect style=\"fill:\\75 rl(https://evil.test/x.svg#p)\"/></svg>", false, "external resource");
Expect("CSS image-set()", $"<svg {Ns}><style>.a{{background-image:image-set(\"https://evil.test/x.png\" 1x)}}</style></svg>", false, "external resource");
Expect("CSS relative url()", $"<svg {Ns}><style>.a{{background:url(x.png)}}</style></svg>", false, "external resource");
Expect("CSS data: url()", $"<svg {Ns}><style>.a{{background:url('data:image/svg+xml,<svg/>')}}</style></svg>", false, "external resource");
Expect("CSS -moz-binding", $"<svg {Ns}><style>.a{{-moz-binding:url(#x)}}</style></svg>", false, "behaviour");
Expect("CSS behavior", $"<svg {Ns}><style>.a{{behavior:url(#x)}}</style></svg>", false, "behaviour");
Expect("CSS expression()", $"<svg {Ns}><rect style=\"width:expression(alert(1))\"/></svg>", false, "expression");

// Presentation attributes are CSS values too.
Expect("fill attribute, external url", $"<svg {Ns}><rect fill=\"url(https://evil.test/x.svg#p)\" width=\"5\" height=\"5\"/></svg>", false, "external resource");
Expect("filter attribute, relative url", $"<svg {Ns}><rect filter=\"url(other.svg#f)\"/></svg>", false, "external resource");

// Outside an <a>, a reference must stay inside the file.
Expect("use, relative file", $"<svg {Ns}><use href=\"sprite.svg#icon\"/></svg>", false, "outside the image");
Expect("use, data: SVG", $"<svg {Ns}><use href=\"data:image/svg+xml;base64,PHN2Zy8+#a\"/></svg>", false, "data:");
Expect("textPath, external", $"<svg {Ns}><text><textPath href=\"https://evil.test/p.svg#p\">x</textPath></text></svg>", false, "external URL");

Expect("DOCTYPE, public SVG 1.1", "<?xml version=\"1.0\"?><!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\"><svg " + Ns + "/>", false, "DOCTYPE");
Expect("billion laughs", "<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY lol \"lol\"><!ENTITY lol2 \"&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;\">]><svg " + Ns + "><text>&lol2;</text></svg>", false, "DOCTYPE");
Expect("XML non-character U+FFFE", "<svg " + Ns + "><text>\uFFFE</text></svg>", false, "well-formed");

// The switches, off by default.
Expect("<a>, default options", $"<svg {Ns}><a href=\"https://example.com\"><rect width=\"5\" height=\"5\"/></a></svg>", false, "<a>");
Expect("<image>, default options", $"<svg {Ns}><image href=\"data:image/png;base64,iVBORw0KGgo=\"/></svg>", false, "<image>");
Expect("<style>, style switched off", $"<svg {Ns}><style>.a{{fill:red}}</style></svg>", false, "<style>", noStyle);

// Turning a switch on widens one thing and nothing else.
Expect("links on, data: href", $"<svg {Ns}><a href=\"data:text/html,&lt;script&gt;alert(1)&lt;/script&gt;\"/></svg>", false, "data:", links);
Expect("links on, script still refused", $"<svg {Ns}><a href=\"https://example.com\"><script>alert(1)</script></a></svg>", false, "<script>", links);
Expect("images on, embedded SVG", $"<svg {Ns}><image href=\"data:image/svg+xml;base64,PHN2Zy8+\"/></svg>", false, "data:", images);
Expect("images on, relative file", $"<svg {Ns}><image href=\"photo.jpg\"/></svg>", false, "outside the image", images);

Console.WriteLine("\nMUST ACCEPT (real artwork - a false positive here means a broken logo)\n");

Expect("minimal rect", $"<svg {Ns} width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\" fill=\"#E8630A\"/></svg>", true);
Expect("gradient + clipPath + defs", $"<svg {Ns} viewBox=\"0 0 100 100\"><title>Logo</title><desc>A logo</desc><defs><linearGradient id=\"g\"><stop offset=\"0%\" stop-color=\"#E8630A\"/><stop offset=\"100%\" stop-color=\"#C9530A\"/></linearGradient><clipPath id=\"c\"><circle cx=\"50\" cy=\"50\" r=\"45\"/></clipPath></defs><g clip-path=\"url(#c)\"><rect width=\"100\" height=\"100\" fill=\"url(#g)\"/><path d=\"M20 70 L50 25 L80 70 Z\" fill=\"#fff\"/></g></svg>", true);
Expect("symbol + use (internal)", $"<svg {Ns} {XlinkNs}><defs><symbol id=\"s\"><circle cx=\"5\" cy=\"5\" r=\"5\"/></symbol></defs><use xlink:href=\"#s\"/></svg>", true);
Expect("style element, safe CSS", $"<svg {Ns}><style>.a{{fill:#E8630A;stroke-width:2}}</style><rect class=\"a\" width=\"10\" height=\"10\"/></svg>", true);
Expect("style attribute, safe", $"<svg {Ns}><rect style=\"fill:#E8630A;opacity:0.5\" width=\"10\" height=\"10\"/></svg>", true);
Expect("text and tspan", $"<svg {Ns}><text x=\"0\" y=\"10\">Simplepage<tspan dx=\"2\">.com</tspan></text></svg>", true);
Expect("mask and pattern", $"<svg {Ns}><defs><mask id=\"m\"><rect width=\"10\" height=\"10\" fill=\"#fff\"/></mask><pattern id=\"p\" width=\"4\" height=\"4\"><circle cx=\"2\" cy=\"2\" r=\"1\"/></pattern></defs><rect mask=\"url(#m)\" fill=\"url(#p)\" width=\"10\" height=\"10\"/></svg>", true);
Expect("comments stripped, still fine", $"<svg {Ns}><!-- exported by Illustrator --><rect width=\"5\" height=\"5\"/></svg>", true);

Console.WriteLine("\nMUST ACCEPT - added for the package\n");

Expect("style with CDATA and comments", $"<svg {Ns}><style><![CDATA[/* brand */ .a{{fill:url(#g)}} .b{{fill:url('#g')}}]]></style></svg>", true);
Expect("Figma drop shadow filter", $"<svg {Ns}><defs><filter id=\"f\" x=\"0\" y=\"0\" width=\"20\" height=\"20\" filterUnits=\"userSpaceOnUse\" color-interpolation-filters=\"sRGB\"><feFlood flood-opacity=\"0\" result=\"BackgroundImageFix\"/><feColorMatrix in=\"SourceAlpha\" type=\"matrix\" values=\"0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 127 0\" result=\"hardAlpha\"/><feOffset dy=\"4\"/><feGaussianBlur stdDeviation=\"2\"/><feComposite in2=\"hardAlpha\" operator=\"out\"/><feBlend mode=\"normal\" in2=\"BackgroundImageFix\" result=\"effect1\"/><feBlend mode=\"normal\" in=\"SourceGraphic\" in2=\"effect1\" result=\"shape\"/></filter></defs><g filter=\"url(#f)\"><rect width=\"10\" height=\"10\"/></g></svg>", true);
Expect("CSS escape for a font name", $"<svg {Ns}><style>.t{{font-family:'\\5FAE\\8F6F\\96C5\\9ED1'}}</style></svg>", true);
Expect("links on, https link", $"<svg {Ns}><a href=\"https://example.com\" target=\"_blank\"><rect width=\"5\" height=\"5\"/></a></svg>", true, options: links);
Expect("links on, mailto and relative", $"<svg {Ns}><a href=\"mailto:hello@example.com\"/><a href=\"/contact\"/></svg>", true, options: links);
Expect("images on, embedded PNG", $"<svg {Ns} {XlinkNs}><image width=\"1\" height=\"1\" xlink:href=\"data:image/png;base64,\n  iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==\"/></svg>", true, options: images);

// Real files from real tools, kept as files so a contributor can add one without touching code.
var corpus = Path.Combine(AppContext.BaseDirectory, "corpus");

foreach (var (folder, shouldBeSafe) in new[] { ("accept", true), ("reject", false) })
{
    var directory = Path.Combine(corpus, folder);
    if (!Directory.Exists(directory))
    {
        continue;
    }

    Console.WriteLine($"\nCORPUS FILES - {folder.ToUpperInvariant()}\n");

    foreach (var file in Directory.EnumerateFiles(directory, "*.svg").Order(StringComparer.Ordinal))
    {
        using var stream = File.OpenRead(file);

        // A reject sample may name its reason in the file name: name--reason.svg
        var stem = Path.GetFileNameWithoutExtension(file);
        var split = stem.IndexOf("--", StringComparison.Ordinal);
        var because = split > 0 ? stem[(split + 2)..].Replace('-', ' ') : null;

        Record($"{folder}/{Path.GetFileName(file)}", SvgSafetyCheck.Check(stream), shouldBeSafe, shouldBeSafe ? null : because);
    }
}

Console.WriteLine($"\n{pass} passed, {fail} failed\n");
return fail == 0 ? 0 : 1;
