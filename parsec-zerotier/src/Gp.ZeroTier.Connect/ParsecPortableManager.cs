using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Gp.ZeroTier.Connect.Core;

namespace Gp.ZeroTier.Connect;

public sealed class ParsecPortableManager(LauncherOptions options)
{
    private const int MaxArchiveEntries = 20;
    private const long MaxExpandedBytes = 16L * 1024 * 1024;

    public async Task StartAsync(string assignmentId, CancellationToken cancellationToken)
    {
        var directory = GetAssignmentDirectory(assignmentId);
        await EnsureExtractedAsync(directory, cancellationToken);
        await ValidatePackageAsync(directory, cancellationToken);

        var executable = Path.Combine(directory, "parsecd.exe");
        if (IsOwnedProcessRunning(executable)) return;

        Process? started;
        try
        {
            started = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = directory,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new LauncherException("PARSEC_LAUNCH_FAILED", "Nie można uruchomić klienta Parsec.");
        }

        if (started is null)
            throw new LauncherException("PARSEC_LAUNCH_FAILED", "Nie można uruchomić klienta Parsec.");
        using (started)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            if (!started.HasExited || IsOwnedProcessRunning(executable)) return;
        }
        throw new LauncherException("PARSEC_LAUNCH_FAILED", "Klient Parsec zakończył działanie bezpośrednio po uruchomieniu.");
    }

    public async Task CleanupAsync(string assignmentId, CancellationToken cancellationToken)
    {
        var directory = GetAssignmentDirectory(assignmentId);
        var executable = Path.Combine(directory, "parsecd.exe");
        foreach (var process in FindOwnedProcesses(executable))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited && process.CloseMainWindow())
                        await WaitForExitAsync(process, TimeSpan.FromSeconds(3), cancellationToken);
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                        await WaitForExitAsync(process, TimeSpan.FromSeconds(5), cancellationToken);
                    }
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception)
                {
                    throw new LauncherException("PARSEC_CLEANUP_FAILED", "Nie można zatrzymać klienta Parsec zakończonego przydziału.");
                }
            }
        }

        if (!Directory.Exists(directory)) return;
        try { Directory.Delete(directory, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException("PARSEC_CLEANUP_FAILED", "Nie można usunąć danych Parsec zakończonego przydziału.");
        }
    }

    private async Task EnsureExtractedAsync(string directory, CancellationToken cancellationToken)
    {
        if (Directory.Exists(directory)) return;
        Directory.CreateDirectory(options.ParsecDirectory);
        var staging = Path.Combine(options.ParsecDirectory, $".staging-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(staging);
            await ExtractEmbeddedArchiveAsync(staging, cancellationToken);
            try { Directory.Move(staging, directory); }
            catch (IOException) when (Directory.Exists(directory)) { }
        }
        catch (LauncherException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new LauncherException("PARSEC_PACKAGE_INVALID", "Nie można przygotować wbudowanego pakietu Parsec.");
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, true); } catch { }
            }
        }
    }

    private static async Task ExtractEmbeddedArchiveAsync(string destination, CancellationToken cancellationToken)
    {
        await using var resource = typeof(ParsecPortableManager).Assembly.GetManifestResourceStream(ParsecPortablePolicy.ResourceName)
            ?? throw new LauncherException("PARSEC_PACKAGE_MISSING", "W aplikacji brakuje pakietu Parsec.");
        await using var archiveBuffer = new MemoryStream();
        await resource.CopyToAsync(archiveBuffer, cancellationToken);
        var archiveHash = Convert.ToHexString(SHA256.HashData(archiveBuffer.GetBuffer().AsSpan(0, checked((int)archiveBuffer.Length)))).ToLowerInvariant();
        if (!string.Equals(archiveHash, ParsecPortablePolicy.ArchiveSha256, StringComparison.Ordinal))
            throw new LauncherException("PARSEC_PACKAGE_INVALID", "Suma kontrolna wbudowanego pakietu Parsec jest nieprawidłowa.");

        archiveBuffer.Position = 0;
        using var archive = new ZipArchive(archiveBuffer, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > MaxArchiveEntries || archive.Entries.Sum(entry => entry.Length) > MaxExpandedBytes)
            throw new LauncherException("PARSEC_PACKAGE_INVALID", "Wbudowany pakiet Parsec przekracza dozwolony rozmiar.");

        var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative;
            try { relative = ParsecPortablePolicy.GetArchiveRelativePath(entry.FullName); }
            catch (FormatException)
            {
                throw new LauncherException("PARSEC_PACKAGE_INVALID", "Wbudowany pakiet Parsec zawiera nieprawidłową ścieżkę.");
            }
            if (entry.Name.Length == 0) continue;
            if (relative.Length == 0 || !ParsecPortablePolicy.ExpectedFiles.Contains(relative) || !extracted.Add(relative))
                throw new LauncherException("PARSEC_PACKAGE_INVALID", "Wbudowany pakiet Parsec zawiera nieoczekiwany plik.");

            var target = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
            var targetDirectory = Path.GetDirectoryName(target) ?? destination;
            Directory.CreateDirectory(targetDirectory);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, cancellationToken);
        }
        if (!extracted.SetEquals(ParsecPortablePolicy.ExpectedFiles))
            throw new LauncherException("PARSEC_PACKAGE_INVALID", "Wbudowany pakiet Parsec jest niekompletny.");
    }

    private static async Task ValidatePackageAsync(string directory, CancellationToken cancellationToken)
    {
        foreach (var pair in ParsecPortablePolicy.BinarySha256)
        {
            var path = Path.Combine(directory, pair.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || !string.Equals(await HashFileAsync(path, cancellationToken), pair.Value, StringComparison.OrdinalIgnoreCase))
                throw new LauncherException("PARSEC_PACKAGE_INVALID", $"Plik pakietu Parsec jest nieprawidłowy: {pair.Key}.");
        }

        var configPath = Path.Combine(directory, "config.json");
        var appDataPath = Path.Combine(directory, "appdata.json");
        if (!File.Exists(configPath) || !ParsecPortablePolicy.HasExpectedGuestConfig(await File.ReadAllTextAsync(configPath, cancellationToken)))
            throw new LauncherException("PARSEC_CONFIG_INVALID", "Konfiguracja klienta Parsec nie spełnia profilu ucznia.");
        if (!File.Exists(appDataPath) || !ParsecPortablePolicy.HasExpectedAppData(
                await File.ReadAllTextAsync(appDataPath, cancellationToken),
                ParsecPortablePolicy.BinarySha256[$"parsecd-{ParsecPortablePolicy.Version}.dll"]))
            throw new LauncherException("PARSEC_PACKAGE_INVALID", "Metadane pakietu Parsec są nieprawidłowe.");

        await VerifyAuthenticodeAsync(directory, cancellationToken);
    }

    private static async Task VerifyAuthenticodeAsync(string directory, CancellationToken cancellationToken)
    {
        var checks = ParsecPortablePolicy.PublisherSubjectPatterns.Select(pair =>
        {
            var path = ZeroTierInstallationPolicy.QuotePowerShellLiteral(
                Path.Combine(directory, pair.Key.Replace('/', Path.DirectorySeparatorChar)));
            var pattern = ZeroTierInstallationPolicy.QuotePowerShellLiteral(pair.Value);
            return $"$s=Get-AuthenticodeSignature -LiteralPath {path}; if($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch {pattern}) {{exit 23}}";
        });
        var script = string.Join(';', checks);
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var result = await ProcessRunner.RunAsync(powershell, ["-NoProfile", "-NonInteractive", "-Command", script], TimeSpan.FromSeconds(30), cancellationToken);
        if (result.ExitCode != 0)
            throw new LauncherException("PARSEC_SIGNATURE_INVALID", "Podpis Authenticode pakietu Parsec jest nieprawidłowy lub pochodzi od innego wydawcy.");
    }

    private string GetAssignmentDirectory(string assignmentId)
    {
        if (!ParsecPortablePolicy.IsValidAssignmentId(assignmentId))
            throw new LauncherException("PARSEC_ASSIGNMENT_INVALID", "Identyfikator przydziału nie może zostać użyty do przygotowania Parsec.");
        return Path.Combine(options.ParsecDirectory, assignmentId);
    }

    private static IReadOnlyList<Process> FindOwnedProcesses(string executable)
    {
        var owned = new List<Process>();
        foreach (var process in Process.GetProcessesByName("parsecd"))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) owned.Add(process);
                else process.Dispose();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                process.Dispose();
            }
        }
        return owned;
    }

    private static bool IsOwnedProcessRunning(string executable)
    {
        var processes = FindOwnedProcesses(executable);
        try { return processes.Count > 0; }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static async Task WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try { await process.WaitForExitAsync(timeoutCts.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
    }
}
