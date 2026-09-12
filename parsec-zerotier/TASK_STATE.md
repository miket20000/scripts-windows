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
  `ZEROTIER, INC.` organization component. Runtime commands invoke the same
  signed service engine with its official `-q` CLI mode instead of relying on
  fragile nested quoting through the `.bat` wrapper. Authenticode validation
  embeds a safely quoted literal path because Windows PowerShell `-Command`
  does not expose trailing native-process arguments through `$args`.
- Join plus exact `1/0/0/0` flag readback, Node ID enrollment, expected guest
  IP/status, route-to-VM selection, VM ping, public best-route preservation,
  targeted rollback with public-route recovery readback, and next-start cleanup
  are implemented. Once enrollment succeeds, its encrypted state is retained
  after a local failure rollback so Central access can still be reconciled.
- VM route and ping readiness are retried for a bounded 45-second window after
  authorization. An active saved device token now retrieves the exact binding
  from `/guest/zerotier/status` and resumes the same assignment without another
  code or Central authorization. Resume ignores only exact-interface prefixes
  contained in the assigned `/29`; a broader route remains a conflict.
- Successful provisioning or resume leaves the ready message visible and
  disables code entry. Expired/revoked cleanup removes only the saved GP
  Network ID and state, then leaves the controls available for a future lease.
- State and the offline telemetry queue use machine-scope DPAPI and a SYSTEM /
  Administrators-only ACL. Telemetry excludes credentials and broad host/network
  inventory.

## Verification

- `PASS` — cross-platform Core tests: 18/18. They cover exact, covering and
  contained prefix collisions; unrelated/default/down/resume cases; invalid
  prefixes; own `/32` routes during resume; rejection of a broader own-interface
  route; bootstrap/status/telemetry JSON; count and byte queue bounds; quoted
  official publisher subjects; safe PowerShell literal quoting; and split
  CLI/service-engine discovery.
- `PASS` — Release WPF build on Ubuntu with .NET SDK 10 and
  `EnableWindowsTargeting=true`: zero warnings and zero errors.
- `PASS` — final self-contained single-file publish completed after the
  resume/readiness/UI corrections; the candidate consists of
  `GP-ZeroTier-Connect.exe` only, SHA-256
  `4615c9d528d85f588a159f729afdfb5a62405b03d7edd9a4be274543f883c834`.
  Static source scan found no embedded credentials; matches were documentation
  terms only.
- `PASS` — the first full L-WM66 runtime pilot was started manually from
  Explorer Session 1. The operator entered the real single-use code and
  reported `Połączenie przygotowane. Można uruchomić Parsec.` The completed
  admin-only install/join proves elevated execution; the visible UAC prompt
  itself was not separately attested.
  Independent readback confirmed install, join, enrollment, telemetry,
  reachability, and split routing. Parsec was not started by the launcher.
- `PASS` — controlled Windows 11 MT `ZT_NETWORK_CONFLICT` scenario used the
  current `b6d7c6c` framework-dependent candidate and an exact
  `172.30.253.8/29` route on active Wi-Fi. The launcher stopped after bootstrap,
  before install/join, displayed the exact assigned/conflicting prefixes,
  conflict kind and interface, and sent `network_preflight_failed/BLOCKED` with
  `ZT_NETWORK_CONFLICT`. Backend readback found no guest Node ID or enrollment;
  Central remained at the five-VM `5/10` baseline. Bounded cleanup removed the
  one owned route and test process, with no GP membership/state/address and
  unchanged public routing/HTTPS. Evidence is in the devbox provisioning module
  at `evidence/mt-network-conflict-20260912T065023Z.json`.
- `BLOCKED` — dismissal remains intentionally unexecuted, as required by the
  operator. Public-route failure rollback remains unexecuted.
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
  fixes pass the current Core suite and zero-warning WPF build.
- `PASS` by direct Windows readback after the signer fix — engine signature is
  `Valid`, the corrected exact organization regex matches, CLI version is
  `1.16.2`, and legacy network `743993800f834be2` remained `OK`, address
  `172.30.252.2/29`, flags `1/0/0/0` throughout.
- `BLOCKED` — the final rebuilt MT helper, SHA-256
  `7f7aa0df01212ba82a9b1795ded5d600663d39260273c71c31f219060edb4bba`,
  was denied before process start by organizational Device Guard. Code
  Integrity recorded events 3077 and 3033. No policy bypass was attempted;
  staging and isolated runtime state were removed.
- Fail-latched framework-dependent MT retry used the installed, validly signed
  Microsoft `dotnet.exe` and .NET runtime 10.0.10, without changing Device
  Guard. The first DLL run proved that execution was allowed and passed the
  four platform/storage tests, but found that the Authenticode subprocess saw
  a null `$args[0]`; the next candidate passed signature validation but exposed
  fragile `.bat`/`cmd.exe` invocation from `ProcessStartInfo`. Both application
  `FAIL`s remain recorded and produced the minimal fixes described above.
- `PASS` — final framework-dependent MT helper, SHA-256
  `36d91ec0faf93db47d812ea9d6a1152891e96ecacdfb9a6a9a3e6e21366d3744`:
  5/5 for IP Helper snapshot/best-interface readback, real-prefix conflict,
  planned `172.30.253.0/29` no-conflict, machine-scope DPAPI and bounded
  encrypted telemetry queue, plus read-only reuse of the existing signed
  ZeroTier 1.16.2 engine and Node ID. ACL contained only SYSTEM and
  Administrators with non-inherited FullControl. Cleanup removed the isolated
  runtime state and all test staging; legacy network `743993800f834be2`
  remained `OK` at `172.30.252.2/29` with flags `1/0/0/0`.
