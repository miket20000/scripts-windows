using System.Text.Json;
using Gp.ZeroTier.Connect.Core;

var tests = new (string Name, Action Run)[]
{
    ("same /29 conflicts", () => Conflict("172.30.253.0/29", "172.30.253.0/29")),
    ("covering /16 conflicts", () => Conflict("172.30.253.8/29", "172.30.0.0/16")),
    ("covered /30 conflicts", () => Conflict("172.30.253.8/29", "172.30.253.8/30")),
    ("unrelated route passes", () => NoConflict("172.30.253.8/29", "10.0.0.0/8")),
    ("default route is ignored", () => NoConflict("172.30.253.8/29", "0.0.0.0/0")),
    ("down interface is ignored", DownInterfaceIsIgnored),
    ("same controlled resume is ignored", ControlledResumeIsIgnored),
    ("controlled resume ignores own host routes", ControlledResumeHostRoutesAreIgnored),
    ("controlled resume rejects broader own route", ControlledResumeBroaderRouteConflicts),
    ("invalid prefix rejected", InvalidPrefixRejected),
    ("bootstrap JSON contract", BootstrapJsonContract),
    ("active status JSON contains safe resume topology", StatusResumeJsonContract),
    ("telemetry omits secrets and assignment", TelemetryContract),
    ("queue drops oldest by count", QueueDropsOldest),
    ("queue drops oldest by bytes", QueueDropsByBytes),
    ("quoted official publisher matches", OfficialPublisherMatches),
    ("PowerShell literal quoting preserves apostrophes", PowerShellLiteralQuotingPreservesApostrophes),
    ("service engine discovery includes ProgramData", EngineDiscoveryIncludesDataDirectory),
    ("Parsec assignment id is path-safe", ParsecAssignmentIdIsPathSafe),
    ("Parsec archive path is normalized", ParsecArchivePathIsNormalized),
    ("Parsec archive traversal is rejected", ParsecArchiveTraversalIsRejected),
    ("Parsec guest profile is enforced", ParsecGuestProfileIsEnforced),
    ("Parsec appdata binds expected DLL", ParsecAppDataBindsExpectedDll),
    ("Parsec official publisher matches", ParsecOfficialPublisherMatches)
};

var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"RESULT {(failed == 0 ? "PASS" : "FAIL")} {tests.Length - failed}/{tests.Length}");
return failed == 0 ? 0 : 1;

static void Conflict(string assigned, string observed)
{
    var result = NetworkConflictDetector.Find(Ipv4Prefix.Parse(assigned), [new(Ipv4Prefix.Parse(observed), "route", "VPN-CG", true)]);
    Assert(result is not null, "expected conflict");
}

static void NoConflict(string assigned, string observed)
{
    var result = NetworkConflictDetector.Find(Ipv4Prefix.Parse(assigned), [new(Ipv4Prefix.Parse(observed), "route", "Ethernet", true)]);
    Assert(result is null, "unexpected conflict");
}

static void DownInterfaceIsIgnored()
{
    var result = NetworkConflictDetector.Find(Ipv4Prefix.Parse("172.30.253.0/29"), [new(Ipv4Prefix.Parse("172.30.253.0/29"), "local_address", "Down", false)]);
    Assert(result is null, "down interface should not conflict");
}

static void ControlledResumeIsIgnored()
{
    var result = NetworkConflictDetector.Find(Ipv4Prefix.Parse("172.30.253.0/29"), [new(Ipv4Prefix.Parse("172.30.253.0/29"), "route", "ZeroTier", true, "1234567890abcdef")], "1234567890abcdef");
    Assert(result is null, "own resumed route should not conflict");
}

static void ControlledResumeHostRoutesAreIgnored()
{
    var assigned = Ipv4Prefix.Parse("172.30.253.8/29");
    var result = NetworkConflictDetector.Find(assigned, [
        new(Ipv4Prefix.Parse("172.30.253.10/32"), "route", "ZeroTier", true, "1234567890abcdef"),
        new(Ipv4Prefix.Parse("172.30.253.15/32"), "route", "ZeroTier", true, "1234567890abcdef")
    ], "1234567890abcdef");
    Assert(result is null, "own host routes inside the assigned prefix should not conflict");
}

