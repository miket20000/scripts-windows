using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Gp.ZeroTier.Connect.Core;
using Microsoft.Win32.SafeHandles;

namespace Gp.ZeroTier.Connect;

public interface IZeroTierController
{
    Task PrepareStorageAccessAsync(CancellationToken cancellationToken);
    Task<bool> IsInstalledAsync(CancellationToken cancellationToken);
    Task EnsureInstalledAsync(CancellationToken cancellationToken);
    Task<string> GetVersionAsync(CancellationToken cancellationToken);
    Task<string> GetNodeIdAsync(CancellationToken cancellationToken);
    Task JoinAsync(string networkId, CancellationToken cancellationToken);
    Task LeaveAsync(string networkId, CancellationToken cancellationToken);
    Task<ZeroTierNetworkState?> WaitUntilReadyAsync(string networkId, string expectedAddress, CancellationToken cancellationToken);
}

public sealed class ElevatedZeroTierClient : IZeroTierController, IAsyncDisposable
{
    private const string HelperFileName = "GP-ZeroTier-Connect.Elevated.exe";
    private readonly SemaphoreSlim gate = new(1, 1);
    private NamedPipeServerStream? pipe;
    private Process? helper;

    public Task PrepareStorageAccessAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(new(NewRequestId(), PrivilegedOperations.PrepareStorage), cancellationToken);

