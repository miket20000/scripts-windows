namespace Gp.ZeroTier.Connect;

public sealed record LauncherOptions(
    Uri BackendBaseUri,
    string StateDirectory,
    string ParsecDirectory,
    Uri ZeroTierMsiUri,
    string ZeroTierMsiSha256,
    string ZeroTierVersion)
{
    public static LauncherOptions CreateDefault()
    {
        var stateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GP", "ZeroTierConnect");
        return new(
            new Uri("https://dysk.gp.edu.pl/", UriKind.Absolute),
            stateDirectory,
            Path.Combine(stateDirectory, "ParsecPortable"),
            new Uri("https://download.zerotier.com/RELEASES/1.16.2/dist/ZeroTier%20One.msi"),
            "42514072B0FE44B8F66E0395BCD23A0B1D1642C28ED00831F1527B2F41B14670",
            "1.16.2");
    }
}

public class LauncherException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class BackendException(string code, string message, System.Net.HttpStatusCode statusCode)
    : LauncherException(code, message)
{
    public System.Net.HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed record ProvisioningResult(string Message, bool HasActiveLease);
