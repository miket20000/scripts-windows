using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gp.ZeroTier.Connect.Core;
using Microsoft.Win32.SafeHandles;

namespace Gp.ZeroTier.Connect.Elevated;

internal static class Program
{
    private static readonly Regex PipeNamePattern = new("^gp-zt-[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    private static async Task<int> Main(string[] args)
    {
        if (!TryParseArguments(args, out var pipeName, out var parentProcessId)) return 10;
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 11;

        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(30_000, CancellationToken.None);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverProcessId) ||
                serverProcessId != (uint)parentProcessId)
                return 12;
            var parentUser = ParentProcessIdentity.GetUserSid(parentProcessId);

            var zeroTier = new ZeroTierManager();
            while (pipe.IsConnected)
            {
                PrivilegedRequest request;
                try { request = await PrivilegedIpcFrame.ReadAsync<PrivilegedRequest>(pipe, CancellationToken.None); }
                catch (EndOfStreamException) { break; }
                catch (IOException) { break; }
                catch (InvalidDataException) { return 13; }
                catch (JsonException) { return 13; }

                var response = await HandleAsync(zeroTier, parentUser, request);
                await PrivilegedIpcFrame.WriteAsync(pipe, response, CancellationToken.None);
                if (request.Operation == PrivilegedOperations.Shutdown && response.Success) break;
            }
            return 0;
        }
        catch (TimeoutException) { return 14; }
        catch (IOException) { return 15; }
        catch (UnauthorizedAccessException) { return 16; }
    }

    private static async Task<PrivilegedResponse> HandleAsync(
        ZeroTierManager zeroTier,
        SecurityIdentifier parentUser,
        PrivilegedRequest request)
    {
        if (!PrivilegedRequestPolicy.IsValid(request))
            return Failed(request.RequestId, "ZT_HELPER_REQUEST_INVALID", "Helper odrzucił nieprawidłowe żądanie.");

        try
        {
            switch (request.Operation)
            {
                case PrivilegedOperations.PrepareStorage:
                    StateAccessManager.PrepareForUser(parentUser);
                    return Passed(request.RequestId);
                case PrivilegedOperations.IsInstalled:
                    return Passed(request.RequestId) with { BooleanValue = zeroTier.IsInstalled };
                case PrivilegedOperations.EnsureInstalled:
                    await zeroTier.EnsureInstalledAsync(CancellationToken.None);
                    return Passed(request.RequestId);
                case PrivilegedOperations.GetVersion:
                    return Passed(request.RequestId) with { TextValue = await zeroTier.GetVersionAsync(CancellationToken.None) };
                case PrivilegedOperations.GetNodeId:
                    return Passed(request.RequestId) with { TextValue = await zeroTier.GetNodeIdAsync(CancellationToken.None) };
                case PrivilegedOperations.Join:
                    await zeroTier.JoinAsync(request.NetworkId!, CancellationToken.None);
                    return Passed(request.RequestId);
                case PrivilegedOperations.Leave:
                    await zeroTier.LeaveAsync(request.NetworkId!, CancellationToken.None);
                    return Passed(request.RequestId);
                case PrivilegedOperations.WaitUntilReady:
                    return Passed(request.RequestId) with
                    {
                        NetworkValue = await zeroTier.WaitUntilReadyAsync(
                            request.NetworkId!, request.ExpectedAddress!, CancellationToken.None)
                    };
                case PrivilegedOperations.Shutdown:
                    return Passed(request.RequestId);
                default:
                    return Failed(request.RequestId, "ZT_HELPER_REQUEST_INVALID", "Helper odrzucił nieznaną operację.");
            }
        }
        catch (PrivilegedException ex)
        {
            return Failed(request.RequestId, ex.Code, ex.Message);
        }
        catch
        {
            return Failed(request.RequestId, "ZT_HELPER_FAILED", "Podwyższona operacja ZeroTier nie powiodła się.");
        }
    }

    private static PrivilegedResponse Passed(string requestId) => new(requestId, true);

    private static PrivilegedResponse Failed(string requestId, string code, string message) =>
        new(requestId, false, code, message);

    private static bool TryParseArguments(string[] args, out string pipeName, out int parentProcessId)
    {
        pipeName = "";
        parentProcessId = 0;
        if (args.Length != 4 || args[0] != "--pipe" || args[2] != "--parent" ||
            !PipeNamePattern.IsMatch(args[1]) || !int.TryParse(args[3], out parentProcessId) || parentProcessId <= 0)
            return false;
        pipeName = args[1];
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
