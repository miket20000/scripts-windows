using System.Net;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Gp.ZeroTier.Connect.Core;

public sealed record BootstrapRequest([property: JsonPropertyName("activation_code")] string ActivationCode);

public sealed record BootstrapResponse(
    [property: JsonPropertyName("bootstrap_token")] string BootstrapToken,
    [property: JsonPropertyName("assignment_id")] string AssignmentId,
    [property: JsonPropertyName("network_id")] string NetworkId,
    [property: JsonPropertyName("assigned_prefix")] string AssignedPrefix,
    [property: JsonPropertyName("vm_ip")] string VmIp,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

public sealed record EnrollRequest([property: JsonPropertyName("node_id")] string NodeId);

public sealed record EnrollResponse(
    [property: JsonPropertyName("device_token")] string DeviceToken,
    [property: JsonPropertyName("assignment_id")] string AssignmentId,
    [property: JsonPropertyName("guest_ip")] string GuestIp,
    [property: JsonPropertyName("lease_expires_at")] DateTimeOffset LeaseExpiresAt);

public sealed record LeaseStatusResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("assignment_id")] string AssignmentId);

public sealed record CleanupAckRequest(
    [property: JsonPropertyName("network_id")] string NetworkId,
    [property: JsonPropertyName("result")] string Result);

public sealed record ClientState(
    string AssignmentId,
    string NetworkId,
    string DeviceToken,
    DateTimeOffset LeaseExpiresAt);

public sealed record Ipv4Prefix(uint Network, int PrefixLength)
{
    public static Ipv4Prefix Parse(string value)
    {
        var parts = value.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !int.TryParse(parts[1], out var prefixLength) || prefixLength is < 0 or > 32)
            throw new FormatException($"Nieprawidłowy prefiks IPv4: {value}");

        var raw = address.GetAddressBytes();
        var host = ((uint)raw[0] << 24) | ((uint)raw[1] << 16) | ((uint)raw[2] << 8) | raw[3];
        var mask = prefixLength == 0 ? 0U : uint.MaxValue << (32 - prefixLength);
        return new(host & mask, prefixLength);
    }

    public bool Overlaps(Ipv4Prefix other)
    {
        var commonLength = Math.Min(PrefixLength, other.PrefixLength);
        var mask = commonLength == 0 ? 0U : uint.MaxValue << (32 - commonLength);
        return (Network & mask) == (other.Network & mask);
    }

    public bool IsDefault => PrefixLength == 0;

    public override string ToString()
    {
        var bytes = new[] { (byte)(Network >> 24), (byte)(Network >> 16), (byte)(Network >> 8), (byte)Network };
        return $"{new IPAddress(bytes)}/{PrefixLength}";
    }
}

public sealed record NetworkObservation(
    Ipv4Prefix Prefix,
    string Kind,
    string InterfaceName,
    bool IsInterfaceUp,
    string? NetworkId = null);

public sealed record NetworkConflict(
    string AssignedPrefix,
    string ConflictingPrefix,
    string Kind,
    string InterfaceName);

public static class NetworkConflictDetector
{
    public static NetworkConflict? Find(
        Ipv4Prefix assigned,
        IEnumerable<NetworkObservation> observations,
        string? resumableNetworkId = null)
    {
        foreach (var item in observations)
        {
            if (!item.IsInterfaceUp || item.Prefix.IsDefault)
                continue;
            if (resumableNetworkId is not null &&
                string.Equals(item.NetworkId, resumableNetworkId, StringComparison.OrdinalIgnoreCase) &&
                item.Prefix == assigned)
                continue;
            if (assigned.Overlaps(item.Prefix))
                return new(assigned.ToString(), item.Prefix.ToString(), item.Kind, item.InterfaceName);
        }
        return null;
    }
}

public static class TelemetryQueueLimiter
{
    public static (IReadOnlyList<T> Items, int Dropped) Limit<T>(
        IEnumerable<T> values,
        int maxItems,
        int maxBytes,
        Func<IReadOnlyList<T>, int> serializedSize)
    {
        var all = values.ToList();
        var kept = all.TakeLast(maxItems).ToList();
        var dropped = all.Count - kept.Count;
        while (kept.Count > 0 && serializedSize(kept) > maxBytes)
        {
            kept.RemoveAt(0);
            dropped++;
        }
        return (kept, dropped);
    }
}

public static class ZeroTierInstallationPolicy
{
    public const string PublisherSubjectPattern = "(^|, )O=\"?ZEROTIER, INC\\.\"?(,|$)";

    public static bool IsExpectedPublisherSubject(string subject) =>
        Regex.IsMatch(subject, PublisherSubjectPattern, RegexOptions.CultureInvariant);

    public static string QuotePowerShellLiteral(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    public static IEnumerable<string> GetEngineCandidates(string dataDirectory, string cliDirectory) =>
        new[] { dataDirectory, cliDirectory }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(directory => new[] { "zerotier-one_x64.exe", "zerotier-one.exe" }
                .Select(name => Path.Combine(directory, name)));
}

public sealed record TelemetryConflict(
    [property: JsonPropertyName("assigned_prefix")] string AssignedPrefix,
    [property: JsonPropertyName("conflicting_prefix")] string ConflictingPrefix,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("interface_name")] string InterfaceName);

public sealed record TelemetryEvent(
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("client_instance_id")] string ClientInstanceId,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("result")] string Result,
    [property: JsonPropertyName("error_code")] string? ErrorCode,
    [property: JsonPropertyName("launcher_version")] string LauncherVersion,
    [property: JsonPropertyName("zerotier_version")] string? ZeroTierVersion,
    [property: JsonPropertyName("occurred_at")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("duration_ms")] long? DurationMs,
    [property: JsonPropertyName("network_status")] string? NetworkStatus,
    [property: JsonPropertyName("connection_path")] string? PathType,
    [property: JsonPropertyName("conflict")] TelemetryConflict? Conflict);
