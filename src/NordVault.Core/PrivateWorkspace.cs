using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NordVault.Core;

[SupportedOSPlatform("windows")]
public sealed class PrivateWorkspace : IDisposable
{
    public string Path { get; }
    private readonly string _parent;
    private readonly string _name;
    private bool _disposed;

    public PrivateWorkspace(string? parent = null)
    {
        _parent = System.IO.Path.GetFullPath(parent ?? System.IO.Path.GetTempPath()).TrimEnd('\\');
        FileCryptoPaths.AssertNoReparsePoints(_parent);
        _name = "NV-" + Guid.NewGuid().ToString("N"); Path = System.IO.Path.Combine(_parent, _name);
        if (System.IO.Path.Exists(Path)) throw new IOException("Arbetsmappen finns redan.");
        if (string.Equals(new DriveInfo(System.IO.Path.GetPathRoot(Path)!).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User ?? throw new IOException("Windows-användaren kunde inte identifieras.");
            var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false); security.SetOwner(sid);
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(Path).Create(security);
        }
        else Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        if (_disposed) return;
        var full = System.IO.Path.GetFullPath(Path);
        if (!string.Equals(System.IO.Path.GetDirectoryName(full), _parent, StringComparison.OrdinalIgnoreCase) || System.IO.Path.GetFileName(full) != _name)
            throw new IOException("Arbetsmappens identitet ändrades; ingen rekursiv borttagning utfördes.");
        FileCryptoPaths.AssertNoReparsePoints(full);
        // Never traverse nested reparse points when removing our uniquely created directory.
        foreach (var entry in Directory.EnumerateFileSystemEntries(full)) RejectNestedLinks(entry);
        Directory.Delete(full, true); _disposed = true;
    }

    private static void RejectNestedLinks(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("En länk finns i arbetsmappen; automatisk städning stoppades.");
        if ((attributes & FileAttributes.Directory) != 0)
            foreach (var entry in Directory.EnumerateFileSystemEntries(path)) RejectNestedLinks(entry);
    }
}