    public async Task<bool> IsInstalledAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(new(NewRequestId(), PrivilegedOperations.IsInstalled), cancellationToken);
        return response.BooleanValue ?? throw InvalidResponse();
    }

    public Task EnsureInstalledAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(new(NewRequestId(), PrivilegedOperations.EnsureInstalled), cancellationToken);

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(new(NewRequestId(), PrivilegedOperations.GetVersion), cancellationToken);
        return response.TextValue ?? throw InvalidResponse();
    }

    public async Task<string> GetNodeIdAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(new(NewRequestId(), PrivilegedOperations.GetNodeId), cancellationToken);
        return response.TextValue ?? throw InvalidResponse();
    }

    public Task JoinAsync(string networkId, CancellationToken cancellationToken) =>
        ExecuteAsync(new(NewRequestId(), PrivilegedOperations.Join, networkId), cancellationToken);

    public Task LeaveAsync(string networkId, CancellationToken cancellationToken) =>
        ExecuteAsync(new(NewRequestId(), PrivilegedOperations.Leave, networkId), cancellationToken);

    public async Task<ZeroTierNetworkState?> WaitUntilReadyAsync(
        string networkId,
        string expectedAddress,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            new(NewRequestId(), PrivilegedOperations.WaitUntilReady, networkId, expectedAddress),
            cancellationToken);
        return response.NetworkValue;
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (pipe is { IsConnected: true })
            {
                try
                {
                    var request = new PrivilegedRequest(NewRequestId(), PrivilegedOperations.Shutdown);
                    await PrivilegedIpcFrame.WriteAsync(pipe, request, CancellationToken.None);
                    _ = await PrivilegedIpcFrame.ReadAsync<PrivilegedResponse>(pipe, CancellationToken.None);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException) { }
            }
            pipe?.Dispose();
            helper?.Dispose();
            pipe = null;
            helper = null;
        }
        finally
        {
            gate.Release();
            gate.Dispose();
        }
    }

    private async Task ExecuteAsync(PrivilegedRequest request, CancellationToken cancellationToken) =>
        _ = await SendAsync(request, cancellationToken);

    private async Task<PrivilegedResponse> SendAsync(PrivilegedRequest request, CancellationToken cancellationToken)
    {
        if (!PrivilegedRequestPolicy.IsValid(request))
            throw new LauncherException("ZT_HELPER_REQUEST_INVALID", "Aplikacja odrzuciła nieprawidłową operację uprzywilejowaną.");

        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedAsync(cancellationToken);
            try
            {
                await PrivilegedIpcFrame.WriteAsync(pipe!, request, cancellationToken);
                var response = await PrivilegedIpcFrame.ReadAsync<PrivilegedResponse>(pipe!, cancellationToken);
                if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
                    throw InvalidResponse();
                if (!response.Success)
                    throw new LauncherException(
                        response.ErrorCode ?? "ZT_HELPER_FAILED",
                        response.ErrorMessage ?? "Podwyższona operacja ZeroTier nie powiodła się.");
                return response;
            }
            catch (LauncherException) { throw; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or System.Text.Json.JsonException)
            {
                ResetConnection();
                throw new LauncherException("ZT_HELPER_DISCONNECTED", "Utracono bezpieczne połączenie z helperem ZeroTier.");
            }
        }
        finally { gate.Release(); }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (pipe is { IsConnected: true } && helper is { HasExited: false }) return;
        ResetConnection();

        var helperPath = Path.Combine(AppContext.BaseDirectory, HelperFileName);
        if (!File.Exists(helperPath))
            throw new LauncherException("ZT_HELPER_MISSING", "Brakuje podwyższonego helpera ZeroTier.");

        await using var helperLock = new FileStream(helperPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await VerifyHelperAuthenticityAsync(helperPath, cancellationToken);

        var pipeName = $"gp-zt-{Guid.NewGuid():N}";
        var newPipe = CreatePipe(pipeName);
        Process? started = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = helperPath,
                Arguments = $"--pipe {pipeName} --parent {Environment.ProcessId}",
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas"
            };
            try { started = Process.Start(startInfo); }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new LauncherException("ZT_ELEVATION_CANCELLED", "Anulowano zgodę UAC wymaganą do obsługi ZeroTier.");
            }
            if (started is null)
                throw new LauncherException("ZT_HELPER_START_FAILED", "Nie można uruchomić podwyższonego helpera ZeroTier.");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));
            var connectionTask = newPipe.WaitForConnectionAsync(timeoutCts.Token);
            var exitTask = started.WaitForExitAsync(timeoutCts.Token);
            try
            {
                var completed = await Task.WhenAny(connectionTask, exitTask);
                if (completed == exitTask)
                {
                    await exitTask;
                    throw new LauncherException("ZT_HELPER_START_FAILED", "Podwyższony helper zakończył pracę przed zestawieniem bezpiecznego kanału.");
                }
                await connectionTask;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new LauncherException("ZT_HELPER_TIMEOUT", "Podwyższony helper nie połączył się w dozwolonym czasie.");
            }
            timeoutCts.Cancel();

            if (!GetNamedPipeClientProcessId(newPipe.SafePipeHandle, out var clientProcessId) ||
                clientProcessId != (uint)started.Id)
                throw new LauncherException("ZT_HELPER_IDENTITY_INVALID", "Nie potwierdzono tożsamości procesu helpera ZeroTier.");

            pipe = newPipe;
            helper = started;
            newPipe = null!;
            started = null;
        }
        finally
        {
            newPipe?.Dispose();
            started?.Dispose();
        }
    }

    private static async Task VerifyHelperAuthenticityAsync(string helperPath, CancellationToken cancellationToken)
    {
        var mainPath = Environment.ProcessPath ?? throw new LauncherException("ZT_HELPER_SIGNATURE_INVALID", "Nie można ustalić ścieżki głównej aplikacji.");
        var main = ZeroTierInstallationPolicy.QuotePowerShellLiteral(mainPath);
        var helper = ZeroTierInstallationPolicy.QuotePowerShellLiteral(helperPath);
        var script = $"$m=Get-AuthenticodeSignature -LiteralPath {main};$h=Get-AuthenticodeSignature -LiteralPath {helper};if($m.Status -eq 'Valid'){{if($h.Status -ne 'Valid' -or $null -eq $m.SignerCertificate -or $null -eq $h.SignerCertificate -or $m.SignerCertificate.Thumbprint -ne $h.SignerCertificate.Thumbprint){{exit 23}}}}elseif($m.Status -ne 'NotSigned' -or $h.Status -ne 'NotSigned'){{exit 23}}";
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var result = await ProcessRunner.RunAsync(
            powershell,
            ["-NoProfile", "-NonInteractive", "-Command", script],
            TimeSpan.FromSeconds(30),
            cancellationToken);
        if (result.ExitCode != 0)
            throw new LauncherException("ZT_HELPER_SIGNATURE_INVALID", "Helper ZeroTier nie ma podpisu zgodnego z główną aplikacją.");
    }

    private static NamedPipeServerStream CreatePipe(string pipeName)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security);
    }

    private void ResetConnection()
    {
        pipe?.Dispose();
        helper?.Dispose();
        pipe = null;
        helper = null;
    }

    private static LauncherException InvalidResponse() =>
        new("ZT_HELPER_RESPONSE_INVALID", "Helper ZeroTier zwrócił nieprawidłową odpowiedź.");

    private static string NewRequestId() => Guid.NewGuid().ToString("N");

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
