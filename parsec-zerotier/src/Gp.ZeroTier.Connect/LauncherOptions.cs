namespace Gp.ZeroTier.Connect;

public sealed record LauncherOptions(
    Uri BackendBaseUri,
    string StateDirectory,
    string ParsecDirectory)
{
    public static LauncherOptions CreateDefault()
    {
        var stateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GP", "ZeroTierConnect");
        return new(
            new Uri("https://dysk.gp.edu.pl/", UriKind.Absolute),
            stateDirectory,
            Path.Combine(stateDirectory, "ParsecPortable"));
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
