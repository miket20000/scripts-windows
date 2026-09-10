# ZeroTier + Internet test — TASK STATE

## Goal

Connect the Windows guests, including `student-ve-09`, to `VM-PC25` through
private ZeroTier network `743993800f834be2` while preserving Internet, DNS,
VPN-CG, and the reverse-SSH management path through `gp`. The active
acceptance criterion is functional:
the extra high-metric route `0.0.0.0/0 -> 25.255.255.254` is allowed to exist
only if measurements show that Windows does not use it for normal Internet.

## Scope and constraints

- This stage covers ZeroTier connectivity and Internet preservation only.
- Do not start or reconfigure Parsec and do not implement the launcher.
- Use split tunneling: `allowManaged=1`, `allowDefault=0`, `allowGlobal=0`,
  `allowDNS=0`; no bridge.
- Reach VM-PC25 through `gp` and the reverse listener `127.0.0.1:32605`.
- Apply changes one endpoint at a time. On measured Internet/DNS/VPN/SSH
  impact, leave the affected node and read back the recovered state.
- Preserve the earlier default-route FAIL as historical evidence. Do not edit
  `launcher-zerotier-parsec-architecture.md`, commit, or push.

## Current status

- Central network `743993800f834be2` is private, uses `172.30.252.0/29`, has
  IPv4 auto-assignment off, and has no IPv6 auto-assignment, managed default
  route, or ZeroTier DNS servers.
- VM-PC25 runs the validated official ZeroTier One 1.16.2 client. Node
  `1c9f72075e` is authorized, status `OK`, and assigned `172.30.252.1/29`.
- VM-PC25 retains the extra route through `25.255.255.254`, but all measured
  public destinations use physical `Ethernet 3`, source `192.168.1.31`, and
  gateway `192.168.1.1`, including after service and host restarts.
- Guest runs ZeroTier One 1.16.2. Node `21fb06dd30` is authorized and assigned
  `172.30.252.2/29`. Windows installed the managed `172.30.252.0/29` route;
  bidirectional overlay, TCP/22, DIRECT peer path, and VPN-CG coexistence pass.
- Additional guest `student-ve-09` / `VE-09` now runs the validated official
  ZeroTier One 1.16.2 client. Node `e6caeda303` is authorized, status `OK`,
  assigned `172.30.252.3/29`, and retains split-tunnel flags `1/0/0/0`.
  Bidirectional overlay, TCP/22, DIRECT peer path, Internet and DNS pass.
- After VE-09 switched to an iPhone hotspot, its ZeroTier overlay reconverged
  through `RELAY`. VM-PC25 still reaches `172.30.252.3`, but average RTT
  increased to 273 ms and VE-09 reverse SSH through `gp:32569` stopped
  completing the SSH handshake.
- On 2026-09-10 the guest was moved to an iPhone hotspot (`172.20.10.2`,
  gateway `172.20.10.1`). The existing ZeroTier membership stayed active and
  reached VM-PC25, but the peer path changed from `DIRECT` to `RELAY`.
- The architecture document was not modified. No commit or push was made.

## Preserved historical result

- `FAIL` under the earlier, stricter criterion **"no additional default
  route"**: the first VM-PC25 join created `0.0.0.0/0` through
  `25.255.255.254`, metric 10034, despite `allowDefault=false`. That candidate
  was immediately left and the route disappeared. This result is retained but
  is not an automatic FAIL under the current functional criterion.

## Functional verification

### VM-PC25 before rejoin

- `PASS` — not a member (`listnetworks=[]`), physical IP `192.168.1.31`, and
  only default route `0.0.0.0/0 -> 192.168.1.1`, interface 17, metric 25.
- `PASS` — `Find-NetRoute` selected interface 17 / `Ethernet 3`, source
  `192.168.1.31`, next hop `192.168.1.1` for `1.1.1.1`, `8.8.8.8`, and the
  resolved HTTPS endpoint `172.66.167.152`.
- `PASS` — DNS resolved `example.com`; three HTTPS requests returned 200; the
  `gp:32605` listener and `guest -> gp -> VM-PC25` round-trip worked.

### VM-PC25 after rejoin, before authorization

- `PASS` — flags read back as `allowManaged=true`, `allowDefault=false`,
  `allowGlobal=false`, `allowDNS=false`; initial private-network state was
  `ACCESS_DENIED`/`REQUESTING_CONFIGURATION`, as expected before authorization.
