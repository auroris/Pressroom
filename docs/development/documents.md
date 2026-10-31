# Authoring documents

Pressroom renders exactly what you send it: one complete HTML document, laid out by
Chromium's print engine. This guide explains what a document may contain, how to
control pagination, and how to get predictable fonts, headers, and footers. The
[API reference](api.md) lists the request options these techniques pair with.

## Documents are self-contained

A request carries the whole document as a string. The renderer never fetches
anything on its behalf:

- **Styles** go in `<style>` elements or `style` attributes. `<link>` stylesheets
  cannot load.
- **Images, fonts, and other resources** must be `data:` URLs. Network (`http:`,
  `https:`), local-file, and `blob:` resources are blocked before the document is
  parsed, including inside nested frames and `srcdoc` documents.
- **`<base>` elements are rejected** with a 400 response, in the document and in
  header or footer templates, so relative URLs cannot create an alternative fetch
  path.
- **Forms, popups, downloads, workers, service workers, and origin-backed storage**
  are disabled. Scripts are off unless the caller's API key permits them (see
  [JavaScript and readiness](#javascript-and-readiness)).

Blocked resources are simply missing from the output. The server logs a count of
blocked resources per render but never the URLs, so if an image fails to appear,
check that it is embedded as a `data:` URL rather than referenced by address.

Inline SVG works well for logos, marks, and charts, and keeps documents small. The
bundled [sample document](../../samples/orders-bilingual.html) uses an inline SVG mark.

## Fonts

The release packages **Noto Sans** in regular, bold, italic, and bold italic. These
faces are injected into every document automatically, so you need no `@font-face`
rule and no font installation on the server:

```css
:root { font-family: 'Noto Sans', sans-serif; }
```

Any other font must be embedded by the document as a `data:`-backed `@font-face`
rule. Fonts installed on the Windows server may also resolve, but they are a
machine property rather than part of the release, so treat them as optional and
qualify them separately. `GET /v1/info` lists the guaranteed packaged faces.

Pressroom waits for the document's fonts to be ready before printing, within the
render deadline.

## Page size, margins, and breaks

Paper geometry comes from the request options, not from the document, unless you
ask otherwise:

- `options.pageSize` selects `Letter` (the default), `Legal`, `Tabloid`, `A3`, `A4`,
  `A5`, or custom dimensions such as `{ "width": "8.5in", "height": "11in" }`.
  `options.landscape` rotates it.
- `options.margins` default to 20 mm top and bottom and 15 mm left and right.
  Lengths accept `px`, `mm`, `cm`, and `in`, and the margins must leave a positive
  printable area.
- Set `options.preferCssPageSize` to `true` when the document's own `@page { size: … }`
  rule should win over `pageSize`.
- `options.scale` shrinks or enlarges the rendering between 0.1 and 2.

Use the CSS fragmentation properties to control where pages break:

```css
h2 { break-after: avoid; }         /* keep a heading with what follows */
tr, figure { break-inside: avoid; } /* keep a row or figure on one page */
.appendix { break-before: page; }   /* start a section on a new page */
```

Repeated table headers come for free: Chromium repeats `<thead>` on each page of a
long table.

## Print and screen media

Rendering uses print media by default, so `@media print` rules apply and
`@media screen` rules do not. The sample document gives the page a tinted
background on screen for previewing in a browser and a white background in print.
Set `options.media` to `"screen"` to print a document exactly as a browser would
display it. Background colours and images are printed by default; set
`options.printBackground` to `false` to drop them.

## Headers and footers

Running headers and footers are separate HTML templates in the request, not part
of the document. The defaults leave the header blank and print "Page N / M"
at the bottom right in 10px text with 20px of right padding:

```json
"options": {
  "displayHeaderFooter": true,
  "headerTemplate": "<div></div>",
  "footerTemplate": "<div style=\"font-size: 10px; text-align: right; width: 100%; padding-right: 20px;\">Page <span class=\"pageNumber\"></span> / <span class=\"totalPages\"></span></div>"
}
```

Chromium fills elements with the classes `date`, `title`, `url`, `pageNumber`, and
`totalPages`. Templates have their own rules:

- They do not inherit the document's CSS; give them inline styles, including an
  explicit `font-size`, and leave room for them in the top and bottom margins.
- Scripts in templates are not executed, and resources other than `data:` are
  blocked.
- **Fonts in templates fall back to system fonts.** The packaged Noto Sans faces
  reach the main document but not the header and footer area in the pinned
  Chromium build, even with an embedded `@font-face` rule. Use a generic family
  such as `sans-serif` and check the appearance on the Windows server.

Supply `options.headerTemplate` to enable a custom header. The default empty
`<div></div>` suppresses Chromium's built-in header while leaving the footer
enabled. Set `options.displayHeaderFooter` to `false` for documents that draw
their own running elements.

## Accessibility and navigation

Pressroom requests a **tagged PDF** by default, so headings, lists, tables, and
alternative text in well-structured HTML become structure in the PDF. It also
builds a **document outline** (bookmarks) from the heading hierarchy. Both can be
turned off with `options.tagged` and `options.outline`.

Tagging does not by itself make a PDF conform to PDF/UA. Good source HTML still
matters: set `lang` on `<html>`, give the document a `<title>`, use real heading
levels, add `alt` text, and mark table header cells with `<th scope>`.

## Locale and time zone

`options.locale` (default `en-CA`) and `options.timeZone` (default
`America/Edmonton`) set the browser's locale and time zone for the render. They
affect `Intl` formatting and `Date` behaviour in documents that run scripts, and
the `date` substitution in header and footer templates. The locale must be one .NET
recognises; the time zone must be an IANA identifier.

## JavaScript and readiness

Scripts are disabled unless the calling application's API key carries
`AllowJavaScript: true` (see [Configuration](../configuration.md#javascript-permission)).
When the key permits scripts, the document may run inline and `data:`-backed
scripts, which is how chart libraries and client-side templating can be used. The
request can opt out for an individual render with `"options": { "javaScript": false }`
but can never grant permission the key lacks.

Static HTML needs no readiness condition: Pressroom prints once the document and
its fonts have loaded. A document that draws asynchronously can tell Pressroom
when it is ready:

```json
"options": {
  "readiness": {
    "selector": "#chart svg",
    "function": "window.readyToPrint === true",
    "delayMs": 0,
    "timeoutMs": 5000
  }
}
```

- `selector` waits for an element to exist.
- `function` waits for a JavaScript expression to become truthy; it requires
  effective JavaScript and returns 400 otherwise.
- `delayMs` adds a fixed pause after the other conditions, inside the same budget.
- `timeoutMs` bounds all of them. A condition that does not complete in time fails
  the render with 422.

All readiness waiting happens within the request's overall deadline.

## The sample document

[`samples/orders-bilingual.html`](../../samples/orders-bilingual.html) is a compact
template for the techniques above: Noto Sans in all four styles, a `@page` rule,
inline SVG, a table with `break-inside: avoid` rows, a `break-before: page`
section, print and screen media rules, and English and French text. Its companion
[`orders-bilingual.request.json`](../../samples/orders-bilingual.request.json) is
ready to submit and requests inline display.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Image or icon missing | Referenced by URL instead of embedded as `data:` |
| Text in a system font instead of your font | Font not embedded as a `data:` `@font-face`; or it is in a header or footer template |
| Chart area empty | Scripts disabled for the key, or printed before the chart finished drawing; opt the key in and add a readiness condition |
| 400 with `options.readiness.function` | JavaScript is effectively disabled for this request |
| 422 | A readiness condition timed out or page execution raised an error |
| A page splits a row or figure | Add `break-inside: avoid` |
| Headers overlap content | Increase the top or bottom margin |
