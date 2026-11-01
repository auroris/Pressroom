# Security model

Pressroom renders HTML from your applications on a server you control. Its browser
isolation, authentication and resource limits work alongside the host permissions
and network policy you configure for the deployment.

## Who is trusted

- **Calling applications** are trusted parties identified by API keys. They own
  their templates, their users' authorization, and what ends up in a document.
- **Documents** run in a fresh, isolated browser context with the controls below.
  These controls apply to every document, including templates and upstream user input.
- **The server** holds the keys and the browser. Run it behind HTTPS under a
  least-privileged identity, using the deployment recommendation below for its exposure.

## Document isolation

Each render receives its own browser context, including cookies and storage. The
context is disposed when the request ends. Its controls are:

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

The bundled Chromium can fail a render when a header or footer template attempts
to load an external resource: printing either rejects the template or reaches the
request deadline. A WebRTC attempt inside a `srcdoc` or blank child frame can also
crash that render under the connection policy. In both cases the caller receives
an error that excludes document content, the browser context closes, and later
renders proceed normally.

## Authentication

- API-key mode requires a valid `X-Api-Key` on rendering, service information,
  OpenAPI, and printer endpoints. `/v1/health` is always anonymous so IIS can
  warm the application.
- Secrets are compared in constant time, must be unique, and are identified in logs
  only by their nonsecret key identifier.
- A supplied invalid, empty, oversized, or duplicate key header returns 401 in
  either mode. Under IIS, Pressroom disables automatic Windows authentication and
  applies its configured access policy to every caller.
- Anonymous mode must be selected explicitly. It still rejects invalid keys,
  always renders without scripts, and never allows printing without a key.

## Retention and logging

- Synchronous PDF-only requests process HTML and PDF bytes in memory, including
  page counting.
- Printer requests write the PDF to a private job directory under
  `<StateRoot>/temp` for SumatraPDF or CUPS and remove it after submission. Like
  job storage, print directories are owner-only: mode 0700 on Linux, and on Windows
  a protected access list granting only the service identity. The OS spooler may
  retain data independently.
- Logs are metadata only: identifiers, sizes, timings, page counts, outcomes, and
  blocked-resource counts. They never include document bodies, PDF bytes, secrets,
  blocked URLs, or page console and error text. Page diagnostics returned to the
  caller on 422 are bounded.
- Chromium may create temporary files in its private working directory, and the
  operating system may page memory.

## Job storage and containers

With jobs enabled, HTML/PDFs live under `<StateRoot>/jobs/<id>` for configured
retention. Linux uses 0700 directories/0600 files; Windows uses a protected
service-SID DACL. Use volume encryption for encryption at rest. Successful jobs
remove HTML; failed input and results follow [retention settings](configuration.md).
Storage is locked to one instance. With persistent storage, queued jobs survive
restart; active jobs become failed/interrupted. Interrupted printing may have
reached the OS spooler.

Owners are API key identifiers. Rotating a secret under the same identifier keeps
access; reusing an identifier grants access to its prior jobs. Other keys get 404
and cannot list those jobs. Anonymous jobs share one principal: any anonymous
caller can list and retrieve them, including callers who know an ID. Prefer
API-key mode. Cancellation cannot retract already-spooled pages.

The Linux container recipe uses a non-root identity, a read-only root, and temporary
memory-backed state. Windows uses ContainerUser and a disposable writable layer;
stop and recreate it to discard state. See [containers](container.md) for the
lifecycle differences. Egress policy belongs to a firewall or NetworkPolicy.
Callers poll for job status and download results through the API. Job logs record metadata.

## Resource protection

- The complete JSON body is capped at 25 MiB by the application and 32 MiB by the
  host, and admission is bounded before the body is read, so a burst cannot
  allocate unlimited HTML.
- Two concurrent renders plus eight queued requests by default; beyond that,
  429 with `Retry-After` while readiness stays healthy.
- Every request has one end-to-end deadline (30 s by default, 60 s maximum). A
  timeout or client disconnect closes the browser context. A stuck cleanup
  triggers supervised browser replacement.
- The browser is recycled after a planned number of renders, draining in-flight
  work first.

## Deployment recommendations

- **Internet-facing use:** use [Linux Docker with iptables isolation](container.md#optional-linux-outbound-isolation),
  API-key authentication, and an HTTPS reverse proxy in front of the loopback-bound
  API. Enable the documented outbound blocking for this Docker deployment. A
  [direct Linux installation](linux.md) is also suitable when it runs under a
  dedicated unprivileged user, with API keys and HTTPS.
- **Intranet use:** Windows/IIS installations and Windows containers are intended
  primarily for internal networks. Both Linux options also work here. Use HTTPS
  and application-specific keys. Host firewall configuration remains an optional
  deployment choice.
- On IIS, run a dedicated application pool identity with read and execute access
  to the release folder and the documented state-directory permissions.
  See [Deployment](deployment.md).
- Store the settings file outside the release folder, restricted to the service
  identity and administrators. Keep secrets out of `web.config`, `appsettings.json`,
  and publish profiles.
- Give each calling application its own key, and opt in `AllowJavaScript` only
  where scripts are needed.
- Verify every release's `release-manifest.json` hashes and the archive checksum
  before deployment.

<a id="what-is-not-guaranteed"></a>

## Deployment responsibilities

- **Operating-system egress isolation.** Enforce strict outbound restrictions with
  an independently tested host network policy, such as firewall rules on the
  worker identity. The [container guide](container.md) documents optional iptables
  rules on Linux and HNS ACLs on Windows. Apply the isolated deployment recipe to
  add these host controls to Pressroom's browser and application controls.
- **Browser updates and host isolation.** Keep the release current and isolate the
  server to limit exposure to browser exploits. Chromium's sandbox and the fresh
  context for each render provide additional containment.
- **HTML sanitisation.** Validate and sanitise content in the calling application.
  Pressroom's document checks reject `<base>` and block resource fetching; they
  are not general-purpose HTML sanitisation.
- **PDF/UA conformance.** Validate the source HTML and resulting PDF against your
  accessibility requirements. Pressroom requests tagged output by default;
  conformance depends on the source HTML.

The controls above are exercised by automated tests against real Chromium on every
release build. The [Windows / IIS acceptance checklist](windows-acceptance.md) and
the Linux [acceptance checklist](linux.md#acceptance-checklist) list the checks a
deployment should repeat on the target server.
