# Security model

Pressroom is built for one job on a trusted network: applications you control send
HTML, and a server you control turns it into PDF. This page describes the
boundaries it enforces, what it deliberately does not promise, and how to deploy it
accordingly.

## Who is trusted

- **Calling applications** are trusted parties identified by API keys. They own
  their templates, their users' authorization, and what ends up in a document.
- **Documents** are not trusted. Every render runs in a fresh, isolated browser
  context with the controls below, whether the HTML came from a template or from
  user input upstream.
- **The server** holds the keys and the browser. It should sit on an internal
  network behind HTTPS, under a least-privileged identity.

## Document isolation

Each render receives a new nonpersistent browser context that is disposed when the
request ends. No cookies, storage, or state are shared between renders. Inside that
context:

| Control | Effect |
|---|---|
| Resource policy | Only `data:` resources load. Network, local-file, and `blob:` URLs are blocked before parsing, including in nested documents |
| Content Security Policy | Delivered by header before the document is parsed: inline styles and data resources allowed; workers, objects, forms, and base URLs denied; sandbox without same-origin or popup privileges |
| Connection allowlist | An empty allowlist blocks document-initiated connections, including WebRTC, WebTransport, DNS prefetch, and preconnect. Startup verifies that the launched browser enforces it and fails otherwise |
| Offline context and interception | Request and WebSocket interception plus offline mode as additional barriers |
| Disabled features | Downloads, popups, forms, service workers, and origin-backed storage |
| `<base>` elements | Rejected with 400 so relative URLs cannot reach a different origin |
| JavaScript | Off by default. Enabled only for API keys with `AllowJavaScript: true`; a request can further disable it, never enable it |
| Navigation | Navigating the main document away from the submitted HTML closes the page |
| Chromium sandbox | On (`Render:ChromiumSandbox: true`). A failed sandboxed launch is a startup error; `--no-sandbox` is not a supported workaround |

Two known behaviours of the pinned Chromium build surface as render errors rather
than weaknesses: a header or footer template that attempts an external load can be
rejected at print time, and a WebRTC attempt inside a `srcdoc` or blank child frame
can crash that render under the connection policy. Both return a content-free
document error, the context is closed, and later renders remain usable.

## Authentication

- API-key mode requires a valid `X-Api-Key` on rendering, service information,
  OpenAPI, and printer endpoints. `/v1/health` is always anonymous so IIS can
  warm the application.
- Secrets are compared in constant time, must be unique, and are identified in logs
  only by their nonsecret key identifier.
- A supplied invalid, empty, oversized, or duplicate key header returns 401 in
  either mode. There is no fallback to a Windows identity and no Negotiate or NTLM
  challenge; under IIS, Pressroom disables automatic Windows authentication.
- Anonymous mode must be selected explicitly. It still rejects invalid keys,
  always renders without scripts, and never allows printing without a key.

## Retention and logging

- Synchronous PDF-only requests do not deliberately store input HTML or output PDFs. Renders
  write no PDF file; the page count is read from the in-memory document.
- Printer requests write the PDF to a private job directory under
  `<StateRoot>\temp` for SumatraPDF and remove it after submission. The Windows
  spooler may retain data independently.
- Logs are metadata only: identifiers, sizes, timings, page counts, outcomes, and
  blocked-resource counts. They never include document bodies, PDF bytes, secrets,
  blocked URLs, or page console and error text. Page diagnostics returned to the
  caller on 422 are bounded.
- Chromium may create temporary files in its private working directory, and the
  operating system may page memory. Pressroom does not claim zero disk writes.

## Job storage and containers

With jobs enabled, HTML/PDFs live under `<StateRoot>/jobs/<id>` for configured
retention. Linux uses 0700 directories/0600 files; Windows uses a protected
service-SID DACL. Use volume encryption for encryption at rest. Successful jobs
remove HTML; failed input and results follow [retention settings](configuration.md).
Storage is locked to one instance. Queued jobs survive restart; active jobs become
failed/interrupted. Interrupted printing may have reached the Windows spooler.

Owners are API key identifiers. Rotating a secret under the same identifier keeps
access; reusing an identifier grants access to its prior jobs. Other keys get 404
and cannot list those jobs. Anonymous jobs share one principal: any anonymous
caller can list and retrieve them, including callers who know an ID. Prefer
API-key mode. Cancellation cannot retract already-spooled pages.

Containers add a non-root identity and read-only root with writable state volume.
Egress policy still belongs to a firewall or NetworkPolicy. No callbacks or
outbound job delivery are implemented. Job logs remain metadata only.

## Resource protection

- The complete JSON body is capped at 25 MiB by the application and 32 MiB by the
  host, and admission is bounded before the body is read, so a burst cannot
  allocate unlimited HTML.
- Two concurrent renders plus eight queued requests by default; beyond that,
  429 with `Retry-After` while readiness stays healthy.
- Every request has one end-to-end deadline (30 s by default, 60 s maximum). A
  timeout or client disconnect closes the browser context rather than leaving
  work running, and a stuck cleanup triggers supervised browser replacement.
- The browser is recycled after a planned number of renders, draining in-flight
  work first.

## Deployment recommendations

- Keep Pressroom on an internal network and terminate HTTPS at IIS; API keys and
  document content travel in the clear otherwise.
- Run a dedicated application pool identity with read and execute access to the
  release folder, read access to the settings file, and Modify access only to
  `temp` and `logs`. See [Deployment](deployment.md).
- Store the settings file outside the release folder, restricted to the pool
  identity and administrators. Keep secrets out of `web.config`, `appsettings.json`,
  and publish profiles.
- Give each calling application its own key, and opt in `AllowJavaScript` only
  where scripts are needed.
- Verify every release's `release-manifest.json` hashes and the ZIP checksum before
  deployment.

## What is not guaranteed

- **Operating-system egress isolation.** The controls above are browser and
  application controls, verified by tests. A deployment that needs a strict
  no-network guarantee must enforce it independently, for example with firewall
  policy on the worker identity.
- **Protection against a browser exploit.** The Chromium sandbox and a fresh
  context per render limit exposure; they are not a substitute for keeping the
  release current and the server isolated.
- **HTML sanitisation.** Pressroom rejects `<base>` and blocks fetching; it does not
  rewrite or validate document content beyond that.
- **PDF/UA conformance.** Tagged output is requested by default, but conformance
  depends on the source HTML.

The [verification record](development/verification.md) documents the sandbox and
authentication checks that were run against real Chromium and IIS, and the
[acceptance record](windows-acceptance.md) lists the checks a deployment should
repeat on the target server.
