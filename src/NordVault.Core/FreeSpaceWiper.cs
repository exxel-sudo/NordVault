using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace NordVault.Core;

public sealed record WipeProgress(int Pass, int Passes, long Written, long Total, string Stage);

/// <summary>
/// Overwrites free space on a drive without BitLocker: fills it with files of a pattern, overwrites those files in place for
/// every further pass, then deletes them. Existing files are never opened. Flash and SSD remap cells, so nothing here can
/// promise that old data is physically gone; one pass is as good as seven there.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FreeSpaceWiper
{
    public const string FolderPrefix = "NordVault-rensning-";
    public static readonly int[] AllowedPasses = [1, 3, 7];
    private const int Chunk = 4 * 1024 * 1024;
    private const long FileCap = 1024L * 1024 * 1024; // below FAT32's 4 GiB limit
    private const int TinyFiles = 20000; // reuses free NTFS file records that may hold small deleted files

    /// <summary>1 pass: random. 3: 00, FF, random. 7: DoD 5220.22-M ECE (00, FF, random, random, 00, FF, random).</summary>
    public static int[] Patterns(int passes) => passes switch
    {
        1 => [-1],
        3 => [0x00, 0xFF, -1],
        7 => [0x00, 0xFF, -1, -1, 0x00, 0xFF, -1],
        _ => throw new ArgumentException("Välj 1, 3 eller 7 pass.")
    };

    public static string? Refuse(DriveInfo drive)
    {
        try
        {
            if (!drive.IsReady) return "Disken är inte redo. Är den låst med BitLocker eller utdragen?";
            if (drive.DriveType is not (DriveType.Removable or DriveType.Fixed)) return "Bara lokala diskar och USB-minnen kan rensas, inte nätverks- eller cd-enheter.";
            if (string.Equals(drive.RootDirectory.FullName, Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase))
                return "Windows-disken fylls aldrig helt: Windows kan sluta fungera medan den är full. Använd fliken Rensning i NordVault (BitLocker) för den.";
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return "Disken kunde inte läsas: " + error.Message; }
    }

    public static async Task WipeAsync(string root, int passes, IProgress<WipeProgress>? progress, CancellationToken cancellation)
    {
        var drive = new DriveInfo(root);
        if (Refuse(drive) is { } reason) throw new IOException(reason);
        root = drive.RootDirectory.FullName;
        RemoveLeftovers(root);
        await RunAsync(Path.Combine(root, FolderPrefix + Guid.NewGuid().ToString("N")), passes, long.MaxValue, drive.AvailableFreeSpace, progress, cancellation);
    }

    /// <summary>Earlier runs that were stopped by a crash or a pulled USB stick leave only our own fill files.</summary>
    public static void RemoveLeftovers(string root)
    {
        foreach (var folder in Directory.EnumerateDirectories(root, FolderPrefix + "*"))
        {
            var info = new DirectoryInfo(folder);
            if (Regex.IsMatch(info.Name, "\\A" + FolderPrefix + "[0-9a-f]{32}\\z") && !info.Attributes.HasFlag(FileAttributes.ReparsePoint)) info.Delete(true);
        }
    }

    /// <summary>Fills until the disk is full; <paramref name="limit"/> stops earlier (tests). <paramref name="estimate"/> is only for progress.</summary>
    internal static async Task RunAsync(string folder, int passes, long limit, long estimate, IProgress<WipeProgress>? progress, CancellationToken cancellation)
    {
        var patterns = Patterns(passes);
        Directory.CreateDirectory(folder);
        var files = new List<string>(); var buffer = new byte[Chunk]; long written = 0, total = Math.Max(1, Math.Min(limit, estimate)) * patterns.Length;
        try
        {
            Fill(buffer, patterns[0]);
            // Pass 1 claims all free space; the files then cover exactly the clusters that later passes overwrite.
            var full = false;
            while (!full && written < limit)
            {
                cancellation.ThrowIfCancellationRequested();
                var path = Path.Combine(folder, $"f{files.Count:D6}.bin");
                FileStream stream;
                try { stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 0, FileOptions.WriteThrough); }
                catch (IOException error) when (IsDiskFull(error)) { full = true; break; }
                files.Add(path);
                using (stream)
                {
                    long size = 0; var piece = Chunk;
                    while (size < FileCap && written < limit)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var count = (int)Math.Min(piece, limit - written);
                        try { await stream.WriteAsync(buffer.AsMemory(0, count), cancellation); }
                        catch (IOException error) when (IsDiskFull(error))
                        {
                            if (piece == 512) { full = true; break; }
                            piece = Math.Max(512, piece / 8); continue;
                        }
                        size += count; written += count;
                        if (patterns[0] < 0) RandomNumberGenerator.Fill(buffer);
                        progress?.Report(new(1, patterns.Length, written, total, "Fyller ledigt utrymme"));
                    }
                }
            }
            if (full)
                for (var n = 0; n < TinyFiles; n++)
                {
                    if (n % 500 == 0) { cancellation.ThrowIfCancellationRequested(); progress?.Report(new(1, patterns.Length, written, total, "Fyller lediga filposter")); }
                    var path = Path.Combine(folder, $"s{n:D5}");
                    try
                    {
                        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 0, FileOptions.WriteThrough);
                        files.Add(path); stream.Write(buffer, 0, 512);
                    }
                    catch (IOException error) when (IsDiskFull(error)) { break; }
                }
            var claimed = written;
            for (var pass = 1; pass < patterns.Length; pass++)
            {
                Fill(buffer, patterns[pass]);
                foreach (var path in files)
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 0, FileOptions.WriteThrough);
                    for (long left = stream.Length; left > 0;)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var count = (int)Math.Min(Chunk, left);
                        await stream.WriteAsync(buffer.AsMemory(0, count), cancellation);
                        left -= count; written += count;
                        if (patterns[pass] < 0) RandomNumberGenerator.Fill(buffer);
                        progress?.Report(new(pass + 1, patterns.Length, written, claimed * patterns.Length, "Skriver över"));
                    }
                }
            }
        }
        finally
        {
            foreach (var path in files) try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void Fill(byte[] buffer, int pattern) { if (pattern < 0) RandomNumberGenerator.Fill(buffer); else Array.Fill(buffer, (byte)pattern); }
    // ERROR_HANDLE_DISK_FULL (39) and ERROR_DISK_FULL (112).
    private static bool IsDiskFull(IOException error) => (error.HResult & 0xFFFF) is 39 or 112;
}
