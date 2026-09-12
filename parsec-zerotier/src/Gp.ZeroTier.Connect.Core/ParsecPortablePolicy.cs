using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gp.ZeroTier.Connect.Core;

public static class ParsecPortablePolicy
{
    public const string Version = "150-104a";
    public const string ResourceName = "Gp.ZeroTier.Connect.Assets.codinggiants-parsec-150-104a.zip";
    public const string ArchiveSha256 = "ac7483a8a0021c79492671f06966a52ba2527fcc17a2ddff5fcc148d91b07b55";
    public const string UnityPublisherSubjectPattern = "(^|, )O=\"?Unity Technologies SF\"?(,|$)";
    public const string ParsecPublisherSubjectPattern = "(^|, )O=\"?Parsec Cloud, Inc\\.\"?(,|$)";
    private const string ArchiveRoot = "codinggiants-parsec";
    private static readonly Regex AssignmentPattern = new("^asg_[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    public static IReadOnlyDictionary<string, string> BinarySha256 { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["parsecd.exe"] = "e1181c0ee31fb49e19038081c111387fc70be880fa1e3a783e6ec62004c5b4e0",
        ["parsecd-150-104a.dll"] = "8c8c4299ddf503b2c25c0905b0a0820f3e8238c252e9ace37bf25964ef4a8f6f",
        ["service/pservice.exe"] = "566f3dfd079b1c3129aae68645f0005a869544078aa86b0a14864a36ad7aa9cf",
        ["vusb/parsec-vud.exe"] = "f8db024b61c36e5d45ca5b485bf855dbfe1d0523333158e873d7deb4d86ec0e4"
    };

    public static IReadOnlyDictionary<string, string> PublisherSubjectPatterns { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["parsecd.exe"] = UnityPublisherSubjectPattern,
        ["parsecd-150-104a.dll"] = UnityPublisherSubjectPattern,
        ["service/pservice.exe"] = UnityPublisherSubjectPattern,
        ["vusb/parsec-vud.exe"] = ParsecPublisherSubjectPattern
    };

    public static IReadOnlySet<string> ExpectedFiles { get; } = new HashSet<string>(BinarySha256.Keys, StringComparer.OrdinalIgnoreCase)
    {
        "README.txt",
        "appdata.json",
        "config.json"
    };

    public static bool IsValidAssignmentId(string value) => AssignmentPattern.IsMatch(value);

    public static bool IsExpectedPublisherSubject(string relativePath, string subject) =>
        PublisherSubjectPatterns.TryGetValue(relativePath, out var pattern) &&
        Regex.IsMatch(subject, pattern, RegexOptions.CultureInvariant);

    public static string GetArchiveRelativePath(string entryName)
    {
        var normalized = entryName.Replace('\\', '/').TrimEnd('/');
        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            Regex.IsMatch(normalized, "^[A-Za-z]:", RegexOptions.CultureInvariant))
            throw new FormatException("Archive entry is rooted.");

        var parts = normalized.Split('/');
        if (parts.Any(part => part is "" or "." or "..") ||
            parts.Length == 0 ||
            !string.Equals(parts[0], ArchiveRoot, StringComparison.Ordinal))
            throw new FormatException("Archive entry is outside the package root.");

        return string.Join('/', parts.Skip(1));
    }

    public static bool HasExpectedGuestConfig(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return false;
            var settings = document.RootElement.EnumerateArray().LastOrDefault(value => value.ValueKind == JsonValueKind.Object);
            if (settings.ValueKind != JsonValueKind.Object) return false;

            return HasValue(settings, "app_flags", 1) &&
                HasValue(settings, "app_host", false) &&
                HasValue(settings, "app_run_level", 1) &&
                HasValue(settings, "client_automatic_displays", false) &&
                HasValue(settings, "client_decoder_10bit", false) &&
                HasValue(settings, "client_decoder_444", false) &&
                HasValue(settings, "client_decoder_h265", 1) &&
                HasValue(settings, "client_immersive", 1) &&
                HasValue(settings, "client_overlay", 1) &&
                HasValue(settings, "client_overlay_warnings", 1) &&
                HasValue(settings, "client_renderer", 3) &&
                HasValue(settings, "client_windowed", true) &&
                HasValue(settings, "decoder_software", 0) &&
                HasValue(settings, "network_raw_audio", 0);
        }
        catch (JsonException) { return false; }
    }

    public static bool HasExpectedAppData(string json, string dllSha256)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("entry_symbol", out var symbol) && symbol.GetString() == "wx_main" &&
                root.TryGetProperty("so_name", out var name) && name.GetString() == $"parsecd-{Version}.dll" &&
                root.TryGetProperty("hash", out var hash) && string.Equals(hash.GetString(), dllSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }

    private static bool HasValue(JsonElement settings, string name, bool expected) =>
        settings.TryGetProperty(name, out var setting) &&
        setting.TryGetProperty("value", out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean() == expected;

    private static bool HasValue(JsonElement settings, string name, int expected) =>
        settings.TryGetProperty(name, out var setting) &&
        setting.TryGetProperty("value", out var value) &&
        value.TryGetInt32(out var actual) && actual == expected;
}
