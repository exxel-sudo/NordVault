using System.Diagnostics;
using System.IO;

namespace NordVault.Setup;

/// <summary>
/// NordVault must not run while its files are replaced or removed. That includes the hidden NordVault.exe that Firefox
/// starts for the browser extension: it only relays messages, holds no vault data and Firefox starts it again when needed.
/// </summary>
internal static class RunningApp
{
    public sealed record Found(Process[] Windows, Process[] Relays, bool Elevated);

    public static Found Find(string folder)
    {
        var target = Path.GetFullPath(Path.Combine(folder, "NordVault.exe"));
        List<Process> windows = [], relays = []; var elevated = false;
        foreach (var process in Process.GetProcessesByName("NordVault"))
        {
            if (process.Id == Environment.ProcessId) { process.Dispose(); continue; }
            try
            {
                if (!string.Equals(Path.GetFullPath(process.MainModule!.FileName), target, StringComparison.OrdinalIgnoreCase)) { process.Dispose(); continue; }
                (process.MainWindowHandle != IntPtr.Zero ? windows : relays).Add(process);
            }
            catch (InvalidOperationException) { process.Dispose(); } // exited while being inspected
            catch (System.ComponentModel.Win32Exception)
            { elevated |= !HasExited(process); process.Dispose(); } // an administrator's NordVault: cannot be inspected or closed from here
        }
        return new([.. windows], [.. relays], elevated);
    }

    /// <summary>Closes NordVault's window normally (the vault locks) and ends Firefox relays. Returns an error message, or null when nothing runs any more.</summary>
    public static async Task<string?> CloseAsync(string folder)
    {
        var found = Find(folder);
        try
        {
            foreach (var window in found.Windows) window.CloseMainWindow();
            foreach (var relay in found.Relays) { try { relay.Kill(); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
            var all = found.Windows.Concat(found.Relays).ToArray();
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (all.Any(p => !HasExited(p)) && DateTime.UtcNow < deadline) await Task.Delay(250);
            if (found.Windows.Any(p => !HasExited(p)))
                return "NordVault kunde inte stängas. Om en åtgärd pågår: vänta eller avbryt den, stäng NordVault själv och försök igen.";
            if (found.Relays.Any(p => !HasExited(p)))
                return "NordVault-kopplingen till Firefox kunde inte stängas. Stäng Firefox och försök igen.";
            return Find(folder) is { Elevated: true } or { Windows.Length: > 0 } or { Relays.Length: > 0 }
                ? "NordVault körs fortfarande, kanske som administratör. Stäng alla NordVault-fönster och Firefox och försök igen."
                : null;
        }
        finally { foreach (var process in found.Windows.Concat(found.Relays)) process.Dispose(); }
    }
    private static bool HasExited(Process process) { try { return process.HasExited; } catch (InvalidOperationException) { return true; } }
}
