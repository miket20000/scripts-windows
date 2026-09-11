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
- Existing-installation discovery handles the proven Windows layout where the
  CLI is under `Program Files (x86)` but the signed service engine is under
  `%ProgramData%\ZeroTier\One`. Publisher matching accepts the quoted X.500
  organization emitted by Authenticode while still requiring an exact
  `ZEROTIER, INC.` organization component.
- Join plus exact `1/0/0/0` flag readback, Node ID enrollment, expected guest
  IP/status, route-to-VM selection, VM ping, public best-route preservation,
  targeted rollback with public-route recovery readback, and next-start cleanup
  are implemented. Once enrollment succeeds, its encrypted state is retained
  after a local failure rollback so Central access can still be reconciled.
- State and the offline telemetry queue use machine-scope DPAPI and a SYSTEM /
  Administrators-only ACL. Telemetry excludes credentials and broad host/network
  inventory.

## Verification

- `PASS` — cross-platform Core tests: 14/14. They cover exact, covering and
  contained prefix collisions; unrelated/default/down/resume cases; invalid
  prefixes; bootstrap and telemetry JSON; count and byte queue bounds; quoted
  official publisher subjects; and split CLI/service-engine discovery.
- `PASS` — Release WPF build on Ubuntu with .NET SDK 10 and
  `EnableWindowsTargeting=true`: zero warnings and zero errors.
- `PASS` — self-contained single-file publish completed; the candidate consists
  of `GP-ZeroTier-Connect.exe` only, SHA-256
  `7bf1630b49a92cc40adcc57f737e19b024f7dddce981555e429bdb472fae8e7f`.
  Static source scan found no embedded credentials; matches were documentation
  terms only.
- `BLOCKED` — Windows runtime acceptance is intentionally not executed on
  Ubuntu. Join/leave, installation, enrollment, telemetry delivery,
  reachability, routing rollback, expiry cleanup, and interactive WPF/UAC still
  require the deployed backend, an active assignment/code, and the controlled
  Windows pilot.
- `PASS` — Windows 10 Pro `L-WM66` runtime helper, SHA-256
  `48287e7fe86573d1d9cc7fbf948b7994b87f1e00f1ea60efab43c4d5bb3ad4ef`:
  native IP Helper snapshot and best-interface readback, real-prefix conflict,
  no conflict for planned `172.30.253.0/29`, machine-scope DPAPI roundtrip with
  no plaintext token, and the bounded 100-event encrypted queue. ACL readback
  contained only SYSTEM and Administrators with non-inherited FullControl.
  ZeroTier remained absent before and after; test state and staging were removed.
- Fail-latched MT reuse sequence: the first helper run found that service-engine
  discovery incorrectly assumed the engine was beside the CLI; after that fix,
  the second run found that the valid signer subject quotes `O="ZEROTIER, INC."`.
  Both are application `FAIL`s and remain recorded. The corresponding minimal
  fixes pass the 14 Core tests and zero-warning WPF build.
- `PASS` by direct Windows readback after the signer fix — engine signature is
  `Valid`, the corrected exact organization regex matches, CLI version is
  `1.16.2`, and legacy network `743993800f834be2` remained `OK`, address
  `172.30.252.2/29`, flags `1/0/0/0` throughout.
- `BLOCKED` — the final rebuilt MT helper, SHA-256
  `7f7aa0df01212ba82a9b1795ded5d600663d39260273c71c31f219060edb4bba`,
  was denied before process start by organizational Device Guard. Code
  Integrity recorded events 3077 and 3033. No policy bypass was attempted;
  staging and isolated runtime state were removed.

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
- Native IP Helper structure layout is confirmed on Windows 10 and 11. Exact
  production join/readback JSON and rollback behavior remain unverified because
  no enrollment was authorized.
- The launcher intentionally has no background service. Central revoke is
  immediate; local `leave` occurs on the next launcher start.
- Public `https://dysk.gp.edu.pl/health` and `/openapi.json` returned HTTP 404 at
  the runtime checkpoint, so the guest API was not yet available for the pilot.
- The OpenSSH token is elevated (`S-1-16-12288`), but SSH runs in Session 0
  while Explorer is in Session 1. A WPF/UAC prompt launched through SSH is not
  visible to the logged-in user; interactive UAC must be tested manually from
  Session 1. LocalSystem or a highest-privilege scheduled task is not accepted
  as evidence of the UAC consent path.

## START HERE

1. Deploy/read back the guest API and obtain an active L-WM66 assignment plus
   one-time code; do not synthesize a code or authorize a Central member by hand.
2. Publish the updated launcher and start it manually from L-WM66 Explorer
   Session 1 to verify the unsigned pilot UAC prompt and visible WPF flow.
3. Validate no-conflict preflight, pinned MSI hash/signature/install, join,
   exact flags/address, routing and telemetry, then dismissal/expiry revoke,
   next-start local leave and failure rollback with before/after membership
   readback.
4. Preserve MT legacy network `743993800f834be2`; do not use it for the pilot or
   bypass Device Guard. Do not change Central or VM membership without the
   separately authorized rollout gate.