- Route table contained both defaults:

  | Destination | Next hop | Interface/source | Effective metric |
  | --- | --- | --- | ---: |
  | `0.0.0.0/0` | `192.168.1.1` | `Ethernet 3` / `192.168.1.31` | 25 |
  | `0.0.0.0/0` | `25.255.255.254` | ZeroTier / `169.254.255.15` | 10034 |

- `PASS` — `Find-NetRoute` selected the physical route for `1.1.1.1`,
  `8.8.8.8`, and `172.66.167.152`. Five consecutive HTTPS requests returned
  200 with local address `192.168.1.31` (0.067-0.149 s).
- `PASS` — DNS remained on pre-existing servers `145.239.83.162` and
  `51.75.74.125`; reverse-SSH process, listener, and round-trip remained active.

### ZeroTier service restart

- `PASS` — service returned `Running/Automatic`; both default routes returned
  with metrics 25 and 10034.
- `PASS` — all three public destinations again selected `Ethernet 3`, source
  `192.168.1.31`, next hop `192.168.1.1`; three HTTPS requests returned 200.
- `PASS` — DNS and reverse SSH were unchanged.

### VM-PC25 restart

- `PASS` — scheduled `startupScript` was verified to launch
  `startReverseSsh.ps1`; VM-PC25 rebooted at `2026-09-09 22:23:37 +02:00` and
  the reverse listener returned after about 70 seconds.
- `PASS` — after boot, both default routes existed (25 vs 10034), but
  `Find-NetRoute` selected the physical route for `1.1.1.1`, `8.8.8.8`, and
  HTTPS endpoint `104.20.42.91`.
- `PASS` — five HTTPS requests returned 200 from `192.168.1.31`; DNS and the
  recreated reverse-SSH process passed.

### VM-PC25 after Central authorization

- `PASS` — Central readback: authorized and manual IPv4 `172.30.252.1`.
- `PASS` — client readback: `OK`, `172.30.252.1/29`, managed route
  `172.30.252.0/29`, and all four split-tunnel flags retained.
- `PASS` — public-route selection remained physical for all three targets;
  three HTTPS requests returned 200 from `192.168.1.31`; DNS and reverse SSH
  remained correct.

### Guest, overlay, and VPN-CG

- `PASS` — fresh pre-join baseline on 2026-09-10: guest had no ZeroTier address,
  ZeroTier service was running, and its only default route was Wi-Fi through
  `192.168.50.1` (effective metric 50).
- `PASS` — after user-approved UAC, guest node `21fb06dd30` joined and flags
  read back as `allowManaged=true`, `allowDefault=false`, `allowGlobal=false`,
  `allowDNS=false`; state is `ACCESS_DENIED` until Central authorization.
- `PASS` — guest route table after join contains physical default
  `192.168.50.1` at metric 50 and ZeroTier default `25.255.255.254` at metric
  10034. `Find-NetRoute` selected Wi-Fi, source `192.168.50.183`, and gateway
  `192.168.50.1` for `1.1.1.1`, `8.8.8.8`, and HTTPS endpoint `104.20.42.91`.
- `PASS` — guest DNS stayed on `192.168.50.1`; five consecutive HTTPS requests
  returned 200 from source `192.168.50.183` (0.150-0.178 s).
- `PASS` — after guest join, `gp:32605` remained listening and the
  `guest -> gp -> VM-PC25` round-trip selected host `Ethernet 3` via
  `192.168.1.1`. A supplemental host `curl` command was invalid because
  PowerShell resolved it as an alias; this helper failure is not a network FAIL.
- `PASS` — Central authorized exact guest node `21fb06dd30` and read back manual
  IPv4 `172.30.252.2`. Windows read back that address with prefix `/29` and the
  managed on-link route `172.30.252.0/29` on the ZeroTier adapter.
- `PASS` — guest `Find-NetRoute 172.30.252.1` selected the ZeroTier adapter,
  source `172.30.252.2`, and `/29` route. Guest -> host ping was 10/10
  (49-109 ms, average 60 ms); TCP/22 succeeded over the ZeroTier interface.
- `PASS` — host `Find-NetRoute 172.30.252.2` selected ZeroTier, source
  `172.30.252.1`. Host -> guest ping was 10/10 (48-64 ms, average 54.7 ms).
- `PASS` — host peer readback for `21fb06dd30` showed active direct paths to
  the guest public endpoint, `tunneled=false`; textual CLI later reported
  `DIRECT` explicitly. This is not a RELAY path.
- `PASS` — overlay stability after VPN cleanup: guest -> host ping 20/20
  (50-63 ms, average 55.15 ms), five consecutive TCP/22 attempts succeeded,
  and three HTTPS attempts returned 200 through physical Wi-Fi.
