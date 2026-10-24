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

Use both a valid key and an authorized domain caller. Inspect status codes and
`WWW-Authenticate` headers, not just browser sign-in behavior.

| Request | Expected |
|---|---|
| Valid key, no Windows credentials | 200 on `/v1/info` and successful render |
| Invalid key, Windows credentials available | 401; no Negotiate/NTLM challenge and no Windows fallback |
| Present but empty key, Windows credentials available | Same failure as invalid key |
| No key, authorized Windows caller | Success |
| No key, anonymous caller with Windows mode enabled | Windows challenge / 401 |
| Windows caller outside a required group | 403 |
| Valid key with Windows group restriction enabled | Success; group restriction is Windows-only |

Verify `/v1/health` remains anonymous and `/v1/info` and OpenAPI remain protected.
Windows-only browser tests or fake authentication in an API test host do not
replace these cases.

## Limits, cancellation, and resources

Exercise bodies near the 25 MiB application ceiling and above the 32 MiB host
ceiling. Measure the full UTF-8 JSON body, including escaped HTML and templates.
Application-level oversized bodies should return 413 problem details; an IIS
outer-limit rejection may use a native IIS response.

Fill active and waiting capacity, verify 429 and `Retry-After`, and confirm queue
saturation alone does not make `/v1/health` fail. Cancel queued and active requests,
including an expensive script/print operation. Confirm capacity is actually
released and follow-up renders complete. Readiness failures should return 422;
the overall deadline should return 504 and terminate the request's browser work.

Try HTTP(S), local-file, frame/popup, CSS, fetch/XHR, WebSocket, and service-worker
resource access from controlled fixture documents. Confirm the intended blocking
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
both auth branches, and a PDF after each operation. Keep configuration, logs, and
temporary state outside the replaceable site directory. A partial file-copy or
warm-up failure must not be treated as a successful deployment.

## Result

Record **passed**, **failed**, or **not run** for each area, with the exact artifact,
machine inventory, measurements, and outstanding limitations. A diagnostic host
validation bypass, static DLL scan, or successful developer-machine publish alone
does not satisfy this gate. Requalify after changing the browser or native runtime
payload; qualify the self-contained profile separately if it is released.
