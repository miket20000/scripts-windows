using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Gp.ZeroTier.Connect.Core;

namespace Gp.ZeroTier.Connect;

public sealed class ProvisioningService(
    BackendClient backend,
    TelemetryService telemetry,
    SecureStorage storage,
    WindowsNetworkInspector networkInspector,
    IZeroTierController zeroTier,
    ParsecPortableManager parsec)
{
    public async Task<ProvisioningResult?> CleanupExpiredStateAsync(CancellationToken cancellationToken)
    {
        await telemetry.FlushAsync(cancellationToken);
        var state = storage.LoadState();
        if (state is null) return null;

        LeaseStatusResponse status;
        try { status = await backend.StatusAsync(state.DeviceToken, cancellationToken); }
        catch (BackendException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            status = new("invalid", state.AssignmentId);
        }

        var normalizedStatus = status.Status.ToLowerInvariant();
        if (normalizedStatus == "active" && state.LeaseExpiresAt > DateTimeOffset.UtcNow)
            return await ResumeActiveStateAsync(state, status, cancellationToken);
        if (normalizedStatus is not ("revoked" or "expired" or "invalid")) return null;

        try
        {
            await parsec.CleanupAsync(state.AssignmentId, cancellationToken);
            await telemetry.TrySendAsync(state.DeviceToken, telemetry.Create("parsec_cleanup_completed", "PASS"), cancellationToken);
        }
        catch (LauncherException ex)
        {
            await telemetry.TrySendAsync(state.DeviceToken, telemetry.Create("parsec_cleanup_failed", "FAIL", ex.Code), cancellationToken);
            throw;
        }
        if (await zeroTier.IsInstalledAsync(cancellationToken))
        {
            await zeroTier.EnsureInstalledAsync(cancellationToken);
            await zeroTier.LeaveAsync(state.NetworkId, cancellationToken);
        }
        await telemetry.TrySendAsync(state.DeviceToken, telemetry.Create("cleanup_completed", "PASS"), cancellationToken);
        try { await backend.CleanupAckAsync(state.DeviceToken, state.NetworkId, cancellationToken); }
        catch when (!cancellationToken.IsCancellationRequested) { }
        storage.DeleteState();
        return new("Usunięto dane Parsec i lokalne członkostwo zakończonego przydziału.", false);
    }

    public async Task<ProvisioningResult> ProvisionAsync(string activationCode, CancellationToken cancellationToken)
    {
        await telemetry.FlushAsync(cancellationToken);
        if (storage.LoadState() is not null)
            throw new LauncherException("ZT_ACTIVE_LEASE_EXISTS", "To urządzenie ma zapisany aktywny lub niezweryfikowany przydział. Uruchom aplikację ponownie po zakończeniu lease.");
        var bootstrap = await backend.BootstrapAsync(activationCode, cancellationToken);
        ValidateBootstrap(bootstrap);
        var token = bootstrap.BootstrapToken;
        await telemetry.TrySendAsync(token, telemetry.Create("bootstrap_succeeded", "PASS"), cancellationToken);

        var joined = false;
        ClientState? state = null;
        NetworkSnapshot? beforeJoin = null;
        try
        {
            var assigned = Ipv4Prefix.Parse(bootstrap.AssignedPrefix);
            await CaptureAndPreflightAsync(assigned, token, cancellationToken);

            var installWatch = Stopwatch.StartNew();
            await zeroTier.EnsureInstalledAsync(cancellationToken);
            var version = await zeroTier.GetVersionAsync(cancellationToken);
            await telemetry.TrySendAsync(token, telemetry.Create("install_completed", "PASS", zeroTierVersion: version, durationMs: installWatch.ElapsedMilliseconds), cancellationToken);

            try { beforeJoin = networkInspector.Capture(); }
            catch (LauncherException ex) when (ex.Code == "ZT_NETWORK_PREFLIGHT_UNAVAILABLE")
            {
                await telemetry.TrySendAsync(token, telemetry.Create("network_preflight_failed", "BLOCKED", ex.Code), cancellationToken);
                throw;
            }
            await RunPreflightAsync(assigned, token, beforeJoin, cancellationToken);

            await zeroTier.JoinAsync(bootstrap.NetworkId, cancellationToken);
            joined = true;
            await telemetry.TrySendAsync(token, telemetry.Create("join_completed", "PASS", zeroTierVersion: version), cancellationToken);

            var nodeId = await zeroTier.GetNodeIdAsync(cancellationToken);
            var enrollment = await backend.EnrollAsync(token, nodeId, cancellationToken);
            if (!string.Equals(enrollment.AssignmentId, bootstrap.AssignmentId, StringComparison.Ordinal))
                throw new LauncherException("ZT_ASSIGNMENT_MISMATCH", "Serwer zwrócił enrollment dla innego przydziału.");
            state = new(
                enrollment.AssignmentId,
                bootstrap.NetworkId,
                enrollment.DeviceToken,
                enrollment.LeaseExpiresAt,
                bootstrap.AssignedPrefix,
                bootstrap.VmIp,
                enrollment.GuestIp);
            storage.SaveState(state);
            token = enrollment.DeviceToken;
            await telemetry.TrySendAsync(token, telemetry.Create("enrollment_completed", "PASS", zeroTierVersion: version), cancellationToken);

            var ready = await zeroTier.WaitUntilReadyAsync(bootstrap.NetworkId, enrollment.GuestIp, cancellationToken);
            if (ready is null || ready.InterfaceIndex == 0 ||
                !await WaitForConnectivityAsync(bootstrap.VmIp, ready.InterfaceIndex, cancellationToken))
                throw new LauncherException("ZT_CONNECTIVITY_FAILED", "Sieć została zestawiona, ale przypisana maszyna nie odpowiada.");

            var afterJoin = networkInspector.Capture();
            if (!WindowsNetworkInspector.PublicRouteUnchanged(beforeJoin, afterJoin, ready.InterfaceIndex))
                throw new LauncherException("ZT_PUBLIC_ROUTE_CHANGED", "ZeroTier zmienił trasę używaną do ruchu publicznego.");

            await telemetry.TrySendAsync(
                token,
                telemetry.Create("connectivity_ready", "PASS", zeroTierVersion: version, networkStatus: ready.Status, pathType: "UNKNOWN"),
                cancellationToken);
            return await CompleteWithParsecAsync(enrollment.AssignmentId, token, cancellationToken);
        }
        catch (Exception failure)
        {
            var leaveSucceeded = !joined;
            if (joined)
            {
                try
                {
                    await zeroTier.LeaveAsync(bootstrap.NetworkId, cancellationToken);
                    leaveSucceeded = true;
                    if (failure is LauncherException { Code: "ZT_PUBLIC_ROUTE_CHANGED" } && beforeJoin is not null)
                    {
                        var afterRollback = networkInspector.Capture();
                        leaveSucceeded = beforeJoin.BestInterfaces.All(pair =>
                            afterRollback.BestInterfaces.TryGetValue(pair.Key, out var current) && current == pair.Value);
                    }
                }
                catch { /* Pierwotny błąd zachowuje pierwszeństwo; stan umożliwi cleanup przy kolejnym starcie. */ }
            }
            if (failure is LauncherException launcherFailure && launcherFailure.Code != "ZT_NETWORK_CONFLICT")
                await telemetry.TrySendAsync(token, telemetry.Create(EventNameFor(launcherFailure.Code), "FAIL", launcherFailure.Code), cancellationToken);
            // Once enrollment has completed, retain its device token even after a
            // successful local rollback.  Central access remains lease-scoped and
            // the next launch must still be able to observe/reconcile that lease.
            if (failure is LauncherException { Code: "ZT_PUBLIC_ROUTE_CHANGED" } && !leaveSucceeded)
                throw new LauncherException("ZT_PUBLIC_ROUTE_RECOVERY_FAILED", "ZeroTier został odłączony, ale nie potwierdzono odzyskania wcześniejszego routingu publicznego.");
            throw;
        }
    }

    private async Task<ProvisioningResult> ResumeActiveStateAsync(
        ClientState state,
        LeaseStatusResponse status,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(status.AssignmentId, state.AssignmentId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(status.NetworkId) ||
            !string.Equals(status.NetworkId, state.NetworkId, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(status.AssignedPrefix) ||
            string.IsNullOrWhiteSpace(status.VmIp) ||
            string.IsNullOrWhiteSpace(status.GuestIp))
            throw new LauncherException("ZT_RESUME_STATE_INVALID", "Serwer nie potwierdził parametrów aktywnego przydziału.");

        var assigned = Ipv4Prefix.Parse(status.AssignedPrefix);
        var token = state.DeviceToken;
        var joined = false;
        NetworkSnapshot? beforeJoin = null;
        try
        {
            beforeJoin = networkInspector.Capture();
            await RunPreflightAsync(assigned, token, beforeJoin, cancellationToken, state.NetworkId);

            await zeroTier.EnsureInstalledAsync(cancellationToken);
            var version = await zeroTier.GetVersionAsync(cancellationToken);

            var secondSnapshot = networkInspector.Capture();
            await RunPreflightAsync(assigned, token, secondSnapshot, cancellationToken, state.NetworkId);

            await zeroTier.JoinAsync(state.NetworkId, cancellationToken);
            joined = true;
            await telemetry.TrySendAsync(token, telemetry.Create("join_completed", "PASS", zeroTierVersion: version), cancellationToken);

            var refreshedState = state with
            {
                LeaseExpiresAt = status.LeaseExpiresAt ?? state.LeaseExpiresAt,
                AssignedPrefix = status.AssignedPrefix,
                VmIp = status.VmIp,
                GuestIp = status.GuestIp
            };
            storage.SaveState(refreshedState);

            var ready = await zeroTier.WaitUntilReadyAsync(state.NetworkId, status.GuestIp, cancellationToken);
            if (ready is null || ready.InterfaceIndex == 0 ||
                !await WaitForConnectivityAsync(status.VmIp, ready.InterfaceIndex, cancellationToken))
                throw new LauncherException("ZT_CONNECTIVITY_FAILED", "Sieć została zestawiona, ale przypisana maszyna nie odpowiada.");

            var afterJoin = networkInspector.Capture();
            if (!WindowsNetworkInspector.PublicRouteUnchanged(beforeJoin, afterJoin, ready.InterfaceIndex))
                throw new LauncherException("ZT_PUBLIC_ROUTE_CHANGED", "ZeroTier zmienił trasę używaną do ruchu publicznego.");

            await telemetry.TrySendAsync(
                token,
                telemetry.Create("connectivity_ready", "PASS", zeroTierVersion: version, networkStatus: ready.Status, pathType: "UNKNOWN"),
                cancellationToken);
            return await CompleteWithParsecAsync(state.AssignmentId, token, cancellationToken);
        }
        catch (Exception failure)
        {
            var leaveSucceeded = !joined;
            if (joined)
            {
                try
                {
                    await zeroTier.LeaveAsync(state.NetworkId, cancellationToken);
                    leaveSucceeded = true;
                    if (failure is LauncherException { Code: "ZT_PUBLIC_ROUTE_CHANGED" } && beforeJoin is not null)
                    {
                        var afterRollback = networkInspector.Capture();
                        leaveSucceeded = beforeJoin.BestInterfaces.All(pair =>
                            afterRollback.BestInterfaces.TryGetValue(pair.Key, out var current) && current == pair.Value);
                    }
                }
                catch { }
            }
            if (failure is LauncherException launcherFailure && launcherFailure.Code != "ZT_NETWORK_CONFLICT")
                await telemetry.TrySendAsync(token, telemetry.Create(EventNameFor(launcherFailure.Code), "FAIL", launcherFailure.Code), cancellationToken);
            if (failure is LauncherException { Code: "ZT_PUBLIC_ROUTE_CHANGED" } && !leaveSucceeded)
                throw new LauncherException("ZT_PUBLIC_ROUTE_RECOVERY_FAILED", "ZeroTier został odłączony, ale nie potwierdzono odzyskania wcześniejszego routingu publicznego.");
            throw;
        }
    }

    private async Task<ProvisioningResult> CompleteWithParsecAsync(
        string assignmentId,
        string token,
        CancellationToken cancellationToken)
    {
        try
        {
            await parsec.StartAsync(assignmentId, cancellationToken);
            await telemetry.TrySendAsync(token, telemetry.Create("parsec_launch_completed", "PASS"), cancellationToken);
            return new("Połączenie przygotowane. Uruchomiono Parsec.", true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var code = ex is LauncherException launcherException ? launcherException.Code : "PARSEC_LAUNCH_FAILED";
            await telemetry.TrySendAsync(token, telemetry.Create("parsec_launch_failed", "FAIL", code), cancellationToken);
            return new(
                $"Połączenie przygotowane, ale nie udało się uruchomić Parsec ({code}). Uruchom aplikację ponownie, aby spróbować ponownie.",
                true);
        }
    }

    private async Task<bool> WaitForConnectivityAsync(string vmIp, uint zeroTierInterfaceIndex, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (networkInspector.BestInterfaceFor(vmIp) == zeroTierInterfaceIndex &&
                await CanReachAsync(vmIp, cancellationToken))
                return true;
            await Task.Delay(1500, cancellationToken);
        }
        return false;
    }

    private static async Task<bool> CanReachAsync(string vmIp, CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reply = await ping.SendPingAsync(vmIp, 3000);
            return reply.Status == IPStatus.Success;
        }
        catch (PingException) { return false; }
    }

    private async Task CaptureAndPreflightAsync(Ipv4Prefix assigned, string token, CancellationToken cancellationToken)
    {
        NetworkSnapshot snapshot;
        try { snapshot = networkInspector.Capture(); }
        catch (LauncherException ex) when (ex.Code == "ZT_NETWORK_PREFLIGHT_UNAVAILABLE")
        {
            await telemetry.TrySendAsync(token, telemetry.Create("network_preflight_failed", "BLOCKED", ex.Code), cancellationToken);
            throw;
        }
        await RunPreflightAsync(assigned, token, snapshot, cancellationToken);
    }

    private async Task RunPreflightAsync(
        Ipv4Prefix assigned,
        string token,
        NetworkSnapshot snapshot,
        CancellationToken cancellationToken,
        string? resumableNetworkId = null)
    {
        var conflict = NetworkConflictDetector.Find(assigned, snapshot.Observations, resumableNetworkId);
        if (conflict is null)
        {
            await telemetry.TrySendAsync(token, telemetry.Create("network_preflight_passed", "PASS"), cancellationToken);
            return;
        }
        await telemetry.TrySendAsync(token, telemetry.Create("network_preflight_failed", "BLOCKED", "ZT_NETWORK_CONFLICT", conflict: conflict), cancellationToken);
        throw new LauncherException(
            "ZT_NETWORK_CONFLICT",
            $"Podsieć {conflict.AssignedPrefix} koliduje z {conflict.ConflictingPrefix} ({conflict.Kind}) na interfejsie „{conflict.InterfaceName}”. Nie zmieniono routingu ani członkostwa ZeroTier.");
    }

    private static void ValidateBootstrap(BootstrapResponse value)
    {
        if (value.ExpiresAt <= DateTimeOffset.UtcNow) throw new LauncherException("ZT_BOOTSTRAP_EXPIRED", "Kod aktywacyjny wygasł.");
        if (value.NetworkId.Length != 16 || !value.NetworkId.All(Uri.IsHexDigit)) throw new LauncherException("ZT_NETWORK_ID_INVALID", "Serwer zwrócił nieprawidłowy Network ID.");
        try { _ = Ipv4Prefix.Parse(value.AssignedPrefix); }
        catch (FormatException)
        {
            throw new LauncherException("ZT_ASSIGNED_PREFIX_INVALID", "Serwer zwrócił nieprawidłową podsieć ZeroTier.");
        }
        if (!IPAddress.TryParse(value.VmIp, out var vmIp) || vmIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new LauncherException("ZT_VM_IP_INVALID", "Serwer zwrócił nieprawidłowy adres VM.");
    }

    private static string EventNameFor(string code) => code == "ZT_PUBLIC_ROUTE_CHANGED" ? "public_route_changed" : "provisioning_failed";
}
