# Printing

Besides returning PDFs, Pressroom can render a document and hand the pages to an
allowlisted printer on Windows or Linux. Printing reuses the normal render request: add a
`printer` identifier, authenticate with an API key, and receive a JSON submission
receipt instead of PDF bytes.

## Requirements

- **Windows: SumatraPDF** on the server. Install
  [SumatraPDF](https://www.sumatrapdfreader.org/free-pdf-reader) or place its
  portable executable in a folder the service can execute from. It is an optional
  dependency used only for printer output and is not bundled with Pressroom.
- **Linux: CUPS and its command-line client**, with a working PDF-capable queue.
  Pressroom uses `/usr/bin/lp` by default, or CUPS `lpr` when configured below.
  These are optional host dependencies and are not added to the Docker image.
- **An API key.** Every valid configured key may print. Anonymous callers cannot
  print or list printers, even when `Auth:Mode` is `Anonymous`.
- **Printers visible to the service identity.** A printer installed only for an
  interactive user may not be available to the IIS application pool or Linux service.

## Configure the allowlist

Each allowlist key is a caller-facing identifier of 1–128 ASCII letters, digits,
underscores, and hyphens, matched ignoring case. Each value is an OS printer or
CUPS destination name. No printer is enabled by default. Update the external
settings file and restart Pressroom after changing the allowlist.

### Windows

Set the SumatraPDF path and Windows printer names:

```json
{
  "Printing": {
    "SumatraPdfPath": "C:\\Tools\\SumatraPDF\\SumatraPDF.exe",
    "Printers": {
      "shipping": "Warehouse Laser Printer",
      "pdf-test": "Microsoft Print to PDF"
    }
  }
}
```

Grant the application pool identity execute access to SumatraPDF, print access to
the selected queues, and Modify access to `temp` under `Paths:StateRoot`. Configure
paper, tray, duplex, and other device defaults on the Windows printer itself: the
render options decide the content of the pages, and SumatraPDF uses the printer's
own settings to place them on paper. Qualify the output on the intended device.

### Linux host

Install CUPS and the CUPS client tools using your distribution's packages, then
configure and test the intended queue. Run `lpstat -p -d` as the Pressroom service
user to check which queues that identity can access. For example:

```json
{
  "Printing": {
    "CupsCommandPath": "/usr/bin/lp",
    "Printers": {
      "shipping": "Warehouse_Laser"
    }
  }
}
```

`CupsCommandPath` defaults to `/usr/bin/lp`; set it to `/usr/bin/lpr` to use CUPS
`lpr`. It must be an absolute Linux path whose executable is named `lp` or `lpr`,
with no command arguments. `SumatraPdfPath` is ignored on Linux, and
`CupsCommandPath` is ignored on Windows. A helper is only needed when printing
is configured; ordinary PDF rendering does not require CUPS.

The service user needs permission to execute the helper, submit to the queue,
and write under `Paths:StateRoot`. CUPS must accept submissions from this user
without an interactive password prompt. Configure paper, tray, duplex, and other
defaults in CUPS for that identity. Verify a PDF with
`lp -d Warehouse_Laser /absolute/path/to/test.pdf` under the same identity before
using the API. Pressroom submits the PDF with normal CUPS filtering, so the queue
must support converting PDF for the device.

Linux printing is intended for a direct host installation. Pressroom does not
detect or block containers; the supplied Docker image has no CUPS client or
printer configuration.

## Submit a print job

Send a valid `X-Api-Key` and add `printer` to a normal render request:

```json
{
  "html": "<!doctype html><html><body><h1>Shipping document</h1></body></html>",
  "printer": "shipping",
  "options": { "pageSize": "Letter" }
}
```

`fileName` and `disposition` have no effect on printer output. Omitting `printer`
or setting it to `null` returns a PDF as usual.

Pressroom renders the document through Chromium, writes the PDF to a private job
directory under `<StateRoot>/temp`, and invokes SumatraPDF's
[`-print-to` command](https://www.sumatrapdfreader.org/docs/Command-line-arguments)
on Windows, or [`lp -d`](https://www.cups.org/doc/man-lp.html) /
[`lpr -P`](https://www.cups.org/doc/man-lpr.html) on Linux. Queue names and file
paths are passed as separate arguments without a shell. Job directories are
accessible only to the service user: mode 0700 on Linux, and a protected access
list on Windows.
When the helper reports success the response is **202 Accepted** with
`application/json`:

```json
{
  "jobId": "64e5b7e07c8e4bdb86521bed5cd54d4c",
  "printer": "shipping",
  "status": "submitted",
  "pageCount": 1
}
```

`submitted` means the print helper reported a successful submission. It does not mean paper has finished
printing. `jobId` is Pressroom's correlation identifier and the temporary PDF's
basename, not an OS spooler job identifier. There is no spooler status endpoint:
offline devices and later spooler errors are managed through the Windows or CUPS queue.

With asynchronous jobs enabled, `POST /v1/jobs` accepts the same `printer` field
and API-key requirement. A successful job contains a `printJob` receipt instead
of a downloadable PDF.

`GET /v1/printers` returns the configured identifiers to authenticated callers,
for example `{"printers":["pdf-test","shipping"]}`. It lists the allowlist, not
live queue health.

## Errors and retries

| Status | Meaning |
|---|---|
| 400 | Unknown printer identifier (checked before rendering) |
| 401 | Missing or invalid API key |
| 502 | The print helper reported a submission failure |
| 503 | The print helper or its local resources are unavailable |
| 504 | The processing deadline expired during rendering or submission |

The normal admission limit and processing timeout cover both rendering and
submission. On cancellation or timeout the helper is stopped and the temporary
files are removed after it exits; the OS spooler may retain document data
independently.

A timeout, disconnect, or failure can occur **after some or all pages reached the
spooler**. Check the queue before retrying. Pressroom does not deduplicate or
automatically retry print requests, and neither does the .NET client's
`PrintAsync`.

## Local test

`Microsoft Print to PDF` is a convenient manual target. Select it as `pdf-test` and
complete its **Save Print Output As** dialog before the request times out. Because
it prompts for a destination file, it is a manual test target, not an unattended
IIS destination. The [Windows acceptance checklist](windows-acceptance.md) and the
[Linux acceptance checklist](linux.md#acceptance-checklist) include the printer
checks for a real deployment.
