# Changelog

## 1.2.0 - 2026-10-28

PDF support.

- PDFs are checked at upload, at save and by the health check, with the same three layers as SVG.
- Two checks, both must pass. First the document as a reader sees it, through PdfPig: compressed object streams unpacked, escaped names decoded, actions allowlisted wherever they appear. Then every byte of the file, ignoring its index, so objects hidden outside the index (after `%%EOF`, in unlisted compressed streams, left over from earlier edits) are still checked.
- Refused: JavaScript, actions beyond navigation (launch, submit form, import data, open another file, media), attached files, XFA forms, links to anything but http, https and mailto, encrypted or unreadable files, and files that don't start with a PDF header.
- Leftover objects outside the index are accepted with a warning when nothing dangerous is in them. The warning shows in the back office in Umbraco's warning style.
- Options under `Simplepage:SafeMedia:Pdf`: `AllowLinks` (default on) and `AllowAttachments` (default off).
- New dependency: PdfPig 0.1.16 (Apache-2.0, no dependencies of its own).
- The health check is now "Unsafe files in the media library" and covers PDFs.
- Breaking for code that used it directly: `SvgMediaScanner` is now `MediaScanner`, with `MediaScanResult` and `RefusedMedia`. The integration classes are renamed to match (`SafeMediaUploadFilter`, `SafeMediaFileStreamAnalyzer`, `SafeMediaSaveGuard`).
- Fixed: the scan log line from 1.1 wrote the file path where the media name should be.
- PDF corpus: 33 cases, generated hostile samples (`corpus/pdf/generate.py`) plus real exports from Chrome and macOS.

## 1.1.1 - 2026-10-27

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