- `PASS` — the unsigned WPF pilot was rebuilt without warnings and copied to
  `C:\Users\Public\Desktop\GP-ZeroTier-Connect.exe` on L-WM66. Source and
  destination SHA-256 both equal
  `a322e3245f2bde29d05157bfe2963046aab7612aa9bda4cbcb955549d75f1064`;
  destination signature is the expected `NotSigned` test state. L-WM66 has an
  active Explorer Session 1, 137 GB free, administrative SSH access, and no
  pre-existing ZeroTier installation.
- `PASS` — live L-WM66 enrollment to `VM-PC1`: backend state `active`, Central
  contains exactly the VM and guest at `.1`/`.2`, and organization usage is
  `6/10`. Windows readback found signed ZeroTier 1.16.2, `OK`, address
  `172.30.253.2/29`, flags `1/0/0/0`, VM route over the target adapter, public
  route over Wi-Fi, VM ping and HTTPS success. The peer has three direct paths
  and is classified `DIRECT` (reported latency 126 ms).
- `PASS` — production telemetry contains the expected seven successful events:
  bootstrap, both preflights, install, join, enrollment, and connectivity.
  State consists only of encrypted `instance.dat`/`state.dat`; directory ACL is
  protected and contains only SYSTEM and Administrators SIDs.
- `PASS` — clean-install E2E on `MT` for `VM-PC2`: the previous official
  ZeroTier installation, service, ProgramData, residual Program Files tree and
  adapter were removed and independently read back as absent while Wi-Fi and
  HTTPS remained healthy. The launcher installed signed ZeroTier 1.16.2,
  enrolled Node `cb8f448c7f` at `172.30.253.10/29`, selected the target adapter
  for `172.30.253.9`, retained public routes on Wi-Fi and reached the VM 4/4.
  Parsec was not started.
- `FAIL` (tooling, preserved) — after the MSI uninstall, two bounded helper
  attempts could not delete the exact residual Program Files directory because
  its ACL granted FullControl only to SYSTEM. A targeted ownership/ACL repair
  removed that verified residual tree; independent pre-launch readback then
  confirmed complete product/service/data/files/adapter absence.
- `FAIL` (preserved) — the first MT enrollment completed Central authorization
  but a single immediate VM ping failed, producing `ZT_CONNECTIVITY_FAILED` and
  a confirmed local rollback. After the bounded readiness retry and active-token
  resume fix, the same assignment completed with `connectivity_ready=PASS`; no
  second code, enrollment or authorization was created.
- `FAIL` (preserved) — while active resume was still running, the code controls
  remained briefly enabled; a manual submission raced resume and displayed
  `ZT_ACTIVE_LEASE_EXISTS` after connectivity had already succeeded. Startup
  now disables both controls before the first await and re-enables them only
  after confirming there is no active/unverified saved lease or after completed
  expiry cleanup.
- `PASS` — natural expiry ended the MT lease as `expired`; reconcile confirmed
  Central revoke. The next final-launcher start displayed the cleanup message,
  sent `cleanup_completed=PASS`, obtained backend cleanup acknowledgement,
  removed encrypted state, address and all ZeroTier routes, and preserved the
  signed 1.16.2 installation, protected ACL, Wi-Fi public route and HTTPS 200.
- `FAIL` (tooling, preserved) — the final PowerShell evidence helper parsed
  multiline `listnetworks` output as two pipeline objects and reported an
  incorrect `networkCount=2`. Direct application/backend readback is decisive:
  `LeaveAsync` confirmed absence before telemetry, address and routes are absent,
  state is absent, cleanup acknowledgement is true, and Central topology is the
  five-VM baseline.

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

- Controlled live ZeroTier installation, join, authorization and Central
  mutation are approved. Do not touch a target VM during an active student
  lease and preserve the documented preflight/readback/rollback gates.
- Production binary requires organization Authenticode signing.
- Native IP Helper structure layout is confirmed on Windows 10 and 11. Exact
  production join/readback JSON, enrollment-failure rollback, active-token
  resume, natural expiry and next-start cleanup are verified.
- The launcher intentionally has no background service. Central revoke is
  immediate; local `leave` occurs on the next launcher start.
- The guest API is now published under
  `https://dysk.gp.edu.pl/guest/zerotier`; public readback returned `422` for an
  empty bootstrap body and `401` for status without bearer, proving routing and
  backend validation without issuing a code. Operator access without a token
  returned `403`.
- The OpenSSH token is elevated (`S-1-16-12288`), but SSH runs in Session 0
  while Explorer is in Session 1. A WPF/UAC prompt launched through SSH is not
  visible to the logged-in user; interactive UAC must be tested manually from
  Session 1. LocalSystem or a highest-privilege scheduled task is not accepted
  as evidence of the UAC consent path.

## START HERE

1. Both real pilot leases expired naturally and Central returned to the five-VM
   baseline. MT local cleanup is complete. L-WM66 may still retain an
   `ACCESS_DENIED` local membership until its next manual launcher start; do not
   use dismissal to force cleanup.
2. `ZT_NETWORK_CONFLICT` is complete on MT. Verify its VM-PC2 lease reaches
   natural `expired` state after `2026-09-12T08:18:13.755468Z`, without
   dismissal. Public-route rollback remains a separate controlled scenario; do
   not synthesize codes or authorize a Central member by hand.
3. The unsigned self-contained EXE remains blocked by MT Device Guard. Do not
   bypass policy; production distribution requires organization Authenticode.
4. Do not change Central or VM membership without the separately authorized
   rollout gate. The MT clean-install test intentionally removed its former
   legacy local membership; the legacy Central network itself remains retained.
