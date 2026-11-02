using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace NordVault.Core;

/// <summary>Writes an explicitly chosen recovery file without replacing an existing file.</summary>
public static class RecoveryStore
{
    private static readonly Regex DrivePattern = new(@"\A[A-Z]:\z", RegexOptions.CultureInvariant);
    private static readonly Regex RecoveryPattern = new(@"\A[0-9]{6}(?:-[0-9]{6}){7}\z", RegexOptions.CultureInvariant);
    private static readonly Regex LocalRootPattern = new(@"\A[A-Za-z]:\\\z", RegexOptions.CultureInvariant);

    public static string SaveVerified(string path, RecoveryMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Återställningsfiler måste sparas och verifieras på Windows.");
        ValidateMaterial(material);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("Välj en absolut sökväg på en lokal enhet; nätverk och enhetssökvägar stöds inte.", nameof(path));

        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("Välj en fil i en befintlig mapp.", nameof(path));
        ValidateDestination(fullPath, parent, material);
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            throw new IOException("Återställningsfilen finns redan. Välj ett nytt filnamn; befintliga filer skrivs aldrig över.");

        var document = string.Join(Environment.NewLine,
            "NORDVAULT — ÅTERSTÄLLNINGSNYCKEL FÖR BITLOCKER",
            "Förvara denna fil säkert och åtskild från den krypterade enheten.",
            "Nyckeln ger tillgång till enhetens data. Dela den inte med obehöriga.",
            "",
            $"Enhet: {material.MountPoint}",
            $"Volymidentitet: {material.VolumeId}",
            $"Nyckelskyddets ID: {Guid.Parse(material.ProtectorId):B}",
            $"Återställningslösenord: {material.RecoveryPassword}",
            $"Skapad (UTC): {DateTimeOffset.UtcNow:O}",
            "",
            "Om enheten inte kan låsas upp: använd återställningslösenordet i Windows BitLocker.",
            "");
        var expected = new UTF8Encoding(false, true).GetBytes(document);

        // Keep the created file exclusively open throughout write and verification.
        // Verify the resolved handle path before writing sensitive key material.
        using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough);
        var actualPath = FinalPath(stream.SafeFileHandle);
        if (!string.Equals(actualPath, fullPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Filens verkliga sökväg ändrades. Ingen återställningsnyckel har skrivits.");
        ValidateDestination(actualPath, parent, material);
        stream.Write(expected);
        stream.Flush(flushToDisk: true);
        stream.Position = 0;
        var actual = new byte[expected.Length];
        stream.ReadExactly(actual);
        if (stream.Length != expected.Length || !actual.AsSpan().SequenceEqual(expected))
            throw new IOException("Återställningsfilen kunde inte verifieras. Kryptering får inte startas.");
        ValidateDestination(actualPath, parent, material);
        return fullPath;
    }

    private static void ValidateMaterial(RecoveryMaterial material)
    {
        if (!DrivePattern.IsMatch(material.MountPoint ?? "") || string.IsNullOrWhiteSpace(material.VolumeId) || material.VolumeId.Any(char.IsControl))
            throw new ArgumentException("Återställningsmaterialet saknar giltig enhets- eller volymidentitet.", nameof(material));
        if (!Guid.TryParse(material.ProtectorId, out var protectorId) || protectorId == Guid.Empty)
            throw new ArgumentException("Återställningsmaterialet saknar ett giltigt nyckelskydds-ID.", nameof(material));
        var recoveryPassword = material.RecoveryPassword ?? "";
        if (!RecoveryPattern.IsMatch(recoveryPassword))
            throw new ArgumentException("Återställningslösenordet måste ha åtta grupper med sex siffror.", nameof(material));
        foreach (var group in recoveryPassword.Split('-'))
        {
            var number = int.Parse(group, NumberStyles.None, CultureInfo.InvariantCulture);
            if (number > 720885 || number % 11 != 0)
                throw new ArgumentException("Återställningslösenordet har en ogiltig siffergrupp.", nameof(material));
        }
    }

    private static void ValidateDestination(string fullPath, string parent, RecoveryMaterial material)
    {
        var root = Path.GetPathRoot(fullPath) ?? "";
        if (!LocalRootPattern.IsMatch(root) || fullPath[2..].Contains(':'))
            throw new ArgumentException("Välj en vanlig fil på en lokal enhet. Nätverk och alternativa dataströmmar stöds inte.");
        if (string.Equals(root[..2], material.MountPoint, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Spara återställningsnyckeln på en annan enhet än den som ska krypteras.");
        var drive = new DriveInfo(root);
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady)
            throw new IOException("Återställningsnyckeln måste sparas på en tillgänglig lokal enhet.");
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException("Välj en befintlig mapp för återställningsfilen.");

        for (DirectoryInfo? directory = new(parent); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Återställningsnyckeln får inte sparas via en symbolisk länk, junction eller annan omdirigerad mapp.");
        if (File.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Återställningsfilen får inte vara en symbolisk länk eller annan omdirigering.");

        var volumePath = new StringBuilder(32768);
        if (!GetVolumePathName(parent, volumePath, (uint)volumePath.Capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Återställningsfilens volym kunde inte identifieras.");
        var volumeName = new StringBuilder(1024);
        if (!GetVolumeNameForVolumeMountPoint(volumePath.ToString(), volumeName, (uint)volumeName.Capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Återställningsfilens volymidentitet kunde inte verifieras.");
        if (string.Equals(volumeName.ToString().TrimEnd('\\'), material.VolumeId.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Återställningsfilens sökväg leder till samma volym som ska krypteras.");
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Återställningsfilens verkliga sökväg kunde inte verifieras.");
        var path = buffer.ToString();
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string fileName, StringBuilder volumePathName, uint bufferLength);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, StringBuilder volumeName, uint bufferLength);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder filePath, uint bufferLength, uint flags);
}
