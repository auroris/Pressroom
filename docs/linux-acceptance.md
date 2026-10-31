# Linux acceptance record

Status: **not yet qualified on a clean Linux VM**. This is a gate, not a claim
of a passing deployment. Record host/kernel, artifact hash/version, runtime,
browser, date, and each result.

| Area | Evidence | Result |
|---|---|---|
| Clean Ubuntu 24.04 and Debian 13 | Documented runtime/libraries/fonts only; tarball check as non-root, sandbox on | Not run |
| Access | Health 200, valid key accepted, bad/missing key 401, explicit anonymous mode | Not run |
| Bilingual output | Two pages, Noto text and readable footer; compare extracted text/page count with Windows | Not run |
| Printing | Empty discovery, request 400, nonempty printing settings fail startup | Not run |
| Limits | 413, 429/Retry-After, readiness during saturation, cancellation cleanup | Not run |
| Shutdown | SIGTERM during two renders; no owned node/headless-shell within ShutdownTimeoutMs + 5 s | Not run |
| Recovery | Browser crash, planned recycle, readiness recovery, no child-process growth | Not run |
| Jobs | 500 jobs plus sync, sync p95 at most twice idle; restart 100 queued + 1 active; owner isolation/quota/retention | Not run |
| Soak | 1,000 representative renders; memory, process count, latency and idle recovery | Not run |
| Updates | Stop, replace complete version, check, restart and roll back; state preserved | Not run |

CI runs real-browser and server tests sandboxed as a non-root user in an Ubuntu
container with the supplied seccomp profile, on a disposable host that permits
user namespaces. This does not fill in the clean-VM checks. Never qualify a
release using disabled sandboxing or bypassed host validation.