- `PASS` — VPN-CG coexistence. Existing L2TP profile was initially disconnected,
  had `SplitTunneling=true`, and was connected without configuration changes.
  Route `10.0.0.0/8` was selected through `VPN-CG`, source `10.10.200.213`,
  next hop `10.10.200.200`; `172.30.252.0/29` remained on ZeroTier; public
  `1.1.1.1` and `8.8.8.8` remained on Wi-Fi through `192.168.50.1`.
- `PASS` — with VPN-CG active: DNS resolved, five HTTPS requests returned 200
  from physical source `192.168.50.183`, guest -> host ping was 10/10 and
  TCP/22 succeeded, host -> guest ping was 10/10, peer remained `DIRECT`, and
  reverse SSH remained listening. VPN-CG was then restored to its initial
  `Disconnected` state; route `10.0.0.0/8` disappeared and overlay remained.
- `BLOCKED` — an optional final elevated guest CLI readback of `info`, flags,
  and peers timed out waiting for a second UAC approval. It was read-only and
  made no change. This does not invalidate the earlier flag readback or the
  successful Windows route/address/traffic measurements after authorization.

### iPhone hotspot runtime check — 2026-09-10

- `PASS` — guest ZeroTier service remained running with `172.30.252.2/29`; no
  join, service restart, Central change, or other mutation was needed.
- `PASS` — `Find-NetRoute 172.30.252.1` selected ZeroTier with source
  `172.30.252.2`. Guest -> host ping was 5/5 (329-372 ms, average 345 ms) and
  TCP/22 succeeded through the ZeroTier adapter.
- `PASS` — host -> guest ping was 5/5 (333-343 ms, average 340.6 ms). The host
  CLI reported `21fb06dd30 ... RELAY`; this is functional but materially worse
  than the earlier DIRECT path and may be unsuitable for interactive Parsec.
- `PASS` — public `1.1.1.1` and `8.8.8.8` used guest Wi-Fi source
  `172.20.10.2`, gateway `172.20.10.1`, effective metric 30. DNS resolved and
  two HTTPS requests returned 200 from `172.20.10.2`.
- `PASS` — VM-PC25 retained physical Internet through `Ethernet 3`, source
  `192.168.1.31`, gateway `192.168.1.1`; reverse listener `gp:32605` remained
  active.

### Additional guest student-ve-09 — 2026-09-10

- `PASS` — the SSH path `guest -> gp -> student-ve-09` identifies Windows 10
  Pro host `VE-09`; account `ve-09\giganci` is elevated.
- `PASS` — pre-install baseline had only physical default route
  `0.0.0.0/0 -> 196.168.100.1` through Wi-Fi, effective metric 50 and source
  `196.168.100.67`. DNS used `145.239.83.162` and `51.75.74.125`; five
  HTTPS requests returned 200 in 38-58 ms. ZeroTier service and package were
  absent.
- `PASS` — official ZeroTier MSI installed without restart: size 12,003,840
  bytes, SHA-256
  `42514072B0FE44B8F66E0395BCD23A0B1D1642C28ED00831F1527B2F41B14670`,
  valid Authenticode signer `ZEROTIER, INC.`, installer exit 0. ZeroTier One
  1.16.2 service is running with automatic start.
- `PASS` — node `e6caeda303` joined `743993800f834be2`; readback is
  `allowManaged=1`, `allowDefault=0`, `allowGlobal=0`, `allowDNS=0`.
  Before Central authorization the network state is `ACCESS_DENIED` with no
  assigned address.
- `PASS` — after join, Windows added
  `0.0.0.0/0 -> 25.255.255.254` through the ZeroTier adapter at effective
  metric 10034. `Find-NetRoute` continued to select physical Wi-Fi source
  `196.168.100.67` for `1.1.1.1`, `8.8.8.8`, and `104.20.23.154`.
  DNS resolved and five HTTPS requests returned 200 in 37-60 ms.
- `PASS` — after the operator signed in and confirmed the access change,
  Central showed node `e6caeda303` as the sole pending device and
  `172.30.252.3` as unused. The exact node was authorized and assigned
  `172.30.252.3`; Central reported `Changes Saved`, and Windows read back
  `OK`, `172.30.252.3/29`, and flags `1/0/0/0`.
- `PASS` — VE-09 selected the ZeroTier adapter and source
  `172.30.252.3` for `172.30.252.1`. VE-09 -> VM-PC25 ping was 10/10
  (10-44 ms, average 19 ms), and TCP/22 was 5/5. VM-PC25 selected its
  ZeroTier adapter and source `172.30.252.1`; host -> VE-09 ping was 10/10
  (11-25 ms, average 17 ms), and TCP/22 succeeded.
