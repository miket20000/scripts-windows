# GP ZeroTier Connect — TASK STATE

## Goal

Deliver a Windows 10/11 x64 launcher that uses a one-time `vm-manager` code to
install or reuse the pinned ZeroTier client and join only the network bound to
the active VM assignment, then validate, extract and automatically start the
bundled Parsec Portable client for that assignment.

## Current implementation

- .NET 10 WPF `win-x64` main launcher is self-contained and uses `asInvoker`.
  It refuses to continue if manually started with an elevated token, so Parsec
  always inherits the ordinary interactive-user token.
- A separate self-contained `GP-ZeroTier-Connect.Elevated.exe` carries the
  `requireAdministrator` manifest. ZeroTier install/version/signature/CLI logic
  and state-directory ACL preparation exist only in that helper project.
- Main/helper IPC uses a random per-process named pipe whose DACL admits only
  Administrators. The launcher verifies the connected helper PID; the helper
  verifies the server PID and reads the launcher's user SID directly from its
  process token. The 16 KiB framed protocol accepts only nine exact operations
  and never carries activation codes, backend tokens, assignment IDs, paths or
  arbitrary process arguments.
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
- State and the offline telemetry queue retain machine-scope DPAPI under
  `%ProgramData%`. The helper replaces the root DACL with exactly SYSTEM,
  Administrators and the verified launcher-user SID, allowing the unelevated UI
  to preserve existing state without granting broader Users access. Telemetry
  excludes credentials and broad host/network inventory.
- Parsec Portable `150-104a` is embedded as a 3,419,147-byte ZIP with SHA-256
  `ac7483a8a0021c79492671f06966a52ba2527fcc17a2ddff5fcc148d91b07b55`.
  Extraction is fail-closed against traversal, duplicates, unexpected entries,
  missing files, more than 20 entries or more than 16 MiB expanded data.
- The runtime is isolated per `asg_<32 lowercase hex>` assignment under the
  protected state tree. Before every start, the launcher validates the four
  pinned PE hashes, the appdata/DLL binding, the managed guest profile including
  `app_host=false`, and each file's exact packaged Authenticode publisher:
  `Unity Technologies SF` for the runtime/service and `Parsec Cloud, Inc.` for
  the VUSB helper.
- Both fresh provisioning and active-token resume start Parsec only after VM
  connectivity and public-route preservation have passed. Parsec launch failure
  is reported separately and does not roll back a verified ZeroTier connection.
  Expired/revoked/invalid cleanup stops only an exact-path `parsecd.exe` and
  removes only that assignment directory before leaving the saved GP network.
- Parsec authentication remains interactive. No Parsec credential is accepted,
  persisted or sent by the launcher.

## Verification

- `PASS` — final Core suite after the privilege split: 29/29. Five IPC cases
  cover the exact request allowlist, argument-smuggling rejection, absence of
  credential/path fields, bounded frame roundtrip and oversized-frame rejection.
- `PASS` — final Release build of Core, elevated helper and WPF launcher on
  Ubuntu with .NET SDK 10: zero warnings and zero errors.
- `PASS` — final publish contains exactly two files: unelevated
  `GP-ZeroTier-Connect.exe`, 143,303,935 bytes, SHA-256
  `994f1ef40afb05a13bdc3bc92b92534e4aaaea5b5889b7732e8b730ef868e8de`,
  and `GP-ZeroTier-Connect.Elevated.exe`, 73,643,254 bytes, SHA-256
  `dd3fa15ba0deccc2d84f0abacd8977d1dbc7782dc609029f477a51dff1045ddc`.
  Static PE resource readback found `asInvoker` only in the main manifest and
  `requireAdministrator` in the helper manifest; the Parsec ZIP resource remains
  embedded in the main EXE.
- `FAIL` (development, preserved) — the first full helper build used source-
  generated `LibraryImport` without enabling unsafe code. It was replaced by
  the smaller classic `DllImport` declarations; the corrected build above is
  the final candidate.
- `FAIL` (tooling, preserved) — sandboxed restore/build attempts ended without
  useful MSBuild diagnostics because of the environment's stream restriction.
  The same isolated-tree commands rerun through the intended host context
  produced the explicit development failure above and then final PASS results.
- `BLOCKED` — Linux cannot execute the Windows named-pipe ACL/PID checks, UAC
  consent or over-the-shoulder credentials, parent-token SID readback, DACL
  replacement, DPAPI access, helper signature matching, ZeroTier operations or
  confirm Parsec's medium-integrity token. No Windows runtime test or deployment
  was authorized or performed in this stage.
- `PASS` — final cross-platform Core suite: 24/24. Six new cases cover safe
  assignment IDs, archive-root normalization, traversal rejection, the hard
  guest profile, appdata/DLL binding and exact Parsec publisher matching.
