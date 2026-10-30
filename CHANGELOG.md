# Changelog

## 1.2.2 - 2026-10-30

Fewer false refusals on ordinary PDFs, and one real hole closed while testing the change.

### What was wrong

Two checks were stricter than they needed to be, and refused documents that were not dangerous.

**Links to another document.** A PDF can carry an action that opens a second document. SafeMedia refused any file containing one. That is what a brochure does when it links to last year's brochure, so a run against one real media library refused around 25 event programmes and brochures that were doing nothing worse than cross-referencing each other.

**Links without a full web address.** SafeMedia only accepted links beginning `http`, `https` or `mailto`. A link to `#page3`, to `/docs/thing.pdf`, or to `www.example.com` without the `https://` was refused as "a link to something other than a web or email address". These are ordinary links, and a link with no scheme has no scheme to abuse.

### What changed

A link to another document is now judged on **where it points**, not on existing:

- **Allowed:** a relative path alongside the document (`other.pdf`, `docs/2019/brochure.pdf`), and an `http` or `https` address.
- **Refused:** a path on another computer (`\\server\share`), because opening one makes the reader authenticate to a machine of the attacker's choosing and hand over the login details; a path at a fixed location on the machine, including a drive letter; a path that climbs out of its own folder with `..`; and any other scheme, such as `file:`.

Links without a scheme are now accepted. Anything with a scheme is still checked against the allowed list, so `javascript:` and `file:` are refused exactly as before.

### A real hole, found by writing the test for it

The first version of the "another computer" rule looked for two leading backslashes. In a PDF, `\\` in a string parses down to a single backslash, and `\s` and `\x` are not valid escapes at all - so a file written with one level of escaping arrives as `\host\share\x.pdf`, with **one** backslash. It would have passed.

The rule now refuses any single leading slash or backslash as well, and the test corpus builds both forms byte by byte so the difference cannot be lost again.

### What has NOT changed

A PDF that cannot be read is still refused. The structured parser failing is not evidence that a file is safe - it is the absence of evidence either way, and "break the parser so the lenient path runs" is how you would attack a checker that behaved otherwise. Files refused with *"could not be read as a PDF"* should be re-exported from the application that made them.

JavaScript, `Launch`, form submission, data import, embedded media, XFA forms and attachments are all refused exactly as before.


## 1.2.1 - 2026-10-28

