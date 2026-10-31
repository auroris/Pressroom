# Linux acceptance record

Verified on **2026-10-31** on the supplied Ubuntu 26.04.1 x64 VM, kernel
`7.0.0-42-generic`, eight virtual CPUs. Direct installation used Ubuntu's SDK
10.0.114 / ASP.NET Core 10.0.14, a root-owned release and an unprivileged systemd
service. The final smoke check also passed with SDK and PowerShell paths made
inaccessible to the service. Docker 29.1.3 / Compose 2.40.3 used one CPU, a
1.5 GiB memory limit, a read-only root and temporary memory-backed state.

Both used Playwright **1.64.1**, private Node **v24.22.1**, and bundled Chromium
**156.0.8078.4**. Sandboxing and dependency validation stayed enabled. Ubuntu's
global AppArmor user-namespace restriction stayed enabled; direct installation
used an executable-specific profile. The Linux suite passed 180 unique tests,
with 15 Windows-specific skips, both directly and in the unprivileged builder.

| Area | Evidence | Result |
|---|---|---|
| Ubuntu 26.04.1 | Published tarball as non-root, sandbox on | Passed |
| Clean Ubuntu 24.04 and Debian 13 direct-install VMs | Separate host qualification | Not run |
| Access | Health 200, valid key, bad/missing key 401, explicit anonymous mode | Passed both |
| Bilingual output | Two pages, Noto fonts, extracted text and rendered pages checked | Passed both |
| Printing (before CUPS support) | Empty discovery, request 400, nonempty printing settings rejected | Passed both; superseded by the host checks below |
| Limits | 413, 429/Retry-After, healthy during saturation, cancellation cleanup | Passed both |
| Shutdown | Two active renders; no owned live child processes | Direct 0.206 s; container 0.427 s |
| Startup | Lifecycle restart to ready | Direct 1.903 s; container 2.366 s |
| Recovery | Browser crash returned 503 then recovered; planned recycling | Passed both |
| Jobs | Owner isolation, quota, idempotency, expired result 410 | Passed both |
| Restarted jobs | 100 queued plus one active | Direct recovered 100 and marked active interrupted; container forgot all temporary jobs |
| Updates | Two versions/images and rollback, same external configuration | Direct retained job PDF; container discarded jobs as required |
| Optional isolation | Host/peer/internet/DNS/UDP with reachable positive controls | Container passed iptables/ip6tables rules |

## Linux host printing follow-up

On **2026-10-31**, the CUPS backend was tested on the same Ubuntu VM with the
service running as an unprivileged user. A temporary CUPS queue targeted
`ippeveprinter` on loopback; no physical printer was used. The queue and virtual
printer process were removed after testing. The existing Pressroom service and
its configuration were unchanged.

- `/usr/bin/lp` and `/usr/bin/lpr` each submitted one synchronous render and one
  asynchronous job through the real server and Chromium renderer.
- All four CUPS jobs completed and the virtual printer retained four valid PDFs.
  The render API returned 202 submission receipts; background jobs succeeded
  with `printJob` receipts and returned 409 for PDF downloads.
- Anonymous printing and printer discovery returned 401. Authenticated discovery
  exposed only the configured identifier. Temporary `print-*` directories were
  removed after submission.
- The Linux server suite passed 127 tests, with two skips (Windows virtual-printer
  behavior and the separate browser load test). The Windows server suite passed
  114 tests, with 15 Linux/browser skips. Coverage includes helper arguments,
  nonzero exits, missing/nonexecutable helpers, cancellation, private Linux
  directories, configuration, authentication, and background printing.

The API tests ran in a private mount namespace hiding `/etc/pressroom`, so they
could use test configuration without reading or changing the installed service's
protected settings. These results qualify submission to a virtual CUPS printer;
physical device output still requires target-printer qualification.

## Load measurements

Each scenario rendered 1,000 documents: 900 simple one-page documents and 100
two-page bilingual samples, then 500 background jobs alongside synchronous traffic.
These are VM acceptance measurements, not a general throughput guarantee.

| Measurement | Direct | Container, 1 CPU |
|---|---:|---:|
| 1,000-render elapsed time | 235.58 s | 460.26 s |
| Render p95 | 313.05 ms | 647.00 ms |
| Synchronous renders during 500 jobs | 570 | 501 |
| Idle synchronous p95 | 285.89 ms | 590.52 ms |
| Busy synchronous p95 | 294.09 ms | 899.22 ms |
| Busy / idle ratio, target ≤2 | 1.029 | 1.523 |
| Live processes / zombies after soak recovery | 8 / 0 | 8 / 0 |

Planned recycling produced one or two expected 503s per phase, retried and reported
separately by the harness. Direct-soak peak cgroup memory was 910,630,912 bytes.
Container memory peaked at 1,582,407,680 bytes across soak/jobs and fell to
804,962,304 bytes after idle recovery, with **zero OOM and OOM-kill events**.
These values include caches and temporary files. One transient recycling zombie
was gone at the post-soak check. Resize limits for actual document workloads.

## Provenance and final checks

- Direct tarball SHA-256: `61863281f33b0c3f3dac6aa0a2e3a686b6b7a233e8f2b6032db013fb9720e1a1`.
- Runtime-validation image: `sha256:6f09008b92dcc12bcadbbe87589f4cf4b99a5e23fc03291696c323fb750d48c4`.
- Rolling ASP.NET 10.0 base resolved to `sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee`.
- Soak image: `sha256:372879e59346e958cca05f6c8a00ce95f79af9edbd2976a7b7411ddb2042903c`.
  The final build resolved the same base/Playwright version and passed the full
  builder suite, offline preflight, HTTP/shutdown/restart, and rollback checks.
  Final smoke startup was 2.728 s; shutdown was 0.438 s.
- Trivy 0.76.1 on the final image: **0 critical, 0 high, 30 medium, 16 low**.
  Scanner coverage is not a claim that every native component is vulnerability-free.
- Final network checks used the documented iptables/ip6tables rules, after removing
  the initial nftables experiment and its systemd integration. A Compose firewall
  helper also passed an experiment but is deliberately not shipped.

Raw logs, PDFs, metrics, scan output and provenance are retained in
`artifacts/vm-acceptance` in the working checkout and `/home/auroris/pressroom-evidence`
on the VM. Disposable credentials are excluded from public verification artifacts.

The container's Ubuntu 24.04 userspace was tested on the Ubuntu 26.04 host.
Kubernetes and physical printing were not tested. Docker's tested firewall backend
was iptables, not its alternative native nftables backend. Requalify other hosts
and future dependencies; never bypass a failure by disabling the browser sandbox.
