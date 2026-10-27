# SafeMedia for Umbraco

Refuses unsafe SVG uploads before they are stored.

`Simplepage.Umbraco.SafeMedia` is free and MIT licensed, published by Simplepage Ltd. Version 1 covers SVG. PDF follows in 1.1.

```
dotnet add package Simplepage.Umbraco.SafeMedia
```

That is the whole installation. There is nothing to configure to be secure.

## The problem

An SVG is not a picture the way a JPEG is. It is an XML document, and a browser will run what is inside it: `<script>` elements, `onload` handlers, `javascript:` links, embedded HTML.

Umbraco serves media from the site's own domain. So when you allow SVG uploads, any editor account - or anyone who gets hold of one - can upload a file that runs script as your site. Open that file's URL, or let the back office preview it, and the script runs with the same access as the person looking at it. If that person is an administrator, the script can do whatever an administrator can.

Allowing SVG in Umbraco is a one-line config change. Nothing in Umbraco checks what is inside the file. That gap is what this package closes.

## What it does

Every SVG is parsed and checked against an allowlist before Umbraco keeps it. If anything fails, the upload is refused and the editor is told why. The file is never altered.

- **It parses, it does not pattern match.** Searching a file for `<script` and `javascript:` is the version that gets bypassed, because the attacker chooses the encoding and the browser does the decoding. All four of these run as script in a browser, and none of them contains the text `javascript:`:

  ```
  href="&#106;avascript:alert(1)"      entity
  href="&#x6a;avascript:alert(1)"      hex entity
  href="java&#x0A;script:alert(1)"     newline inside the scheme
  href="java&#9;script:alert(1)"       tab inside the scheme
  ```

  SafeMedia reads the file with .NET's `XmlReader`, so by the time a value is checked it has already been decoded. The check sees what the browser will see. CSS gets the same treatment: `u\72l(` is checked as `url(`.

- **It allowlists, it does not blocklist.** Only elements known to be safe in artwork are allowed: shapes, paths, text, groups, gradients, patterns, masks, clip paths, markers, `use`/`symbol` and filters. Anything else is refused, including elements SVG has not invented yet. A blocklist is only as good as its last update.

- **It rejects, it does not strip.** A sanitiser that quietly removes the bad parts hands the editor a logo that renders differently, with no idea why. A rejection costs one re-export. It also makes strictness cheap: a false positive is an inconvenience, not a broken page.

- **It fails closed.** A DOCTYPE, malformed XML, an unreadable file or an unexpected error all mean a refusal. DTDs are prohibited outright, which closes XXE and billion-laughs.

### What is refused

| | |
|---|---|
| Script | `<script>`, any `on*` attribute, `javascript:`, `vbscript:` and `data:` URLs |
| Embedded documents | `<foreignObject>`, `<iframe>`, `<embed>`, `<object>`, and any XHTML or MathML element |
| Animation | `<set>`, `<animate>` and friends, which can rewrite any attribute after the check |
| Outside references | Any `href` that is not a `#fragment` in the same file, any CSS `url()` that is not a `#fragment`, `@import`, `image-set()`, `xml-stylesheet`, `<feImage>` |
| Old CSS execution | `expression()`, `behavior`, `-moz-binding` |
| Anything unreadable | DOCTYPE, malformed XML, invalid characters, `.svgz` (compressed, so it cannot be checked) |

Editor metadata from Inkscape, Illustrator, Sketch and Affinity is allowed. Browsers don't render it.

## How it hooks into Umbraco

Three layers, outermost first.

1. **At upload, with the reason.** An MVC filter on Umbraco's temporary-file upload checks any `.svg` before Umbraco stores it. If it fails, the editor sees why straight away: *"This SVG was not saved because it contains an inline event handler (onload)..."* Nothing is written.

2. **At upload, for everything else.** An `IFileStreamSecurityAnalyzer`, which Umbraco runs for every temporary file however it arrives, refuses anything that looks like SVG. This catches uploads that don't come through the back office endpoint. It can only answer yes or no, so the editor gets Umbraco's generic message there. The reasons go to the log.

