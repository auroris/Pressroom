# Deployment examples

`compose.yaml` and `kubernetes.yaml` use a mounted JSON settings file and one
service instance. Replace the version and create the settings file/Secret before
starting. `check.settings.json` is anonymous and intended only for an offline
`--check`; do not deploy it as a production configuration.

`pressroom-seccomp.json` derives from Docker's default profile at
[moby/profiles 4b8214f](https://github.com/moby/profiles/blob/4b8214fc4971e06172938a1f1554ccff6647ee80/seccomp/default.json)
(Apache-2.0). It allows clone, clone3, unshare and setns for Chromium's user
namespace sandbox, as described by [Playwright](https://playwright.dev/docs/docker),
plus pidfd_open and pidfd_send_signal for identity-safe process cleanup. The
default deny policy and other syscall restrictions remain in place. This profile
is for a non-root service; it does not grant host capabilities or network isolation.

See [Linux](../docs/linux.md) and [containers](../docs/container.md).
