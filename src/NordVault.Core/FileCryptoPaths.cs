using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace NordVault.Core;

public static class FileCryptoPaths
{
    public static string Validate(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Filkryptering kräver Windows.");
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || !Regex.IsMatch(path, @"\A[A-Za-z]:\\", RegexOptions.CultureInvariant) ||
            path[2..].Contains(':') || path.Contains('/') || path.Any(char.IsControl) ||
            path.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0 ||
            Regex.IsMatch(path, @"(?:^|\\)\.{1,2}(?:\\|$)|[. ](?:\\|$)|(?:^|\\)(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.[^\\]*)?(?:\\|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new ArgumentException("Välj en vanlig lokal filsökväg utan länkar, nätverk, dataströmmar eller enhetsprefix.", nameof(path));
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var drive = new DriveInfo(root);
        if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new IOException("Filens lokala enhet är inte tillgänglig.");
        AssertNoReparsePoints(full);
        return full;
    }

    public static void AssertNoReparsePoints(string path)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Symboliska länkar och omdirigerade mappar stöds inte. Hämta molnfiler lokalt först.");
            current = Path.GetDirectoryName(current);
        }
    }

    public static void VerifyHandlePath(SafeFileHandle handle, string expected)
    {
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Filens verkliga sökväg kunde inte kontrolleras.");
        var actual = buffer.ToString();
        if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Filens sökväg ändrades. Åtgärden avbröts.");
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder filePath, uint bufferLength, uint flags);
}
