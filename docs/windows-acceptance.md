# Windows / IIS release acceptance

The Visual Studio publish check proves that the selected payload can launch and
render on the publishing machine. It does not prove a clean IIS deployment or
the absence of hidden machine prerequisites. Complete this record against the
actual published files before calling a release qualified.

## Record the environment

Record the release manifest, publish profile, Windows Server edition/build,
.NET/ASP.NET Core and Hosting Bundle versions, IIS configuration, pool identity,
machine-installed Visual C++ runtime inventory, Media Foundation feature state,
browser version, font manifest, and configured rendering limits. Preserve the
exact artifact and this result with the release.

Start with a supported clean Windows Server baseline containing IIS and the .NET
10 Hosting Bundle. Media Foundation must be absent for the standard qualification.
Do not add another machine-wide Visual C++ runtime to make the test pass without
first recording the exact missing dependency. The Hosting Bundle's own baseline
components are part of the stated prerequisite; document them separately.

Use a fresh location with no developer Playwright browser cache and no dependency
validation markers. Do not provide Node/npm, the .NET SDK, PowerShell 7, or network
download access to the application. Grant the real pool identity read/execute
access to the release and write access only to its external runtime directories.
Keep normal Playwright validation and `ChromiumSandbox=true` enabled.

## Cold start and published payload

- Start the actual IIS application and confirm `/v1/health` becomes 200 after
  warm-up. Confirm that `/v1/info` matches the expected release/browser/font
  versions. A build-directory run is not a substitute.
- Submit `samples/orders-bilingual.request.json` through the real site and verify
  a PDF response, two pages, Letter portrait dimensions, page numbers, vector
  graphic, bold/italic faces, French accents/ligatures, outlines, and requested
  tagging. Inspect the generated PDF visually and with your PDF inspection tool.
- The sample requests `disposition: "inline"`. Verify that response's
  `Content-Disposition`, repeat with `"attachment"`, and repeat with the property
  omitted to check the attachment default. Confirm safe filenames in both modes.
  If a server-side caller returns the PDF onward, check its browser response too:
  it must carry the chosen mode, not just copy the PDF bytes. Invalid disposition
  values must return 400. Browser PDF settings can affect inline display.
- Verify a caller-embedded image and font in addition to the packaged Noto Sans
  faces. Verify generic header/footer typography independently; page CSS and fonts
  do not automatically apply to Chromium's header/footer templates. Custom embedded
  header/footer fonts are not supported in this initial build: an observed Linux
  test fell back to a system font despite its data-backed font rule. Any later
  supported custom-header/font feature needs a new explicit acceptance test.
- Restart the pool and repeat. Confirm owned browser/driver processes from the
  old worker exit; unrelated machine browsers must remain untouched.
- Confirm Windows Job Object assignment works under the real IIS identity,
  including any nesting imposed by IIS. The renderer assigns its private Node
  process before browser launch and tracks its descendants in that job; inability
  to establish that ownership must fail startup. Exercise shutdown/crash cleanup
  and confirm the owned job reaches zero active processes.
- Confirm the release directory is not writable to the pool and that startup
  still succeeds without writing a dependency-validation marker there.

## Real IIS authentication

Enable IIS Anonymous Authentication and disable Windows Authentication. Configure
each access mode in the external JSON file and restart between modes. Inspect
status codes and `WWW-Authenticate` headers.

| Mode and request | Expected |
|---|---|
| `ApiKey`: valid configured key | 200 on `/v1/info` and successful render |
| `ApiKey`: missing key | 401, including callers with Windows credentials |
| Either mode: invalid, empty, duplicate, or oversized key | 401; no Negotiate/NTLM challenge |
| `Anonymous`: no key | Successful render, `/v1/info`, and OpenAPI access |
| `Anonymous`: valid configured key | Success |
| `ApiKey`: no keys configured | Startup fails with a configuration error |
| Either mode: key with omitted or false `AllowJavaScript`, and omitted or true request `javaScript` | Static render; document scripts do not execute |
| `Anonymous`: omitted or true `javaScript` without a key | Static render; document scripts do not execute |
| Either mode: key with `AllowJavaScript: true`, and omitted or true request `javaScript` | Chart scripts and readiness expression execute |
| Either mode: opted-in configured key plus `javaScript: false` | Static render; document scripts do not execute |
| Invalid key settings or duplicate configured secrets | Startup fails with a configuration error |

