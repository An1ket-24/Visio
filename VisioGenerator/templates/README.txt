blank.vsdx
==========

Base package for generated drawings.

Origin: sample drawing shipped with the `vsdx` Python package
(https://pypi.org/project/vsdx/, BSD licence, (c) Dave Howard). It is a genuine
Microsoft Visio 16 file, saved by Visio itself, so its document part, style
sheets, window state, masters and relationships are known-good.

Why a template instead of a fully synthetic package:

    Visio validates every part against its own schema on open. A hand-written
    package can be structurally perfect OPC and still be rejected with
    "the file format is invalid or has become corrupted" because one part
    (document.xml, windows.xml, docProps/app.xml) does not match that schema.

So VsdxWriter opens this file, replaces `visio/pages/page1.xml` with the
generated shapes, patches the page size in `visio/pages/pages.xml`, and copies
every other part byte-for-byte. Only the page content is ours; the container is
Visio's.

Replace with a JCI-standard template (.vsdx or .vstx) when one is available -
the generator only needs `visio/pages/page1.xml` and `visio/pages/pages.xml` to
exist in it.
