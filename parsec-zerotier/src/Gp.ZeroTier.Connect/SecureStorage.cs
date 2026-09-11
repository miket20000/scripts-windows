using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Gp.ZeroTier.Connect.Core;

namespace Gp.ZeroTier.Connect;

public sealed class SecureStorage
{
    private const int MaxEvents = 100;
    private const int MaxQueueBytes = 256 * 1024;
    private readonly string directory;
    private readonly string statePath;
    private readonly string queuePath;
    private readonly string instancePath;
    private readonly string droppedPath;
    private bool aclApplied;

    public SecureStorage(string directory)
    {
        this.directory = directory;
        statePath = Path.Combine(directory, "state.dat");
        queuePath = Path.Combine(directory, "telemetry.dat");
        instancePath = Path.Combine(directory, "instance.dat");
        droppedPath = Path.Combine(directory, "telemetry-dropped.dat");
    }

    public ClientState? LoadState() => Load<ClientState>(statePath);
    public void SaveState(ClientState state) => Save(statePath, state);
    public void DeleteState() { if (File.Exists(statePath)) File.Delete(statePath); }

    public string GetClientInstanceId()
    {
        var value = Load<string>(instancePath);
        if (!string.IsNullOrWhiteSpace(value)) return value;
        value = Guid.NewGuid().ToString("N");
        Save(instancePath, value);
        return value;
    }

    public IReadOnlyList<QueuedTelemetry> LoadQueue() => Load<List<QueuedTelemetry>>(queuePath) ?? [];

    public int SaveQueue(IEnumerable<QueuedTelemetry> values)
    {
        var limited = TelemetryQueueLimiter.Limit(values, MaxEvents, MaxQueueBytes, items => JsonSerializer.SerializeToUtf8Bytes(items).Length);
        var queue = limited.Items;
        var dropped = limited.Dropped;
        if (queue.Count == 0)
        {
            if (File.Exists(queuePath)) File.Delete(queuePath);
        }
        else Save(queuePath, queue);
        if (dropped > 0) Save(droppedPath, Load<int>(droppedPath) + dropped);
        return dropped;
    }

    public int GetDroppedCount() => Load<int>(droppedPath);

    public void ClearDroppedCount()
    {
        if (File.Exists(droppedPath)) File.Delete(droppedPath);
    }

    private T? Load<T>(string path)
    {
        if (!File.Exists(path)) return default;
        var cipher = File.ReadAllBytes(path);
        var plain = Dpapi.Unprotect(cipher);
        return JsonSerializer.Deserialize<T>(plain);
    }

    private void Save<T>(string path, T value)
    {
        EnsureDirectory();
        var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        var cipher = Dpapi.Protect(plain);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, cipher);
        File.Move(temp, path, true);
    }

    private void EnsureDirectory()
    {
        Directory.CreateDirectory(directory);
        if (aclApplied) return;
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "icacls.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { directory, "/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F" }
        });
        if (process is null || !process.WaitForExit(10_000) || process.ExitCode != 0)
            throw new LauncherException("ZT_SECURE_STORAGE_ACL_FAILED", "Nie można zabezpieczyć katalogu stanu aplikacji.");
        aclApplied = true;
    }

    private static class Dpapi
    {
        private const int CryptprotectLocalMachine = 0x4;

        public static byte[] Protect(byte[] value) => Transform(value, true);
        public static byte[] Unprotect(byte[] value) => Transform(value, false);

        private static byte[] Transform(byte[] value, bool protect)
        {
            var input = new DataBlob(value);
            try
            {
                DataBlob output;
                var ok = protect
                    ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptprotectLocalMachine, out output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output);
                if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    var result = new byte[output.Length];
                    Marshal.Copy(output.Data, result, 0, output.Length);
                    return result;
                }
                finally { LocalFree(output.Data); }
            }
            finally { input.Dispose(); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob : IDisposable
        {
            public int Length;
            public IntPtr Data;
            public DataBlob(byte[] value)
            {
                Length = value.Length;
                Data = Marshal.AllocHGlobal(value.Length);
                Marshal.Copy(value, 0, Data, value.Length);
            }
            public void Dispose() { if (Data != IntPtr.Zero) Marshal.FreeHGlobal(Data); }
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}

public sealed record QueuedTelemetry(string Token, TelemetryEvent Event);