Security fix for "SVG bombs" ([GHSA-5f39-6hx3-pgrw](https://github.com/SimplepageUK/safemedia/security/advisories/GHSA-5f39-6hx3-pgrw)). Reported by [Liam Laverty](https://github.com/liamlaverty) - thank you.

### What was wrong

An SVG can reuse a shape by referring to it, so it doesn't have to be drawn out in full each time. Nest those references - a group that repeats a shape ten times, inside a group that repeats that ten times, and so on - and a 2 KB file can make the browser draw millions of shapes. The page hangs or crashes for anyone who views it, including editors previewing it in the back office. Every part of such a file is otherwise legitimate, so SafeMedia used to accept it.

### What changed

SafeMedia now works out how many shapes an SVG will actually draw, once every reference is followed, and refuses the file if that is more than a set limit. The editor sees: *"This SVG was not saved because it would draw more than 50,000 shapes when displayed, which can hang the browser (an SVG bomb)."*

It also refuses files whose references loop back on themselves, and files nested more than 256 levels deep. No real image does either.

### The new setting: `MaxRenderedElements`

This is the most shapes an SVG is allowed to draw. **The default is 50,000, and almost every site can leave it alone.**

- A typical logo or icon draws a few hundred shapes at most, and a detailed illustration a few thousand.
- We tested the limit in Chrome: 100,000 shapes took a couple of seconds to draw; 1,000,000 froze the browser tab. 50,000 leaves a wide margin below the point where browsers struggle.
- **Raise it** only if your site genuinely hosts very large SVGs - a detailed map or a complex technical drawing - and editors are seeing them refused as "SVG bombs". Keep it well under 1,000,000.
- **Lower it** if your site only ever uses logos and icons and you want to be stricter.

To change it, add this to `appsettings.json` (the number is the limit you want):

```json
{
  "Simplepage": {
    "SafeMedia": {
      "Svg": {
        "MaxRenderedElements": 100000
      }
    }
  }
}
```

Files already in the media library are re-checked against the current limit by the health check (**Settings > Health Check > Security > Unsafe files in the media library**).

### Also in this release

- The check runs in a fraction of a second even on deliberately awkward files, so a hostile upload can't slow the server down instead of the browser.
- The test site's `appsettings.json` in 1.0.0 to 1.2.0 contained an image-signing key that Umbraco's installer generated during local testing. It belonged only to that throwaway test site and is now removed; the build now fails if a key is ever committed again.

## 1.2.0 - 2026-10-28

PDF support.

- PDFs are checked at upload, at save and by the health check, with the same three layers as SVG.
- Two checks, both must pass. First the document as a reader sees it, through PdfPig: compressed object streams unpacked, escaped names decoded, actions allowlisted wherever they appear. Then every byte of the file, ignoring its index, so objects hidden outside the index (after `%%EOF`, in unlisted compressed streams, left over from earlier edits) are still checked.
- Refused: JavaScript, actions beyond navigation (launch, submit form, import data, open another file, media), attached files, XFA forms, links to anything but http, https and mailto, encrypted or unreadable files, and files that don't start with a PDF header.
- Leftover objects outside the index are accepted with a warning when nothing dangerous is in them. The warning shows in the back office in Umbraco's warning style.
- A back office extension (`App_Plugins/Simplepage.SafeMedia`, one JavaScript file, no build step) shows warnings on newly created media. Umbraco 17.7 and earlier drop notification messages from create responses, so drag-and-drop uploads would otherwise never show them. The package now uses the Razor SDK to ship it as a static web asset.
- Options under `Simplepage:SafeMedia:Pdf`: `AllowLinks` (default on) and `AllowAttachments` (default off).
- New dependency: PdfPig 0.1.16 (Apache-2.0, no dependencies of its own).
- The health check is now "Unsafe files in the media library" and covers PDFs.
- Breaking for code that used it directly: `SvgMediaScanner` is now `MediaScanner`, with `MediaScanResult` and `RefusedMedia`. The integration classes are renamed to match (`SafeMediaUploadFilter`, `SafeMediaFileStreamAnalyzer`, `SafeMediaSaveGuard`).
- Fixed: the scan log line from 1.1 wrote the file path where the media name should be.
- PDF corpus: 33 cases, generated hostile samples (`corpus/pdf/generate.py`) plus real exports from Chrome and macOS.

## 1.1.1 - 2026-10-28

Documentation only. No code changes.

- The package README said PDF support would follow in 1.1. 1.1 was the health check; PDF is next.
- README links to SECURITY.md and CONTRIBUTING.md now point to GitHub, so they work on NuGet and the Umbraco Marketplace.
- The README links to the Marketplace listing.

## 1.1.0 - 2026-10-27

- Health check: **Settings > Health Check > Security > Unsafe SVGs in the media library.** Checks every SVG already stored, including the recycle bin, against the current rules and configuration. Reports each file that would be refused, with a link, the path and the reasons. Report only; it changes nothing.
- It also finds SVG files in the media folder that no media item references, such as those left by a save-time-only guard in Umbraco 14+.
- Everything in the report is HTML-encoded, because the back office renders health check messages as HTML and media names are editor-controlled.
- `SvgMediaScanner` is a public service, so the same scan can run from your own code or a scheduled job.

## 1.0.0 - 2026-10-27

First release. SVG only.

- Parser-based SVG validator with an element allowlist. Rejects rather than strips. Fails closed on DOCTYPE, malformed XML and unreadable files.
- Refuses hostile SVGs at the back office temporary-file upload, before anything is stored, with the reason shown to the editor.
- `IFileStreamSecurityAnalyzer` refuses SVG content reaching the temporary file service by any other route.
- `MediaSavingNotification` guard as the last line. It explains the refusal to the editor and deletes any file the refused save had already written.
- Options under `Simplepage:SafeMedia:Svg`: `AllowStyleElement`, `AllowLinks`, `AllowEmbeddedImages`.
- CSS is checked after resolving CSS escapes. `url()` must be a `#fragment`, including in presentation attributes such as `fill`.
- Editor metadata namespaces (Inkscape, Sodipodi, RDF, Dublin Core, Creative Commons, Adobe, Sketch, Affinity) are allowed. XHTML, MathML and unknown namespaces are refused.
- Filter primitives allowed (except `feImage`), so Figma drop shadows pass.
- Test corpus of 87 cases, including a drop-in folder of real files, and an Umbraco 17 test site for checking uploads by hand.
