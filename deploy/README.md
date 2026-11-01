# Deployment examples

This folder ships with each release. `compose.yaml` and `kubernetes.yaml` use a
mounted JSON settings file and one service instance. Replace the version and
create the settings file or Secret before starting. `check.settings.json` is
anonymous and intended only for an offline `--check`; do not deploy it as a
production configuration. `Start-WindowsContainer.ps1` and
`Install-WindowsContainerPolicy.ps1` are the Windows container helpers.

For the recommended internet-facing setup, configure host outbound blocking with
the iptables instructions in [containers](../docs/container.md), then add
`-f compose.host-isolated.yaml`. Only the API port is published. State is temporary;
configuration is a read-only host mount. On Windows, the start helper's
`-BlockOutbound` option explicitly enables administrator-managed HNS rules.
The Kubernetes example uses temporary Pod storage and a default-deny egress
NetworkPolicy, which requires a policy-capable CNI. These are optional deployment
recipes that operators select for their network requirements.

`pressroom-seccomp.json` derives from Docker's default profile at
[moby/profiles 4b8214f](https://github.com/moby/profiles/blob/4b8214fc4971e06172938a1f1554ccff6647ee80/seccomp/default.json)
(Apache-2.0). It allows clone, clone3, unshare and setns for Chromium's user
namespace sandbox, as described by [Playwright](https://playwright.dev/docs/docker),
plus chroot inside that namespace and pidfd_open and pidfd_send_signal for
identity-safe process cleanup. Docker's capability-conditional chroot rule does
not cover `cap_drop: [ALL]`, even though Chromium gains the needed capability
inside its own user namespace. Host capabilities remain dropped, and the default
deny policy and other syscall restrictions stay in place. Use this profile for
the non-root service, with the firewall recipe above for network isolation.

See [Linux](../docs/linux.md) and [containers](../docs/container.md).
