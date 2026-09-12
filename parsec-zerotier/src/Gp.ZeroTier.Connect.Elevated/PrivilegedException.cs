namespace Gp.ZeroTier.Connect.Elevated;

public sealed class PrivilegedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
