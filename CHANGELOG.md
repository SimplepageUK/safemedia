# Changelog

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
