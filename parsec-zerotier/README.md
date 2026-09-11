# GP ZeroTier Connect

Windows 10/11 x64 WPF launcher that prepares a single ZeroTier connection for
the VM lease returned by `vm-manager`. It deliberately does not install, start,
or configure Parsec.

## Safety properties

- Requests a UAC elevation through `requireAdministrator`.
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
  ZeroTier and that the VM route selects the enrolled ZeroTier interface. A
  failure triggers `leave` of only the newly joined network.
- Stores the lease token and bounded telemetry queue with machine-scope DPAPI
  under `%ProgramData%\GP\ZeroTierConnect`; directory ACL permits only SYSTEM
  and local Administrators.
- Telemetry has an explicit, PII-minimized schema. Codes, bearer tokens, MAC,
  SSID, DNS, gateways, usernames, and full route tables are never fields in an
  event.

The API base is pinned to `https://dysk.gp.edu.pl/`; an unprivileged environment
variable cannot redirect activation codes or tokens to another server.

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
Production distribution additionally requires organization Authenticode signing.

## Backend contract

The client uses:

- `POST /guest/zerotier/bootstrap` with `{ "activation_code": "123-456" }`;
- `POST /guest/zerotier/enroll` with bearer bootstrap token and Node ID;
- `GET /guest/zerotier/status` with bearer device token;
- `POST /guest/zerotier/cleanup-ack` after confirmed local `leave`;
- `POST /guest/zerotier/telemetry` with the applicable bearer token.

The backend must derive `assignment_id` from the token; the telemetry body does
not contain it. See `Core/Contracts.cs` for the exact JSON names.

## Required Windows acceptance

The Linux build cannot validate DPAPI, ACL application, IP Helper ABI, MSI/UAC,
ZeroTier CLI output, routing, or rollback. Before distribution, execute those
checks on an isolated Windows guest with an expendable test assignment. Never
perform acceptance against an unrelated existing ZeroTier membership.