3. **At save.** A `MediaSavingNotification` handler re-checks any `.svg` on the media item, cancels the save with the same message, and deletes the file if the save has just written it. This covers SVGs already in the media library before the package was installed: they are re-checked the next time anyone saves them.

Why not just the save handler: in Umbraco 14+ the upload property editor copies the file into media storage *before* `MediaSavingNotification` fires. A handler that only cancels the save leaves the file on a public `/media/...` URL. We tested exactly that on Umbraco 17.7.2 before shipping. A cancelled save left the payload served as `image/svg+xml`.

## Configuration

None is needed. Every switch below only widens what is accepted, and the defaults are the strict ones except `<style>`.

```json
{
  "Simplepage": {
    "SafeMedia": {
      "Svg": {
        "AllowStyleElement": true,
        "AllowLinks": false,
        "AllowEmbeddedImages": false
      }
    }
  }
}
```

| Setting | Default | What it allows |
|---|---|---|
| `AllowStyleElement` | `true` | `<style>` blocks. Illustrator, Figma and Inkscape all export CSS classes, so refusing them would refuse most real logos. The CSS is still checked. Set `false` to remove the widest part of the surface. |
| `AllowLinks` | `false` | `<a>` elements linking to http(s), mailto, tel or relative URLs. `javascript:`, `vbscript:` and `data:` are still refused. |
| `AllowEmbeddedImages` | `false` | `<image>` with an embedded PNG, JPEG, GIF or WebP (`data:image/...;base64`). External image URLs and embedded SVG are still refused. |

There is no Strict/Relaxed mode on purpose: each switch names the one thing you are accepting.

## Known false positives

If SafeMedia refuses a file you think is legitimate, please [tell us](CONTRIBUTING.md#2-false-positives). These are the ones we know about:

- **Illustrator "Save As" SVG** adds a `<!DOCTYPE>` and, with "Preserve Illustrator Editing Capabilities", a `<foreignObject>`. Use **File > Export > Export As > SVG** instead.
- **Old icon sets** often start with `<!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" ...>`. Delete that line; the image renders the same.
- **SVGs with links or embedded photos** need `AllowLinks` or `AllowEmbeddedImages`.
- **Sprite sheets referencing other files** (`<use href="sprite.svg#icon">`) are refused. Only same-file references are allowed.

## Compatibility

| | |
|---|---|
| Umbraco | 17.x |
| .NET | 10 |

**Umbraco 13 is not supported.** Its upload pipeline is different (no temporary file service; the file is written during the save request), so it would need a separate integration and a second CI matrix. Umbraco 13 reaches the end of security support in December 2026. If you need it, open an issue - the validator itself has no Umbraco dependency and would carry over unchanged.

## Roadmap

- **1.1** - PDF: refuse JavaScript, launch actions and embedded files.
- **1.2** - A health check that scans media uploaded before the package was installed and reports anything that would now be refused. Also an optional clean mode, for sites that would rather strip than reject.

## Contributing

**The corpus is the product.** A package like this is trusted for its test suite, not its code. We want two things, in this order:

1. **Bypasses.** An SVG that runs script, loads something external, or otherwise does harm, and that SafeMedia accepts. Please report these privately - see [SECURITY.md](SECURITY.md) - and we will add it to the corpus with credit.
2. **False positives.** Real artwork from a real tool that SafeMedia refuses. These are harder to find than attacks and matter as much, because a package that refuses good logos gets uninstalled. Drop the file into `tests/Simplepage.Umbraco.SafeMedia.Tests/corpus/accept/` and open a pull request.

See [CONTRIBUTING.md](CONTRIBUTING.md) for how to run the tests.

## Credits

[cloudflare/svg-hush](https://github.com/cloudflare/svg-hush) is the best SVG safety implementation we found, and its allowlist model shaped this one. It is written in Rust, so it can't be used from .NET without FFI or WebAssembly. If you are not on .NET, use it.

## Licence

MIT. Copyright (c) 2026 Simplepage Ltd.
