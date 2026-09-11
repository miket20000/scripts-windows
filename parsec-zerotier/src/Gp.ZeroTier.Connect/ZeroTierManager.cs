using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;

namespace Gp.ZeroTier.Connect;

public sealed record ZeroTierNetwork(
    string NetworkId,
    string Status,
    IReadOnlyList<string> AssignedAddresses,
    uint InterfaceIndex,
    bool AllowManaged,
    bool AllowDefault,
    bool AllowGlobal,
    bool AllowDns);

public sealed class ZeroTierManager(LauncherOptions options)
{
    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZeroTier", "One");
    private static string CliPath => FindCliPath() ?? Path.Combine(DataDirectory, "zerotier-cli.bat");

    public bool IsInstalled => FindCliPath() is not null;

    public async Task EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        if (IsInstalled)
        {
            await VerifyInstalledPublisherAsync(cancellationToken);
            await EnsureVersionAsync(cancellationToken);
            return;
        }
        if (Directory.Exists(DataDirectory))
            throw new LauncherException("ZT_VERSION_UNSUPPORTED", "Wykryto nierozpoznaną istniejącą instalację ZeroTier. Nie została zmieniona.");

        var installDirectory = Path.Combine(Path.GetTempPath(), "GP-ZeroTier-Connect");
        Directory.CreateDirectory(installDirectory);
        var msiPath = Path.Combine(installDirectory, "zerotier-one-1.16.2.msi");
        using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
        using (var response = await client.GetAsync(options.ZeroTierMsiUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var output = File.Create(msiPath);
            await response.Content.CopyToAsync(output, cancellationToken);
        }

        var hash = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(msiPath), cancellationToken));
        if (!hash.Equals(options.ZeroTierMsiSha256, StringComparison.OrdinalIgnoreCase))
            throw new LauncherException("ZT_INSTALLER_HASH_INVALID", "Pobrany instalator ZeroTier ma nieprawidłowy skrót SHA-256.");
        await VerifyAuthenticodeAsync(msiPath, cancellationToken);

        var msiexec = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        var installed = await ProcessRunner.RunAsync(msiexec, ["/i", msiPath, "/qn", "/norestart"], TimeSpan.FromMinutes(3), cancellationToken);
        if (installed.ExitCode is not (0 or 3010))
            throw new LauncherException("ZT_INSTALL_FAILED", $"Instalator ZeroTier zakończył się kodem {installed.ExitCode}.");
        if (!IsInstalled) throw new LauncherException("ZT_INSTALL_FAILED", "Po instalacji nie odnaleziono oficjalnego klienta ZeroTier.");
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
        throw new LauncherException("ZT_NODE_ID_INVALID", "Klient ZeroTier nie zwrócił poprawnego Node ID.");
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
        if (current is null) throw new LauncherException("ZT_JOIN_FAILED", "Nie potwierdzono członkostwa w sieci ZeroTier.");
        if (!current.AllowManaged || current.AllowDefault || current.AllowGlobal || current.AllowDns)
            throw new LauncherException("ZT_FLAGS_FAILED", "Flagi ZeroTier nie zostały odczytane jako 1/0/0/0.");
    }

    public async Task LeaveAsync(string networkId, CancellationToken cancellationToken)
    {
        ValidateNetworkId(networkId);
        if (await GetNetworkAsync(networkId, cancellationToken) is null) return;
        await RunCliRequiredAsync(["leave", networkId], "ZT_LEAVE_FAILED", cancellationToken);
        if (await GetNetworkAsync(networkId, cancellationToken) is not null)
            throw new LauncherException("ZT_LEAVE_FAILED", "Nie potwierdzono opuszczenia sieci ZeroTier.");
    }

    public async Task<ZeroTierNetwork?> WaitUntilReadyAsync(string networkId, string expectedAddress, CancellationToken cancellationToken)
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

    public static async Task<bool> CanReachAsync(string vmIp, CancellationToken cancellationToken)
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

    private async Task EnsureVersionAsync(CancellationToken cancellationToken)
    {
        var version = await GetVersionAsync(cancellationToken);
        var reportedVersion = version.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (!string.Equals(reportedVersion, options.ZeroTierVersion, StringComparison.Ordinal))
            throw new LauncherException("ZT_VERSION_UNSUPPORTED", $"Wymagany ZeroTier {options.ZeroTierVersion}; wykryto {version}.");
    }

    private static async Task VerifyAuthenticodeAsync(string path, CancellationToken cancellationToken)
    {
        const string script = "$s=Get-AuthenticodeSignature -LiteralPath $args[0]; if($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch 'O=ZEROTIER, INC\\.') { exit 23 }";
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var result = await ProcessRunner.RunAsync(powershell, ["-NoProfile", "-NonInteractive", "-Command", script, path], TimeSpan.FromSeconds(30), cancellationToken);
        if (result.ExitCode != 0)
            throw new LauncherException("ZT_INSTALLER_SIGNATURE_INVALID", "Podpis Authenticode instalatora ZeroTier jest nieprawidłowy lub pochodzi od innego wydawcy.");
    }

    private static async Task VerifyInstalledPublisherAsync(CancellationToken cancellationToken)
    {
        var cliDirectory = Path.GetDirectoryName(CliPath) ?? "";
        var executable = new[] { "zerotier-one_x64.exe", "zerotier-one.exe" }
            .Select(name => Path.Combine(cliDirectory, name))
            .FirstOrDefault(File.Exists);
        if (executable is null)
            throw new LauncherException("ZT_VERSION_UNSUPPORTED", "Nie można potwierdzić oficjalnego pliku wykonywalnego istniejącej instalacji ZeroTier.");
        await VerifyAuthenticodeAsync(executable, cancellationToken);
    }

    private async Task<ZeroTierNetwork?> GetNetworkAsync(string networkId, CancellationToken cancellationToken)
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
        catch (JsonException) { throw new LauncherException("ZT_CLI_INVALID_RESPONSE", "Klient ZeroTier zwrócił nieprawidłową odpowiedź."); }
    }

    private async Task RunCliRequiredAsync(IEnumerable<string> command, string code, CancellationToken cancellationToken)
    {
        var result = await RunCliProcessAsync(command, cancellationToken);
        if (result.ExitCode != 0) throw new LauncherException(code, "Polecenie ZeroTier nie powiodło się.");
    }

    private async Task<string> RunCliAsync(IEnumerable<string> command, CancellationToken cancellationToken)
    {
        var result = await RunCliProcessAsync(command, cancellationToken);
        if (result.ExitCode != 0) throw new LauncherException("ZT_CLI_FAILED", "Nie można odczytać stanu klienta ZeroTier.");
        return result.StandardOutput;
    }

    private static Task<ProcessResult> RunCliProcessAsync(IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var values = arguments.ToArray();
        if (values.Any(value => value.Any(character => char.IsWhiteSpace(character) || "&|<>^\"".Contains(character))))
            throw new LauncherException("ZT_CLI_ARGUMENT_INVALID", "Odrzucono nieprawidłowy argument klienta ZeroTier.");
        var comSpec = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var commandLine = $"call \"{CliPath}\" {string.Join(' ', values)}";
        return ProcessRunner.RunAsync(comSpec, ["/d", "/s", "/c", commandLine], TimeSpan.FromSeconds(30), cancellationToken);
    }

    private static void ValidateNetworkId(string networkId)
    {
        if (networkId.Length != 16 || !networkId.All(Uri.IsHexDigit))
            throw new LauncherException("ZT_NETWORK_ID_INVALID", "Serwer zwrócił nieprawidłowy Network ID.");
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
}
