using System.Reflection;
using Gp.ZeroTier.Connect.Core;

namespace Gp.ZeroTier.Connect;

public sealed class TelemetryService(BackendClient backend, SecureStorage storage)
{
    private readonly string launcherVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    public TelemetryEvent Create(
        string eventType,
        string result,
        string? errorCode = null,
        string? zeroTierVersion = null,
        long? durationMs = null,
        string? networkStatus = null,
        string? pathType = null,
        NetworkConflict? conflict = null) =>
        new(
            Guid.NewGuid().ToString("N"),
            storage.GetClientInstanceId(),
            eventType,
            result,
            errorCode,
            launcherVersion,
            zeroTierVersion,
            DateTimeOffset.UtcNow,
            durationMs,
            networkStatus,
            pathType,
            conflict is null ? null : new(
                conflict.AssignedPrefix,
                conflict.ConflictingPrefix,
                conflict.Kind,
                conflict.InterfaceName[..Math.Min(conflict.InterfaceName.Length, 128)]));

    public async Task TrySendAsync(string token, TelemetryEvent value, CancellationToken cancellationToken)
    {
        try { await backend.SendTelemetryAsync(token, value, cancellationToken); }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var queue = storage.LoadQueue().Append(new QueuedTelemetry(token, value));
                storage.SaveQueue(queue);
            }
            catch { /* Telemetry persistence must not change provisioning safety decisions. */ }
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        List<QueuedTelemetry> queue;
        try { queue = storage.LoadQueue().ToList(); }
        catch { return; }
        var remaining = new List<QueuedTelemetry>();
        foreach (var item in queue)
        {
            try { await backend.SendTelemetryAsync(item.Token, item.Event, cancellationToken); }
            catch when (!cancellationToken.IsCancellationRequested) { remaining.Add(item); }
        }
        try { storage.SaveQueue(remaining); }
        catch { return; }

        int dropped;
        try { dropped = storage.GetDroppedCount(); }
        catch { return; }
        if (dropped <= 0 || queue.Count == 0) return;
        var token = queue[^1].Token;
        var report = Create("telemetry_events_dropped", "FAIL", "ZT_TELEMETRY_QUEUE_OVERFLOW", durationMs: dropped);
        try
        {
            await backend.SendTelemetryAsync(token, report, cancellationToken);
            try { storage.ClearDroppedCount(); }
            catch { }
        }
        catch when (!cancellationToken.IsCancellationRequested) { }
    }
}
