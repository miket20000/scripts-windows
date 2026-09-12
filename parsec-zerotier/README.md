# GP ZeroTier Connect

Windows 10/11 x64 WPF launcher that prepares a single ZeroTier connection for
the VM lease returned by `vm-manager`, extracts the bundled Parsec Portable
client into an assignment-specific protected directory and starts it after
ZeroTier connectivity is confirmed.

## Safety properties

- Runs `GP-ZeroTier-Connect.exe` as the interactive user with an `asInvoker`
  manifest. If it detects an elevated token, it refuses to continue, ensuring
  that Parsec Portable cannot inherit administrator rights from the launcher.
- Starts the separate `GP-ZeroTier-Connect.Elevated.exe` through UAC only for
  the bounded ZeroTier and state-ACL operations that require administrator
  rights. The helper pins its own MSI URL, hash, version and CLI behavior.
- Uses one random named pipe per launcher process. Its ACL admits only an
  elevated administrator; both peers verify the other process PID. IPC frames
  are length-bounded and accept only a fixed operation/argument allowlist.
  Activation codes, backend tokens, assignment IDs and filesystem paths never
  cross this channel.
- Accepts only a six-digit activation code in `123-456` form.
- Calls `vm-manager`; no ZeroTier Central credential is present in the client.
- Performs two IPv4 collision checks before `join`, using Windows IP Helper API
  routes plus active interface addresses. A collision returns
  `ZT_NETWORK_CONFLICT` without installing ZeroTier or changing membership.
- Installs only ZeroTier One 1.16.2 from the pinned official URL after checking
  SHA-256 and a valid `ZEROTIER, INC.` Authenticode signature. Any other existing
  version is left unchanged and reported as unsupported.
- Joins only the Network ID returned by the authenticated bootstrap, enforces
  `allowManaged=1`, `allowDefault=0`, `allowGlobal=0`, `allowDNS=0`, and reads
  membership back.
- Confirms that the best Windows routes to two public probes did not move to
  ZeroTier and that the VM route selects the enrolled ZeroTier interface. VM
  reachability is retried for a bounded 45-second window after authorization;
  a failure triggers `leave` of only the newly joined network.
- On a later start, an active device token retrieves the exact binding from
  `vm-manager` and safely resumes the same assignment without another code or
  enrollment. Only routes inside the assigned prefix on that exact saved
  ZeroTier interface are excluded from conflict detection; broader routes still
  block. A successful resume disables the code field and connect button.
  Controls are disabled for the entire startup status/resume check, preventing
  a second code submission from racing an active saved lease.
- Stores the lease token and bounded telemetry queue with machine-scope DPAPI
  under `%ProgramData%\GP\ZeroTierConnect`. The helper sets a protected ACL for
  SYSTEM, local Administrators and the verified SID of the launcher process,
  preserving existing encrypted state while exposing it only after UAC-approved
  preparation for that interactive user.
- Telemetry has an explicit, PII-minimized schema. Codes, bearer tokens, MAC,
  SSID, DNS, gateways, usernames, and full route tables are never fields in an
  event.

The API base is pinned to `https://dysk.gp.edu.pl/`; an unprivileged environment
variable cannot redirect activation codes or tokens to another server.

## Parsec Portable

The main single-file launcher embeds `codinggiants-parsec-150-104a.zip` (SHA-256
`ac7483a8a0021c79492671f06966a52ba2527fcc17a2ddff5fcc148d91b07b55`).
Before each launch it validates the pinned PE hashes, the `appdata.json` DLL
binding, the student profile including `app_host=false`, and valid Authenticode
signatures from the exact packaged publishers (`Unity Technologies SF` for the
main runtime/service and `Parsec Cloud, Inc.` for the VUSB helper). Extraction rejects unexpected
files, traversal paths, duplicate entries and oversized archives. Runtime files
are isolated under `%ProgramData%\GP\ZeroTierConnect\ParsecPortable\<assignment_id>`
and are removed, after stopping only the matching executable, during normal
expired/revoked lease cleanup. A Parsec launch failure does not roll back a
successfully verified ZeroTier connection. Login remains interactive; the
launcher does not store or submit Parsec credentials.

## Build and test

Requires .NET SDK 10. On Ubuntu, Windows targeting downloads the WindowsDesktop
reference pack but does not execute the produced binary.

```bash
dotnet run --project tests/Gp.ZeroTier.Connect.Tests/Gp.ZeroTier.Connect.Tests.csproj --configuration Release
dotnet restore src/Gp.ZeroTier.Connect/Gp.ZeroTier.Connect.csproj -p:EnableWindowsTargeting=true
dotnet build src/Gp.ZeroTier.Connect/Gp.ZeroTier.Connect.csproj --configuration Release --no-restore -p:EnableWindowsTargeting=true
dotnet publish src/Gp.ZeroTier.Connect/Gp.ZeroTier.Connect.csproj --configuration Release --no-restore -p:EnableWindowsTargeting=true
```

The publish output is under
`src/Gp.ZeroTier.Connect/bin/Release/net10.0-windows/win-x64/publish/`.
It contains exactly the main launcher and its elevated helper; both files are
required. Production distribution requires both EXEs to be Authenticode-signed
with the same organization certificate. At runtime a signed main executable
rejects an unsigned helper or a helper signed by another certificate; a pair of
unsigned binaries is accepted only to permit isolated development acceptance.

## Backend contract

The client uses:

- `POST /guest/zerotier/bootstrap` with `{ "activation_code": "123-456" }`;
- `POST /guest/zerotier/enroll` with bearer bootstrap token and Node ID;
- `GET /guest/zerotier/status` with bearer device token; an active response
  includes the exact Network ID, assigned prefix and VM/guest addresses needed
  for safe retry of the same assignment;
- `POST /guest/zerotier/cleanup-ack` after confirmed local `leave`;
- `POST /guest/zerotier/telemetry` with the applicable bearer token.

The backend must derive `assignment_id` from the token; the telemetry body does
not contain it. See `Core/Contracts.cs` for the exact JSON names.

## Required Windows acceptance

The Linux build cannot validate DPAPI migration, Windows ACL application,
named-pipe PID/ACL enforcement, standard-user UAC over-the-shoulder behavior,
IP Helper ABI, MSI/UAC, ZeroTier CLI output, routing, rollback, Parsec
Authenticode readback, portable extraction, GUI startup, exact-path process
cleanup, or user-session behavior.
Before distribution, execute those checks on an isolated Windows guest with an
expendable test assignment. Never perform acceptance against an unrelated
existing ZeroTier membership.