- `PASS` — final Release WPF build on Ubuntu with .NET SDK 10 and
  `EnableWindowsTargeting=true`: zero warnings and zero errors.
- `PASS` — final self-contained single-file publish produced exactly
  `GP-ZeroTier-Connect.exe`, 143,301,375 bytes, SHA-256
  `8797d826e951fcfbce881c5cf78c380ff2f81e57d07d95f7c03e2b922fe1cf1b`.
  Static readback found the exact embedded resource name. `unzip -t` passed all
  ten archive entries and the repository ZIP hash matches the pinned policy.
- `PASS` — all four packaged PE files have non-empty certificate tables. Static
  PKCS#7 certificate extraction found `Unity Technologies SF` on the runtime,
  DLL and service helper, and `Parsec Cloud, Inc.` on the VUSB helper. Windows
  trust-chain/AuthentiCode status remains part of the runtime acceptance gate.
- `FAIL` (development, preserved) — the first Core compilation used the wrong
  `StartsWith` overload; the first full WPF build then exposed an illegal
  `yield` inside `try/catch`. Both defects were corrected, and the final test,
  build and publish results above are from the corrected candidate.
- `FAIL` (tooling, preserved) — the first packaging command targeted a
  pre-created empty `.zip`, which `zip` rejected as an invalid existing archive.
  Packaging was repeated in a fresh temporary directory and the final archive
  passed hash, entry-list and decompression readback.
- `BLOCKED` (superseded elevated-parent candidate) — Linux could not validate
  Windows Authenticode status, portable extraction/ACL inheritance, GUI
  visibility, exact-path process cleanup or natural expiry cleanup. No Windows
  runtime rollout was performed for that candidate.
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
- `PASS` — controlled MT public-route competition used a valid `VM-PC3`
  enrollment and read back `allowManaged/allowDefault/allowGlobal/allowDNS`
  as `1/0/0/0`. The real ZeroTier adapter exposed a non-winning default route
  with effective metric `10034`, while Wi-Fi remained `45`. A guarded
  `9999 -> 9000` metric perturbation kept all 40 public samples on Wi-Fi,
  preserved HTTPS `200`, and kept only VM `.17` on ZeroTier. Main cleanup and
  an independent restore returned the metric to `9999`; no test route or
  safety route remained. Application telemetry contains seven expected `PASS`
  events and no `public_route_changed`. Evidence is in the devbox module at
  `evidence/mt-public-route-20260912T072733Z.json`.
- `BLOCKED` — dismissal remains intentionally unexecuted, as required by the
  operator. The destructive branch in which ZeroTier actually becomes the
  effective public route remains intentionally unexecuted because this test's
  acceptance contract classifies any such takeover as `FAIL`; the non-winning
  competition and automatic test-route restore are verified.
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
- The earlier architecture document is retained as historical context. Its
  Parsec-out-of-scope decision is superseded by the embedded portable launch
  implementation described above.

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
- The distribution is now an inseparable two-file unit. A signed main EXE
  requires a valid helper signed by the identical certificate thumbprint. An
  unsigned pair is accepted only for isolated development acceptance; production
  signing of both EXEs remains mandatory.
- The helper pipe intentionally grants access to Administrators rather than
  `CurrentUserOnly`, allowing UAC over-the-shoulder elevation under a different
  administrator account. Mutual process-PID verification and the helper's
  launcher-token SID readback retain binding to the initiating UI process.
- Portable login state is assignment-scoped and deleted during normal next-start
  expiry/revocation cleanup. If the launcher is never reopened after expiry,
  local Parsec files remain until the next cleanup run; Central revocation still
  ends VM network access immediately.
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

1. The least-privilege two-EXE candidate is built but not deployed. Before
   distribution, run a separately authorized isolated Windows standard-user
   assignment, including UAC over-the-shoulder credentials, and read back the
   pipe/parent PID binding, exact state DACL, preserved DPAPI state, ZeroTier
   install/join/rollback/cleanup and Parsec's non-elevated token.
2. Exercise active resume, UAC cancellation, missing/replaced/mismatched-signature
   helper, forced helper disconnect, forced Parsec launch failure and natural
   expiry cleanup. Confirm every failure retains the existing rollback and
   telemetry semantics without exposing tokens across IPC.
3. Production distribution requires organization Authenticode signing of both
   EXEs with the same certificate. The unsigned pair remains blocked by MT
   Device Guard; do not bypass that policy.
4. Keep Parsec login interactive unless a separately reviewed supported vendor
   authentication mechanism is selected. Do not inject credentials into CLI
   arguments, config files, telemetry or logs.
5. Do not change Central or VM membership without the separately authorized
   rollout gate. Preserve the five-VM baseline and historical fail-latched
   evidence.
