# Security policy

## Reporting a bypass

If you have found an SVG or PDF that SafeMedia accepts and should not, **please don't open a public issue**. Report it privately:

**[Report a vulnerability](https://github.com/SimplepageUK/safemedia/security/advisories/new)** (GitHub private vulnerability reporting)

Please include:

- the file, or the smallest file that reproduces it
- for an SVG: which browser it executes in, and how it is loaded (opened directly, `<img>`, `<object>`, back office preview)
- for a PDF: which reader acts on it (Acrobat, a browser viewer, another app), and what it does
- the SafeMedia version, and any non-default configuration

## What happens next

- We acknowledge reports within 3 working days.
- We aim to release a fix within 14 days of confirming a bypass, sooner for anything that runs script.
- The sample goes into the test corpus, so the fix stays fixed.
- With your permission we credit you in the release notes and the advisory.

## Supported versions

Only the latest minor version receives security fixes.

| Version | Supported |
|---|---|
| 1.x | Yes |

## Scope

In scope: anything SafeMedia accepts that can run script, load an external resource, read files from the server (XXE), or exhaust it (billion-laughs and similar) when the file is stored and served by Umbraco.

Out of scope: file types SafeMedia does not handle, and Umbraco's own upload handling. Report those to [Umbraco](https://umbraco.com/about-us/trust-center/security-and-umbraco/how-to-report-a-vulnerability-in-umbraco/).
