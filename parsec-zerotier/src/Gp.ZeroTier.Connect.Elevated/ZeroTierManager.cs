using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using Gp.ZeroTier.Connect.Core;

namespace Gp.ZeroTier.Connect.Elevated;

public sealed class ZeroTierManager
{
    private static readonly Uri MsiUri = new("https://download.zerotier.com/RELEASES/1.16.2/dist/ZeroTier%20One.msi");
    private const string MsiSha256 = "42514072B0FE44B8F66E0395BCD23A0B1D1642C28ED00831F1527B2F41B14670";
    private const string RequiredVersion = "1.16.2";
    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZeroTier", "One");
    private static string EnginePath => FindEnginePath() ?? Path.Combine(DataDirectory, "zerotier-one_x64.exe");

    public bool IsInstalled => FindCliPath() is not null && FindEnginePath() is not null;

    public async Task EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        if (IsInstalled)
        {
            await VerifyInstalledPublisherAsync(cancellationToken);
            await EnsureVersionAsync(cancellationToken);
            return;
        }
        if (Directory.Exists(DataDirectory))
            throw new PrivilegedException("ZT_VERSION_UNSUPPORTED", "Wykryto nierozpoznaną istniejącą instalację ZeroTier. Nie została zmieniona.");

        var installDirectory = Path.Combine(Path.GetTempPath(), "GP-ZeroTier-Connect");
        Directory.CreateDirectory(installDirectory);
        var msiPath = Path.Combine(installDirectory, "zerotier-one-1.16.2.msi");
        using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
        using (var response = await client.GetAsync(MsiUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var output = File.Create(msiPath);
            await response.Content.CopyToAsync(output, cancellationToken);
        }

        var hash = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(msiPath), cancellationToken));
        if (!hash.Equals(MsiSha256, StringComparison.OrdinalIgnoreCase))
            throw new PrivilegedException("ZT_INSTALLER_HASH_INVALID", "Pobrany instalator ZeroTier ma nieprawidłowy skrót SHA-256.");
        await VerifyAuthenticodeAsync(msiPath, cancellationToken);

        var msiexec = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        var installed = await ProcessRunner.RunAsync(msiexec, ["/i", msiPath, "/qn", "/norestart"], TimeSpan.FromMinutes(3), cancellationToken);
        if (installed.ExitCode is not (0 or 3010))
            throw new PrivilegedException("ZT_INSTALL_FAILED", $"Instalator ZeroTier zakończył się kodem {installed.ExitCode}.");
        if (!IsInstalled) throw new PrivilegedException("ZT_INSTALL_FAILED", "Po instalacji nie odnaleziono oficjalnego klienta ZeroTier.");
        await VerifyInstalledPublisherAsync(cancellationToken);
        await EnsureVersionAsync(cancellationToken);
    }

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken)
    {
        var result = await RunCliAsync(["-v"], cancellationToken);
        return result.Trim();
    }

    public async Task<string> GetNodeIdAsync(CancellationToken cancellationToken)
    {
        using var json = await RunCliJsonAsync(["info"], cancellationToken);
        if (json.RootElement.TryGetProperty("address", out var address))
        {
            var value = address.GetString();
            if (value is { Length: 10 } && value.All(Uri.IsHexDigit)) return value.ToLowerInvariant();
        }
        throw new PrivilegedException("ZT_NODE_ID_INVALID", "Klient ZeroTier nie zwrócił poprawnego Node ID.");
    }

    public async Task JoinAsync(string networkId, CancellationToken cancellationToken)
    {
        ValidateNetworkId(networkId);
        await RunCliRequiredAsync(["join", networkId], "ZT_JOIN_FAILED", cancellationToken);
        await RunCliRequiredAsync(["set", networkId, "allowManaged=1"], "ZT_FLAGS_FAILED", cancellationToken);
        await RunCliRequiredAsync(["set", networkId, "allowDefault=0"], "ZT_FLAGS_FAILED", cancellationToken);
        await RunCliRequiredAsync(["set", networkId, "allowGlobal=0"], "ZT_FLAGS_FAILED", cancellationToken);
        await RunCliRequiredAsync(["set", networkId, "allowDNS=0"], "ZT_FLAGS_FAILED", cancellationToken);
        var current = await GetNetworkAsync(networkId, cancellationToken);
        if (current is null) throw new PrivilegedException("ZT_JOIN_FAILED", "Nie potwierdzono członkostwa w sieci ZeroTier.");
        if (!current.AllowManaged || current.AllowDefault || current.AllowGlobal || current.AllowDns)
            throw new PrivilegedException("ZT_FLAGS_FAILED", "Flagi ZeroTier nie zostały odczytane jako 1/0/0/0.");
    }

    public async Task LeaveAsync(string networkId, CancellationToken cancellationToken)
    {
        ValidateNetworkId(networkId);
        if (await GetNetworkAsync(networkId, cancellationToken) is null) return;
        await RunCliRequiredAsync(["leave", networkId], "ZT_LEAVE_FAILED", cancellationToken);
        if (await GetNetworkAsync(networkId, cancellationToken) is not null)
            throw new PrivilegedException("ZT_LEAVE_FAILED", "Nie potwierdzono opuszczenia sieci ZeroTier.");
    }

    public async Task<ZeroTierNetworkState?> WaitUntilReadyAsync(string networkId, string expectedAddress, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var network = await GetNetworkAsync(networkId, cancellationToken);
            if (network is not null && network.Status == "OK" && network.AssignedAddresses.Any(value => value.StartsWith(expectedAddress + "/", StringComparison.Ordinal)))
                return network;
            await Task.Delay(1500, cancellationToken);
        }
        return null;
    }

    private async Task EnsureVersionAsync(CancellationToken cancellationToken)
    {
        var version = await GetVersionAsync(cancellationToken);
        var reportedVersion = version.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!string.Equals(reportedVersion, RequiredVersion, StringComparison.Ordinal))
            throw new PrivilegedException("ZT_VERSION_UNSUPPORTED", $"Wymagany ZeroTier {RequiredVersion}; wykryto {version}.");
    }

    private static async Task VerifyAuthenticodeAsync(string path, CancellationToken cancellationToken)
    {
        var pathLiteral = ZeroTierInstallationPolicy.QuotePowerShellLiteral(path);
        var script = $"$s=Get-AuthenticodeSignature -LiteralPath {pathLiteral}; if($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch '{ZeroTierInstallationPolicy.PublisherSubjectPattern}') {{ exit 23 }}";
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var result = await ProcessRunner.RunAsync(powershell, ["-NoProfile", "-NonInteractive", "-Command", script], TimeSpan.FromSeconds(30), cancellationToken);
        if (result.ExitCode != 0)
            throw new PrivilegedException("ZT_INSTALLER_SIGNATURE_INVALID", "Podpis Authenticode instalatora ZeroTier jest nieprawidłowy lub pochodzi od innego wydawcy.");
    }

    private static async Task VerifyInstalledPublisherAsync(CancellationToken cancellationToken)
    {
        var executable = FindEnginePath();
        if (executable is null)
            throw new PrivilegedException("ZT_VERSION_UNSUPPORTED", "Nie można potwierdzić oficjalnego pliku wykonywalnego istniejącej instalacji ZeroTier.");
        await VerifyAuthenticodeAsync(executable, cancellationToken);
    }

    private async Task<ZeroTierNetworkState?> GetNetworkAsync(string networkId, CancellationToken cancellationToken)
    {
        using var json = await RunCliJsonAsync(["listnetworks"], cancellationToken);
        IEnumerable<JsonElement> values = json.RootElement.ValueKind switch
        {
            JsonValueKind.Array => json.RootElement.EnumerateArray(),
            JsonValueKind.Object when json.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array => value.EnumerateArray(),
            JsonValueKind.Object => [json.RootElement],
            _ => []
        };
        foreach (var item in values)
        {
            if (!item.TryGetProperty("nwid", out var id) || !string.Equals(id.GetString(), networkId, StringComparison.OrdinalIgnoreCase)) continue;
            var status = item.TryGetProperty("status", out var statusValue) ? statusValue.GetString() ?? "UNKNOWN" : "UNKNOWN";
            var addresses = item.TryGetProperty("assignedAddresses", out var assigned) && assigned.ValueKind == JsonValueKind.Array
                ? assigned.EnumerateArray().Select(value => value.GetString()).OfType<string>().ToArray()
                : [];
            var interfaceIndex = FindInterfaceIndex(addresses);
            return new(
                networkId,
                status,
                addresses,
                interfaceIndex,
                ReadBoolean(item, "allowManaged"),
                ReadBoolean(item, "allowDefault"),
                ReadBoolean(item, "allowGlobal"),
                ReadBoolean(item, "allowDNS"));
        }
        return null;
    }

    private static bool ReadBoolean(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            JsonValueKind.String => value.GetString() is "1" or "true" or "True",
            _ => false
        };
    }

    private static uint FindInterfaceIndex(IReadOnlyList<string> addresses)
    {
        var expected = addresses.Select(value => value.Split('/')[0]).ToHashSet(StringComparer.Ordinal);
        foreach (var item in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!item.GetIPProperties().UnicastAddresses.Any(value => expected.Contains(value.Address.ToString()))) continue;
            return (uint)(item.GetIPProperties().GetIPv4Properties()?.Index ?? 0);
        }
        return 0;
    }

    private async Task<JsonDocument> RunCliJsonAsync(IEnumerable<string> command, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-j" };
        arguments.AddRange(command);
        var output = await RunCliAsync(arguments, cancellationToken);
        try { return JsonDocument.Parse(output); }
        catch (JsonException) { throw new PrivilegedException("ZT_CLI_INVALID_RESPONSE", "Klient ZeroTier zwrócił nieprawidłową odpowiedź."); }
    }

    private async Task RunCliRequiredAsync(IEnumerable<string> command, string code, CancellationToken cancellationToken)
    {
        var result = await RunCliProcessAsync(command, cancellationToken);
        if (result.ExitCode != 0) throw new PrivilegedException(code, "Polecenie ZeroTier nie powiodło się.");
    }

    private async Task<string> RunCliAsync(IEnumerable<string> command, CancellationToken cancellationToken)
    {
        var result = await RunCliProcessAsync(command, cancellationToken);
        if (result.ExitCode != 0) throw new PrivilegedException("ZT_CLI_FAILED", "Nie można odczytać stanu klienta ZeroTier.");
        return result.StandardOutput;
    }

    private static Task<ProcessResult> RunCliProcessAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var values = arguments.ToArray();
        if (values.Any(value => value.Any(character => char.IsWhiteSpace(character) || "&|<>^\"".Contains(character))))
            throw new PrivilegedException("ZT_CLI_ARGUMENT_INVALID", "Odrzucono nieprawidłowy argument klienta ZeroTier.");
        return ProcessRunner.RunAsync(EnginePath, ["-q", .. values], TimeSpan.FromSeconds(30), cancellationToken);
    }

    private static void ValidateNetworkId(string networkId)
    {
        if (networkId.Length != 16 || !networkId.All(Uri.IsHexDigit))
            throw new PrivilegedException("ZT_NETWORK_ID_INVALID", "Serwer zwrócił nieprawidłowy Network ID.");
    }

    private static string? FindCliPath()
    {
        string[] candidates =
        [
            Path.Combine(DataDirectory, "zerotier-cli.bat"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ZeroTier", "One", "zerotier-cli.bat"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ZeroTier", "One", "zerotier-cli.bat")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindEnginePath()
    {
        var cliDirectory = Path.GetDirectoryName(FindCliPath() ?? "") ?? "";
        return ZeroTierInstallationPolicy.GetEngineCandidates(DataDirectory, cliDirectory)
            .FirstOrDefault(File.Exists);
    }
}
