using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gp.ZeroTier.Connect.Core;

public static class PrivilegedOperations
{
    public const string PrepareStorage = "prepare_storage";
    public const string IsInstalled = "is_installed";
    public const string EnsureInstalled = "ensure_installed";
    public const string GetVersion = "get_version";
    public const string GetNodeId = "get_node_id";
    public const string Join = "join";
    public const string Leave = "leave";
    public const string WaitUntilReady = "wait_until_ready";
    public const string Shutdown = "shutdown";
}

public sealed record PrivilegedRequest(
    string RequestId,
    string Operation,
    string? NetworkId = null,
    string? ExpectedAddress = null);

public sealed record PrivilegedResponse(
    string RequestId,
    bool Success,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    bool? BooleanValue = null,
    string? TextValue = null,
    ZeroTierNetworkState? NetworkValue = null);

public sealed record ZeroTierNetworkState(
    string NetworkId,
    string Status,
    IReadOnlyList<string> AssignedAddresses,
    uint InterfaceIndex,
    bool AllowManaged,
    bool AllowDefault,
    bool AllowGlobal,
    bool AllowDns);

public static class PrivilegedRequestPolicy
{
    private static readonly Regex RequestIdPattern = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    public static bool IsValid(PrivilegedRequest request)
    {
        if (!RequestIdPattern.IsMatch(request.RequestId)) return false;
        return request.Operation switch
        {
            PrivilegedOperations.PrepareStorage or
            PrivilegedOperations.IsInstalled or
            PrivilegedOperations.EnsureInstalled or
            PrivilegedOperations.GetVersion or
            PrivilegedOperations.GetNodeId or
            PrivilegedOperations.Shutdown => request.NetworkId is null && request.ExpectedAddress is null,
            PrivilegedOperations.Join or PrivilegedOperations.Leave =>
                IsNetworkId(request.NetworkId) && request.ExpectedAddress is null,
            PrivilegedOperations.WaitUntilReady =>
                IsNetworkId(request.NetworkId) && IsIpv4Address(request.ExpectedAddress),
            _ => false
        };
    }

    private static bool IsNetworkId(string? value) =>
        value is { Length: 16 } && value.All(Uri.IsHexDigit);

    private static bool IsIpv4Address(string? value) =>
        IPAddress.TryParse(value, out var address) && address.AddressFamily == AddressFamily.InterNetwork;
}

public static class PrivilegedIpcFrame
{
    public const int MaxPayloadBytes = 16 * 1024;

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length is <= 0 or > MaxPayloadBytes)
            throw new InvalidDataException("IPC payload exceeds the allowed size.");
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxPayloadBytes)
            throw new InvalidDataException("IPC payload exceeds the allowed size.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload)
            ?? throw new InvalidDataException("IPC payload is empty.");
    }
}
