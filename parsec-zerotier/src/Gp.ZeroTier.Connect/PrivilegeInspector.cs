using System.Security.Principal;

namespace Gp.ZeroTier.Connect;

public static class PrivilegeInspector
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
