using System.Security.AccessControl;
using System.Security.Principal;

namespace Gp.ZeroTier.Connect.Elevated;

public static class StateAccessManager
{
    private static readonly string StateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "GP", "ZeroTierConnect");

    public static void PrepareForUser(SecurityIdentifier user)
    {
        try
        {
            Directory.CreateDirectory(StateDirectory);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            AddFullControl(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
            AddFullControl(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            AddFullControl(security, user);
            new DirectoryInfo(StateDirectory).SetAccessControl(security);
        }
        catch (SystemException)
        {
            throw new PrivilegedException("ZT_STATE_ACL_FAILED", "Nie można zabezpieczyć katalogu stanu dla bieżącego użytkownika.");
        }
    }

    private static void AddFullControl(DirectorySecurity security, SecurityIdentifier identity)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
    }
}
