# GP ZeroTier Connect — TASK STATE

## Goal

Deliver a Windows 10/11 x64 launcher that uses a one-time `vm-manager` code to
install or reuse the pinned ZeroTier client and join only the network bound to
the active VM assignment. Parsec is outside scope.

## Current implementation

- .NET 10 WPF `win-x64`, self-contained single-file publish configuration and
  `requireAdministrator` manifest are under `src/Gp.ZeroTier.Connect`.
- The cross-platform core contains backend JSON contracts, IPv4 prefix parsing,
  overlap detection, and bounded telemetry queue logic.
- Bootstrap uses `123-456`; Central credentials never enter the launcher.
- Preflight runs after bootstrap and again immediately before `join`. Active
  IPv4 interface addresses and IP Helper API routes are checked; default routes
  and down interfaces do not create false conflicts.
- ZeroTier One is pinned to 1.16.2, SHA-256
  `42514072B0FE44B8F66E0395BCD23A0B1D1642C28ED00831F1527B2F41B14670`,
  and signer `ZEROTIER, INC.`. Existing unsupported versions are not changed.
- Join plus exact `1/0/0/0` flag readback, Node ID enrollment, expected guest
  IP/status, route-to-VM selection, VM ping, public best-route preservation,
  targeted rollback with public-route recovery readback, and next-start cleanup
  are implemented. Once enrollment succeeds, its encrypted state is retained
  after a local failure rollback so Central access can still be reconciled.
- State and the offline telemetry queue use machine-scope DPAPI and a SYSTEM /
  Administrators-only ACL. Telemetry excludes credentials and broad host/network
  inventory.

## Verification

- `PASS` — cross-platform Core tests: 12/12. They cover exact, covering and
  contained prefix collisions; unrelated/default/down/resume cases; invalid
  prefixes; bootstrap and telemetry JSON; count and byte queue bounds.
- `PASS` — Release WPF build on Ubuntu with .NET SDK 10 and
  `EnableWindowsTargeting=true`: zero warnings and zero errors.
- `PASS` — self-contained single-file publish completed; the candidate consists
  of `GP-ZeroTier-Connect.exe` only, SHA-256
  `8b2c7daf01fe84c2744171f4c6be29d85935feef3766b24435661e12e1f7b779`.
  Static source scan found no embedded credentials; matches were documentation
  terms only.
- `BLOCKED` — Windows runtime acceptance is intentionally not executed on
  Ubuntu. DPAPI, ACLs, IP Helper ABI, Authenticode/MSI/UAC, real CLI JSON,
  join/leave, reachability, routing, and rollback require an isolated Windows
  test device and backend assignment.

## Preserved runtime context

- Earlier manual tests proved ZeroTier One 1.16.2 connectivity and split-tunnel
  flags with VM-PC25 and Windows guests. A high-metric `25.255.255.254` default
  route existed, but tested public flows retained their physical interface.
- iPhone hotspot tests could fall back to RELAY with materially increased
  latency. Connectivity success does not establish acceptable Parsec quality.
- The earlier architecture document is retained as historical context; its
  Parsec-launching scope is superseded by the current requirement that Parsec
  remain outside this application.

## Constraints and risks

- Do not perform live ZeroTier installation, join, authorization, or Central
  mutation without separate rollout approval.
- Production binary requires organization Authenticode signing.
- The native IP Helper structure layout and exact ZeroTier 1.16.2 JSON variants
  must be confirmed on Windows before distribution.
- The launcher intentionally has no background service. Central revoke is
  immediate; local `leave` occurs on the next launcher start.

## START HERE

1. On an isolated Windows guest, validate preflight conflicts before any join,
   then DPAPI/ACL, signed MSI install, CLI JSON variants, flags, routing,
   connectivity, failure rollback, dismissal/expiry cleanup, and telemetry.
2. Do not use production Central networks or change existing memberships until
   the Windows candidate passes and rollout is separately authorized.