- `PASS` — both endpoints reported the peer path as `DIRECT`; measured CLI
  latency was 18-39 ms before the service restart. Public routes remained on
  VE-09 Wi-Fi and VM-PC25 Ethernet, DNS resolved, all measured HTTPS requests
  returned 200, and reverse listener `gp:32605` remained active.
- `FAIL` with recovery — the first `Restart-Service ZeroTierOneService
  -Force` attempt stopped the VE-09 service but raised an error before starting
  it again; service readback was `Stopped`, exit 1067. Internet and the
  independent SSH management path remained available.
- Intervention and subsequent `PASS` — explicit `Start-Service` restored
  `Running/Automatic`, node `ONLINE`, network `OK`, address
  `172.30.252.3/29`, and flags `1/0/0/0`. After recovery, VE-09 -> host
  ping was 10/10 (11-20 ms, average 14 ms), TCP/22 was 3/3, host -> VE-09 ping
  was 10/10 (12-25 ms, average 16 ms), and the peer remained `DIRECT`.
  Public Internet still selected Wi-Fi; DNS and three HTTPS requests passed.
- `BLOCKED` — VPN-CG coexistence on VE-09 cannot be tested because that
  machine has no VPN profiles and no `10.0.0.0/8` route. No VPN configuration
  was added or changed.

### VE-09 iPhone hotspot runtime check — 2026-09-10

- `PASS` — after a short reconvergence period, VM-PC25 selected the ZeroTier
  adapter and source `172.30.252.1` for `172.30.252.3`. Host -> VE-09 ping
  was 10/10 (249-303 ms, average 273 ms), and TCP/22 was 3/3.
- `PASS` with degraded path — VM-PC25 reported node `e6caeda303` as
  `RELAY` with CLI latency `-1`, replacing the earlier `DIRECT` path.
  The overlay is functional, but latency is materially worse and may be
  unsuitable for interactive Parsec.
- `FAIL` — VE-09 reverse SSH through `gp:32569` did not recover after the
  network switch. Three connections reached the local forwarded TCP port but
  timed out during SSH banner exchange. The independent VM-PC25 listener
  `gp:32605` remained present.
- `BLOCKED` — because the VE-09 management path was unavailable, direct
  post-switch readback of its service, physical default route, DNS, and Internet
  could not be performed. Successful bidirectional overlay responses and
  TCP/22 prove that the ZeroTier membership itself remained active.

## Risk assessment

- On all tested Windows endpoints, `25.255.255.254` exists at effective metric 10034
  but was never selected for measured public traffic. Host Internet used
  `Ethernet 3 -> 192.168.1.1` (metric 25); the original guest used
  `Wi-Fi -> 192.168.50.1` (metric 50), including with VPN-CG active; VE-09
  used `Wi-Fi -> 196.168.100.1` (metric 50).
- DNS was not taken over by ZeroTier. HTTPS remained stable, VPN-CG retained
  its `10.0.0.0/8` route, and reverse SSH remained stable. Host behavior also
  survived a ZeroTier service restart and a full VM-PC25 restart.
- Residual risk: route preference could change if physical interface metrics or
  connectivity change in the future. The current evidence supports the tested
  configuration; operational monitoring remains advisable during Parsec tests.
  iPhone hotspot use can force VE-09 to `RELAY`, sharply increase latency, and
  disrupt its independent reverse-SSH management path.
- **Recommendation A: ZeroTier is suitable for further testing with Parsec.**

## Important files

- `launcher-zerotier-parsec-architecture.md` — architecture reference only;
  do not modify.
- `TASK_STATE.md` — verified execution snapshot for this test.

## START HERE

1. Preserve all three memberships and split-tunnel flags. Before future Parsec
   work, recheck effective public routes, DNS, reverse SSH, overlay reachability,
   and peer `DIRECT`/`RELAY` state for the selected guest.
2. For VE-09, first restore or diagnose reverse SSH through `gp:32569`; until
   then, use VM-PC25-side overlay tests as evidence and do not claim direct
   readback of VE-09 Internet or DNS after the hotspot switch.
3. On VE-09, use an explicit stop/start procedure with immediate status
   readback instead of assuming `Restart-Service` will complete both phases.
   The failed restart attempt and successful recovery must remain in evidence.
4. Treat Parsec, launcher work, VE-09 reboot testing, and adding a VPN profile
   as separate authorized stages. If public traffic selects `25.255.255.254`
   or DNS/Internet/SSH is damaged, leave the affected member and verify recovery.
