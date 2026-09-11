using System.Diagnostics;
using System.Net;
using Gp.ZeroTier.Connect.Core;

namespace Gp.ZeroTier.Connect;

public sealed class ProvisioningService(
    BackendClient backend,
    TelemetryService telemetry,
    SecureStorage storage,
    WindowsNetworkInspector networkInspector,
    ZeroTierManager zeroTier)
{
    public async Task<string?> CleanupExpiredStateAsync(CancellationToken cancellationToken)
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
            return "Poprzednie połączenie nadal ma aktywny lease.";
        if (normalizedStatus is not ("revoked" or "expired" or "invalid")) return null;

        if (zeroTier.IsInstalled)
        {
            await zeroTier.EnsureInstalledAsync(cancellationToken);
            await zeroTier.LeaveAsync(state.NetworkId, cancellationToken);
        }
        await telemetry.TrySendAsync(state.DeviceToken, telemetry.Create("cleanup_completed", "PASS"), cancellationToken);
        try { await backend.CleanupAckAsync(state.DeviceToken, state.NetworkId, cancellationToken); }
        catch when (!cancellationToken.IsCancellationRequested) { }
        storage.DeleteState();
        return "Usunięto lokalne członkostwo zakończonego przydziału.";
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
            state = new(enrollment.AssignmentId, bootstrap.NetworkId, enrollment.DeviceToken, enrollment.LeaseExpiresAt);
            storage.SaveState(state);
            token = enrollment.DeviceToken;
            await telemetry.TrySendAsync(token, telemetry.Create("enrollment_completed", "PASS", zeroTierVersion: version), cancellationToken);

            var ready = await zeroTier.WaitUntilReadyAsync(bootstrap.NetworkId, enrollment.GuestIp, cancellationToken);
            if (ready is null || ready.InterfaceIndex == 0 ||
                networkInspector.BestInterfaceFor(bootstrap.VmIp) != ready.InterfaceIndex ||
                !await ZeroTierManager.CanReachAsync(bootstrap.VmIp, cancellationToken))
                throw new LauncherException("ZT_CONNECTIVITY_FAILED", "Sieć została zestawiona, ale przypisana maszyna nie odpowiada.");

            var afterJoin = networkInspector.Capture();
            if (!WindowsNetworkInspector.PublicRouteUnchanged(beforeJoin, afterJoin, ready.InterfaceIndex))
                throw new LauncherException("ZT_PUBLIC_ROUTE_CHANGED", "ZeroTier zmienił trasę używaną do ruchu publicznego.");

            await telemetry.TrySendAsync(
                token,
                telemetry.Create("connectivity_ready", "PASS", zeroTierVersion: version, networkStatus: ready.Status, pathType: "UNKNOWN"),
                cancellationToken);
            return new("Połączenie przygotowane. Możesz teraz uruchomić Parsec.");
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

    private async Task RunPreflightAsync(Ipv4Prefix assigned, string token, NetworkSnapshot snapshot, CancellationToken cancellationToken)
    {
        var conflict = NetworkConflictDetector.Find(assigned, snapshot.Observations);
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
