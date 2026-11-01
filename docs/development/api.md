# API reference

Pressroom exposes a small HTTP API under `/v1`. All requests and responses are
UTF-8; request bodies are uncompressed `application/json`. The live OpenAPI
document at `/openapi/v1.json` describes the same contract.

Pressroom returns PDF bytes and metadata to the calling application. The
application chooses whether its browser response displays or downloads the PDF;
see [Returning the PDF to a browser](client.md#returning-the-pdf-to-a-browser).

### Upgrading callers that use disposition

The `disposition` request property and job field have been removed, along with
`PdfDisposition` and the .NET request/result `Disposition` properties. Remove
the field from render and job submissions and update the client and contracts
packages together with the server. Requests that still send it return 400, even
for `"inline"` or `"attachment"`. Move that choice into the application's browser
response. Existing stored jobs remain readable; recovery ignores their legacy
field without exposing it in job responses.

## Endpoints

| Endpoint | Access | Result |
|---|---|---|
| `POST /v1/render` | Configured access mode; API key required when `printer` is supplied | `application/pdf`, or a 202 JSON print receipt |
| `GET /v1/health` | Always anonymous | `{"status":"ready"}` with 200, or `{"status":"unavailable"}` with 503 |
| `GET /v1/info` | Configured access mode | Versions, limits, counters, page sizes, packaged fonts |
| `GET /v1/printers` | API key required in every mode | Configured printer identifiers |
| `GET /openapi/v1.json` | Configured access mode | OpenAPI contract |
| `/v1/jobs` routes | Configured access mode; only when jobs are enabled | See [Asynchronous jobs](#asynchronous-jobs-opt-in) |

In API-key mode, send the secret in the `X-Api-Key` header. In anonymous mode the
header may be omitted; a supplied invalid key is still rejected with 401. See
[Configuration](../configuration.md#access-mode-and-api-keys).

## POST /v1/render

### Request

```json
{
  "html": "<!doctype html><html lang=\"en\"><head><title>Orders</title></head><body>…</body></html>",
  "fileName": "orders-2026-10.pdf",
  "printer": null,
  "options": {
    "pageSize": "Letter",
    "landscape": false,
    "margins": { "top": "20mm", "right": "15mm", "bottom": "20mm", "left": "15mm" },
    "preferCssPageSize": false,
    "printBackground": true,
    "scale": 1.0,
    "displayHeaderFooter": true,
    "headerTemplate": "<div></div>",
    "footerTemplate": "<div style=\"font-size: 10px; text-align: right; width: 100%; padding-right: 20px;\">Page <span class=\"pageNumber\"></span> / <span class=\"totalPages\"></span></div>",
    "tagged": true,
    "outline": true,
    "media": "print",
    "javaScript": true,
    "locale": "en-CA",
    "timeZone": "America/Edmonton",
    "readiness": { "selector": null, "function": null, "delayMs": 0, "timeoutMs": 5000 },
    "timeoutMs": 30000
  }
}
```

Only `html` is required. The values shown are the defaults. Property names are
case-sensitive, unknown properties are rejected, numbers must be JSON numbers, and
`options`, `margins`, and `readiness` may be omitted but not set to `null`.

| Field | Type | Default | Rules |
|---|---|---|---|
| `html` | string | required | A complete HTML document. `<base>` elements are rejected |
| `fileName` | string | `document.pdf` | Suggested PDF filename returned to the application. Sanitized: path separators and control characters replaced, reserved Windows names prefixed, truncated to 120 characters, `.pdf` appended |
| `printer` | string or null | `null` | A configured printer identifier; switches the response to a print receipt. See [Printing](../printing.md) |
| `options.pageSize` | name or object | `Letter` (configurable) | `Letter`, `Legal`, `Tabloid`, `A3`, `A4`, `A5` (case-insensitive), or `{ "width": "8.5in", "height": "11in" }` |
| `options.landscape` | boolean | `false` | Swaps the paper dimensions |
| `options.margins` | object | 20 mm / 15 mm | Each of `top`, `right`, `bottom`, `left` is a length in `px`, `mm`, `cm`, or `in`, up to 200 inches. Margins must leave a positive printable area |
| `options.preferCssPageSize` | boolean | `false` | Let the document's `@page` size override `pageSize` |
| `options.printBackground` | boolean | `true` | Print background colours and images |
| `options.scale` | number | `1` | 0.1 to 2 |
| `options.displayHeaderFooter` | boolean | `true` | Render the header and footer templates |
| `options.headerTemplate`, `options.footerTemplate` | string | blank header / right-aligned `Page N / M` footer | HTML with Chromium's `date`, `title`, `url`, `pageNumber`, `totalPages` classes. Own styles required; `<base>` rejected |
| `options.tagged` | boolean | `true` | Request structural tagging |
| `options.outline` | boolean | `true` | Build a document outline from headings |
| `options.media` | `"print"` or `"screen"` | `print` | CSS media emulated during rendering |
| `options.javaScript` | boolean | `true` | `false` disables scripts for this render. `true` cannot grant permission; the API key decides |
| `options.locale` | string | `en-CA` | A .NET-recognised, non-invariant culture name, up to 64 characters |
| `options.timeZone` | string | `America/Edmonton` | An IANA time zone identifier, up to 128 characters |
| `options.readiness.selector` | string or null | `null` | Wait for a matching element; 1–4096 characters |
| `options.readiness.function` | string or null | `null` | Wait for a truthy JavaScript expression; 1–16384 characters; requires effective JavaScript, otherwise 400 |
| `options.readiness.delayMs` | integer | `0` | Extra pause after other conditions; must fit inside `readiness.timeoutMs` |
| `options.readiness.timeoutMs` | integer | `5000` | Budget for all readiness conditions; 1 to the server maximum |
| `options.timeoutMs` | integer or null | server default (30000) | Overall processing deadline, 100 to the server maximum (60000 by default) |

Everything is validated before a browser context is opened. Validation failures
return 400 with a field-keyed `errors` object.

### PDF response

```http
HTTP/1.1 200 OK
Content-Type: application/pdf
Content-Disposition: attachment; filename=orders-2026-10.pdf; filename*=UTF-8''orders-2026-10.pdf
Cache-Control: no-store
X-Render-Pages: 2
X-Render-Duration-Ms: 1091
```

`X-Render-Duration-Ms` measures processing from admission through page counting,
including queue time. `Content-Disposition` always uses `attachment` to carry the
sanitized filename to the caller. The application sets its own browser response
headers to choose display or download.

### Print receipt

When `printer` is supplied and the caller holds a valid API key, a successful
request returns **202 Accepted** with `application/json`:

```json
{ "jobId": "64e5b7e07c8e4bdb86521bed5cd54d4c", "printer": "shipping", "status": "submitted", "pageCount": 1 }
```

`submitted` confirms acceptance by the print helper. Check printing progress in
the Windows or CUPS queue. See [Printing](../printing.md).

### Status codes

Errors use `application/problem+json` with `title`, `detail`, and, for validation,
an `errors` object keyed by field path.

| Status | Meaning | Notes |
|---|---|---|
| 200 | Rendered | PDF body and headers above |
| 202 | Print job submitted | JSON receipt |
| 400 | Invalid JSON, missing field, invalid option, `<base>` element, unknown printer, or a readiness function without JavaScript | `errors` names the fields |
| 401 | Missing, invalid, empty, or duplicate `X-Api-Key` | Also for printing without a key |
| 413 | Request body over the 25 MiB application limit or the 32 MiB host limit | Host rejections may not be JSON |
| 415 | Body is not uncompressed UTF-8 `application/json` | |
| 422 | A readiness condition failed or page execution raised an error | Bounded diagnostics, no page text in logs |
| 429 | Render capacity exhausted | `Retry-After` header; readiness stays healthy |
| 502 | Printer helper reported a submission failure | Printing only |
| 503 | Renderer starting, restarting, or shutting down; or printer helper unavailable | `Retry-After` when a response can be sent |
| 504 | Overall processing deadline expired | Pages may already have reached a print spooler |

A caller that disconnects is logged with status 499. Native IIS rejections and
forced process termination cannot be guaranteed to produce problem details.

### Deadlines and capacity

One deadline, `options.timeoutMs`, covers waiting in the queue, loading the
document, readiness, PDF generation, and page counting. All stages share that
deadline; response transmission has a separate lifetime. Two renders run concurrently and
eight more may wait by default; the eleventh concurrent request receives 429. The
admission permit is held until the response has been sent, so slow clients count
against capacity. The [Configuration guide](../configuration.md#rendering-limits)
explains the settings.

## GET /v1/health

Returns 200 `{"status":"ready"}` only after a successful warm-up while the browser
is available, and 503 `{"status":"unavailable"}` during startup, browser
replacement, failure, or shutdown. Readiness stays healthy during queue saturation;
render requests beyond capacity receive 429. The anonymous endpoint supports IIS
Application Initialization and load balancer probes.

## GET /v1/info

```json
{
  "version": "1.1.0+d57eddc…",
  "playwrightVersion": "1.64.1",
  "chromiumVersion": "156.0.8078.4",
  "active": 0, "queued": 0, "admitted": 0, "admissionLimit": 10,
  "concurrency": 2, "queueLimit": 8,
  "maxBodyBytes": 26214400, "maxTimeoutMs": 60000,
  "rendersSinceStartup": 42,
  "lastBrowserRestartUtc": "2026-10-31T06:36:10Z",
  "pageSizes": ["Letter", "Legal", "Tabloid", "A3", "A4", "A5"],
  "fonts": [{ "family": "Noto Sans", "weight": "400", "style": "normal" }, …],
  "jobs": { "enabled": false, "queued": 0, "rendering": 0, "stored": 0, "storedBytes": 0, "workers": 1, "maxQueued": 200 }
}
```

`fonts` lists only the packaged faces guaranteed by the release. `storedBytes`
includes a per-job metadata reserve. Readiness stays healthy when the queue or
storage budget is full.

## GET /v1/printers

Returns `{"printers":["pdf-test","shipping"]}`: the configured allowlist, sorted,
for callers presenting a valid API key. Use Windows or CUPS to inspect queue health.

## Request size and encoding

The complete encoded JSON body, including escaped HTML and templates, counts
against the 25 MiB application limit; the host applies a 32 MiB outer ceiling.
Request compression is not supported. Large embedded images are the usual reason
to approach the limit; prefer SVG or appropriately sized raster `data:` URLs.

## Asynchronous jobs (opt-in)

When the server sets `Jobs:Enabled` to `true`, these routes are registered;
otherwise they return 404. They use the configured access mode. A job's owner is
the API key that submitted it: another key always receives 404, including on
deletion. Anonymous callers share one owner and can list and read each other's
jobs, so use API-key mode for private work.

| Endpoint | Response |
|---|---|
| `POST /v1/jobs` | Same body and validation as `/v1/render`; 202 with the job JSON, a `Location` header and `Retry-After`. Queue or quota pressure returns 429 with `Retry-After` |
| `GET /v1/jobs/{id}` | The job JSON; `Retry-After` while queued or rendering; 404 for an unknown or another owner's ID |
| `GET /v1/jobs/{id}/result` | The PDF with `Content-Disposition`, `X-Render-Pages`, `X-Render-Duration-Ms` and `Cache-Control: no-store`. 409 with `Retry-After` before completion or while another download is in progress; 410 after cancellation or expiry |
| `DELETE /v1/jobs/{id}` | 204. Cancels queued or active work, or deletes a stored result. Repeating it is still 204 while the metadata exists; unknown or other-owner IDs return 404 |
| `GET /v1/jobs?status=&limit=&after=` | `{ items, nextAfter }`: the caller's jobs, newest first. `limit` is 1 to 100 (default 50); `after` is the last ID returned |

### Submitting

Job IDs are 128 random bits written as 32 lowercase hex characters. A submission
may carry one `Idempotency-Key` header of 1 to 128 visible ASCII characters. The
same owner and key return the existing job for as long as it is retained, including
in a terminal or tombstone state, and the first body wins. Use a new key for
distinct work. Print jobs require an API key and a configured printer identifier,
exactly as synchronous printing does.

### The job document

A job has `id`, `status` (`queued`, `rendering`, `succeeded`, `failed`, `cancelled`,
`expired`), `createdUtc`, optional `startedUtc` and `completedUtc`, `expiresUtc`,
`fileName` and `links`. Queued jobs expose `queuePosition`, the
global submission order; treat it as an estimate because scheduling rotates
between owners. Succeeded jobs include `pageCount` and `durationMs`. Failed jobs
carry an `error` with `type`, `title`, `status`, `detail`, `code` and `retryable`.
Print jobs include `printer` and a `printJob` receipt, and their result route
returns 409 because no PDF is retained. Failed jobs also return 409 on the result
route; decide whether to retry from the job's `error`, not from `Retry-After`.

### Scheduling and restarts

The rendering timeout starts when a job is dispatched. `Jobs:MaxQueueAge` limits
its wait in the queue. Workers take jobs in submission order and rotate between
owners, using the slots available after reserving synchronous capacity. After a
restart, queued jobs resume and jobs that were rendering are marked failed with
the code `interrupted`. Callers poll for status and decide whether to resubmit a
failed job. An interrupted print submission may already have reached the spooler;
check the queue before resubmitting.

### Retention

The submitted HTML is deleted as soon as a job succeeds. PDFs and the HTML of
failed jobs follow the [configured retention](../configuration.md#asynchronous-jobs).
Expired metadata remains as a tombstone for one extra `ResultRetention` period, so
the result route returns 410 before the ID finally returns 404. With
`DeleteResultOnDownload` on, a job expires after one complete successful download;
only one download per job is admitted at a time. Because the server cannot know
whether the client received every byte, leave that option off when a download may
need to be repeated.