Verify `/v1/health` remains anonymous in both modes. Verify `/v1/info` and OpenAPI
require a valid key in API-key mode. API tests with a controlled host do not
replace these deployment checks.
Use a document with inline scripts, data-backed scripts, event handlers and child
frames to confirm they do not execute when JavaScript is disabled. Check that
static text, embedded images, CSS and packaged fonts still render. A caller
readiness function with JavaScript effectively disabled must return 400, including
when the request says `javaScript: true` but the key lacks permission.

Change a rendering limit and a valid key in the external JSON file, restart, and
confirm both changes take effect. Check that the application reads the file named
by `ConfigurationFile`, fails startup for an explicitly selected missing file,
and does not take application-setting overrides from environment variables.

## Printer output

For a desktop test, configure `Printing:SumatraPdfPath` to an installed or portable
SumatraPDF executable and add `Printing:Printers:pdf-test` with the value
`Microsoft Print to PDF`. Start Pressroom interactively with an API key. Submit
`samples/orders-bilingual.request.json` with `"printer": "pdf-test"`, allowing
enough processing time to complete the printer's **Save Print Output As** dialog.
Save to an absolute path outside the temporary job directory. Expect 202 JSON,
`status: submitted`, a correlation `jobId`, `printer: pdf-test`, and `pageCount: 2`.
Inspect both pages of the saved output. The returned job ID is not a spooler ID,
and the response does not confirm physical printing.

On 2026-10-30, the local Windows test passed with SumatraPDF 3.6.1 and Microsoft
Print to PDF. The user completed the save dialog, the API returned a two-page
submission receipt, and the output was preserved under
`artifacts/print-test/microsoft-print-to-pdf.pdf`. A nonexistent configured Windows
printer returned 502, and an anonymous print request returned 401. This desktop
test does not establish IIS identity or physical-printer acceptance.

For production acceptance, repeat against an actual configured printer under the
dedicated IIS identity, which needs print permission and access to SumatraPDF.
Verify unavailable queues, denied permissions, cancellation, and request timeout;
check that the helper exits and temporary input PDFs are removed. Queued pages may
still print after a cancellation: inspect the Windows queue before retrying.
Microsoft Print to PDF normally prompts for a filename and is unsuitable for
unattended IIS use. Verify `GET /v1/printers` and print requests require a valid key
even in anonymous mode, and that PDF-only requests continue to work.

## Limits, cancellation, and resources

Exercise bodies near the 25 MiB application ceiling and above the 32 MiB host
ceiling. Measure the full UTF-8 JSON body, including escaped HTML and templates.
Application-level oversized bodies should return 413 problem details; an IIS
outer-limit rejection may use a native IIS response.

Fill active and waiting capacity, verify 429 and `Retry-After`, and confirm queue
saturation alone does not make `/v1/health` fail. Cancel queued and active requests,
including an expensive script/print operation using an opted-in key. Confirm capacity is actually
released and follow-up renders complete. Readiness failures should return 422;
the overall deadline should return 504 and terminate the request's browser work.

Try HTTP(S), local-file, frame/popup, CSS, fetch/XHR, WebSocket, and service-worker
resource access from controlled fixture documents. Run script-based probes with
an opted-in key so the attempts actually execute. Confirm the intended blocking
and confirm logs contain no raw URLs, document text, page errors/console text,
PDF content, or keys. A strict no-egress requirement also needs an independently
enforced network policy.

## Recycle, update, rollback, and soak

Perform at least 1,000 representative successful renders at configured concurrency,
crossing planned browser-recycle thresholds. Record errors/retries, memory peaks,
process counts, response times, and post-idle memory. Include slow clients so
buffered PDF response admission is exercised. Confirm bounded memory/process
growth rather than judging only total throughput.

Trigger an IIS recycle during traffic and verify readiness/recovery and cleanup.
Test an owned-browser crash and supervised replacement. Budget for IIS overlapped
worker processes or explicitly choose non-overlapped recycling.

Publish an update using your normal Visual Studio profile, then restore the entire
previous release using that same transport. Verify readiness, version identity,
the configured access mode, and a PDF after each operation. Keep configuration, logs, and
temporary state outside the replaceable site directory. A partial file-copy or
warm-up failure must not be treated as a successful deployment.

## Result

Record **passed**, **failed**, or **not run** for each area, with the exact artifact,
machine inventory, measurements, and outstanding limitations. A diagnostic host
validation bypass, static DLL scan, or successful developer-machine publish alone
does not satisfy this gate. Requalify after changing the browser or native runtime
payload; qualify the self-contained profile separately if it is released.
