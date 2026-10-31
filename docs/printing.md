# Printing to Windows printers

Besides returning PDFs, Pressroom can render a document and hand the pages to an
allowlisted Windows printer. Printing reuses the normal render request: add a
`printer` identifier, authenticate with an API key, and receive a JSON submission
receipt instead of PDF bytes.

## Requirements

- **SumatraPDF** on the server. Install
  [SumatraPDF](https://www.sumatrapdfreader.org/free-pdf-reader) or place its
  portable executable in a folder the service can execute from. It is an optional
  dependency used only for printer output and is not bundled with Pressroom.
- **An API key.** Every valid configured key may print. Anonymous callers cannot
  print or list printers, even when `Auth:Mode` is `Anonymous`.
- **Printers visible to the service identity.** A printer installed only for an
  interactive user may not be available to the IIS application pool.

## Configure the allowlist

Add the SumatraPDF path and the printers to the external settings file, then
restart Pressroom:

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

Each key is a caller-facing identifier of 1–128 ASCII letters, digits, underscores,
and hyphens, matched ignoring case. Each value is the exact name of a Windows
printer. No printer is enabled by default.

Grant the application pool identity execute access to SumatraPDF, print access to
the selected queues, and Modify access to `temp` under `Paths:StateRoot`. Configure
paper, tray, duplex, and other device defaults on the Windows printer itself: the
render options decide the content of the pages, and SumatraPDF uses the printer's
own settings to place them on paper. Qualify the output on the intended device.

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
directory under `<StateRoot>\temp`, and invokes SumatraPDF's
[`-print-to` command](https://www.sumatrapdfreader.org/docs/Command-line-arguments).
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

`submitted` means Windows accepted the job. It does not mean paper has finished
printing. `jobId` is Pressroom's correlation identifier and the temporary PDF's
basename, not a Windows spooler job identifier. There is no status endpoint:
offline devices and later spooler errors are managed through the Windows queue.

`GET /v1/printers` returns the configured identifiers to authenticated callers,
for example `{"printers":["pdf-test","shipping"]}`. It lists the allowlist, not
live queue health.

## Errors and retries

| Status | Meaning |
|---|---|
| 400 | Unknown printer identifier (checked before rendering) |
| 401 | Missing or invalid API key |
| 502 | SumatraPDF reported a submission failure |
| 503 | SumatraPDF or its local resources are unavailable |
| 504 | The processing deadline expired during rendering or submission |

The normal admission limit and processing timeout cover both rendering and
submission. On cancellation or timeout the helper is stopped and the temporary
files are removed after it exits; the Windows spooler may retain document data
independently.

A timeout, disconnect, or failure can occur **after some or all pages reached the
spooler**. Check the queue before retrying. Pressroom does not deduplicate or
automatically retry print requests, and neither does the .NET client's
`PrintAsync`.

## Local test

`Microsoft Print to PDF` is a convenient manual target. Select it as `pdf-test` and
complete its **Save Print Output As** dialog before the request times out. Because
it prompts for a destination file, it is a manual test target, not an unattended
IIS destination. The [acceptance record](windows-acceptance.md) includes the
printer checks for a real deployment.
