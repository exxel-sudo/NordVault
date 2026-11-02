using System.Runtime.Versioning;
using System.Security.AccessControl;

namespace NordVault.Core;
[SupportedOSPlatform("windows")]
internal static class PortableCipherAccess
{
    // Ciphertext is protected by GnuPG. Inherit the destination's policy instead of binding
    // a portable USB file explicitly to the creating computer's Windows account SID.
    public static void Inherit(string path)
    {
        if(!string.Equals(new DriveInfo(Path.GetPathRoot(path)!).DriveFormat,"NTFS",StringComparison.OrdinalIgnoreCase))return;
        var file=new FileInfo(path);var security=file.GetAccessControl(AccessControlSections.Access);
        foreach(FileSystemAccessRule rule in security.GetAccessRules(true,false,typeof(System.Security.Principal.SecurityIdentifier)))security.RemoveAccessRuleSpecific(rule);
        security.SetAccessRuleProtection(false,false);file.SetAccessControl(security);
    }
}