static void ControlledResumeBroaderRouteConflicts()
{
    var result = NetworkConflictDetector.Find(
        Ipv4Prefix.Parse("172.30.253.8/29"),
        [new(Ipv4Prefix.Parse("172.30.0.0/16"), "route", "ZeroTier", true, "1234567890abcdef")],
        "1234567890abcdef");
    Assert(result is not null, "broader route on the resumed interface must still conflict");
}

static void InvalidPrefixRejected()
{
    try { _ = Ipv4Prefix.Parse("172.30.253.0/33"); }
    catch (FormatException) { return; }
    throw new Exception("invalid prefix accepted");
}

static void BootstrapJsonContract()
{
    var json = JsonSerializer.Serialize(new BootstrapRequest("001-002"));
    Assert(json == "{\"activation_code\":\"001-002\"}", json);
}

static void StatusResumeJsonContract()
{
    const string json = """
        {"status":"active","assignment_id":"asg_0123456789abcdef0123456789abcdef","lease_expires_at":"2026-07-21T01:35:00Z","network_id":"0123456789abcdef","assigned_prefix":"172.30.253.0/29","vm_ip":"172.30.253.1","guest_ip":"172.30.253.2"}
        """;
    var value = JsonSerializer.Deserialize<LeaseStatusResponse>(json);
    if (value is null) throw new Exception("status response rejected");
    Assert(value.NetworkId == "0123456789abcdef", "network id missing");
    Assert(value.AssignedPrefix == "172.30.253.0/29", "assigned prefix missing");
    Assert(value.VmIp == "172.30.253.1" && value.GuestIp == "172.30.253.2", "endpoint addresses missing");
}

static void TelemetryContract()
{
    var value = new TelemetryEvent("event", "client", "network_preflight_failed", "BLOCKED", "ZT_NETWORK_CONFLICT", "1.0", "1.16.2", DateTimeOffset.UnixEpoch, 12, null, null, new("172.30.253.0/29", "172.30.0.0/16", "route", "VPN-CG"));
    var json = JsonSerializer.Serialize(value);
    Assert(!json.Contains("assignment", StringComparison.OrdinalIgnoreCase), json);
    Assert(!json.Contains("token", StringComparison.OrdinalIgnoreCase), json);
    Assert(json.Contains("ZT_NETWORK_CONFLICT", StringComparison.Ordinal), json);
    Assert(json.Contains("\"connection_path\"", StringComparison.Ordinal), json);
    Assert(!json.Contains("path_type", StringComparison.Ordinal), json);
}

static void QueueDropsOldest()
{
    var result = TelemetryQueueLimiter.Limit([1, 2, 3, 4], 3, 100, values => values.Count);
    Assert(result.Dropped == 1 && result.Items.SequenceEqual([2, 3, 4]), "wrong count limit");
}

static void QueueDropsByBytes()
{
    var result = TelemetryQueueLimiter.Limit(["aaaa", "bbbb", "cccc"], 10, 8, values => values.Sum(value => value.Length));
    Assert(result.Dropped == 1 && result.Items.SequenceEqual(["bbbb", "cccc"]), "wrong byte limit");
}

static void OfficialPublisherMatches()
{
    const string subject = "E=contact@zerotier.com, CN=\"ZEROTIER, INC.\", O=\"ZEROTIER, INC.\", L=Irvine, C=US";
    Assert(ZeroTierInstallationPolicy.IsExpectedPublisherSubject(subject), "quoted official organization rejected");
    Assert(!ZeroTierInstallationPolicy.IsExpectedPublisherSubject("CN=ZEROTIER, INC., O=Other"), "unrelated organization accepted");
}

static void PowerShellLiteralQuotingPreservesApostrophes()
{
    var value = ZeroTierInstallationPolicy.QuotePowerShellLiteral(@"C:\Users\O'Brien\ZeroTier.exe");
    Assert(value == @"'C:\Users\O''Brien\ZeroTier.exe'", "PowerShell literal was not safely quoted");
}

static void EngineDiscoveryIncludesDataDirectory()
{
    var candidates = ZeroTierInstallationPolicy.GetEngineCandidates("C:/ProgramData/ZeroTier/One", "C:/Program Files (x86)/ZeroTier/One").ToArray();
    Assert(candidates.Contains("C:/ProgramData/ZeroTier/One/zerotier-one_x64.exe"), "ProgramData engine missing");
    Assert(candidates.Contains("C:/Program Files (x86)/ZeroTier/One/zerotier-one.exe"), "CLI directory fallback missing");
}

static void ParsecAssignmentIdIsPathSafe()
{
    Assert(ParsecPortablePolicy.IsValidAssignmentId("asg_0123456789abcdef0123456789abcdef"), "valid assignment rejected");
    Assert(!ParsecPortablePolicy.IsValidAssignmentId("../assignment"), "traversal assignment accepted");
    Assert(!ParsecPortablePolicy.IsValidAssignmentId("asg_0123"), "short assignment accepted");
}

static void ParsecArchivePathIsNormalized()
{
    Assert(ParsecPortablePolicy.GetArchiveRelativePath("codinggiants-parsec/service/pservice.exe") == "service/pservice.exe", "wrong relative path");
    Assert(ParsecPortablePolicy.GetArchiveRelativePath("codinggiants-parsec/") == "", "package root was not accepted");
}

static void ParsecArchiveTraversalIsRejected()
{
    foreach (var value in new[]
    {
        "codinggiants-parsec/../outside.exe",
        "other/parsecd.exe",
        "C:/codinggiants-parsec/parsecd.exe",
        "/codinggiants-parsec/parsecd.exe"
    })
    {
        try { _ = ParsecPortablePolicy.GetArchiveRelativePath(value); }
        catch (FormatException) { continue; }
        throw new Exception($"unsafe archive path accepted: {value}");
    }
}

static void ParsecGuestProfileIsEnforced()
{
    const string valid = """
        ["documentation",{"app_flags":{"value":1},"app_host":{"value":false},"app_run_level":{"value":1},"client_automatic_displays":{"value":false},"client_decoder_10bit":{"value":false},"client_decoder_444":{"value":false},"client_decoder_h265":{"value":1},"client_immersive":{"value":1},"client_overlay":{"value":1},"client_overlay_warnings":{"value":1},"client_renderer":{"value":3},"client_windowed":{"value":true},"decoder_software":{"value":0},"network_raw_audio":{"value":0}}]
        """;
    Assert(ParsecPortablePolicy.HasExpectedGuestConfig(valid), "valid guest profile rejected");
    Assert(!ParsecPortablePolicy.HasExpectedGuestConfig(valid.Replace("\"app_host\":{\"value\":false}", "\"app_host\":{\"value\":true}", StringComparison.Ordinal)), "host-enabled profile accepted");
    Assert(!ParsecPortablePolicy.HasExpectedGuestConfig("{}"), "invalid config accepted");
}

static void ParsecAppDataBindsExpectedDll()
{
    var hash = ParsecPortablePolicy.BinarySha256[$"parsecd-{ParsecPortablePolicy.Version}.dll"];
    var valid = $$"""
        {"entry_symbol":"wx_main","hash":"{{hash}}","so_name":"parsecd-{{ParsecPortablePolicy.Version}}.dll"}
        """;
    Assert(ParsecPortablePolicy.HasExpectedAppData(valid, hash), "valid appdata rejected");
    Assert(!ParsecPortablePolicy.HasExpectedAppData(valid, new string('0', 64)), "wrong DLL hash accepted");
}

static void ParsecOfficialPublisherMatches()
{
    Assert(ParsecPortablePolicy.IsExpectedPublisherSubject("parsecd.exe", "CN=Parsec, O=Unity Technologies SF, C=US"), "official runtime organization rejected");
    Assert(ParsecPortablePolicy.IsExpectedPublisherSubject("service/pservice.exe", "CN=Parsec, O=\"Unity Technologies SF\", C=US"), "quoted service organization rejected");
    Assert(ParsecPortablePolicy.IsExpectedPublisherSubject("vusb/parsec-vud.exe", "CN=Parsec, O=\"Parsec Cloud, Inc.\", C=US"), "official VUSB organization rejected");
    Assert(!ParsecPortablePolicy.IsExpectedPublisherSubject("parsecd.exe", "CN=Parsec, O=Parsec Cloud, Inc., C=US"), "wrong per-file organization accepted");
    Assert(!ParsecPortablePolicy.IsExpectedPublisherSubject("unknown.exe", "CN=Parsec, O=Unity Technologies SF, C=US"), "unknown binary accepted");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
