using System.Runtime.Versioning;
using Microsoft.Win32;

namespace NordVault.Core;

/// <summary>
/// Right-click menu "NordVault" in Explorer, for the current user only. Windows 11 shows it under "Visa fler alternativ"
/// (Shift+right-click); the short menu needs a signed app package.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ExplorerMenu
{
    public const string ArchiveArgument = "--archive", WipeArgument = "--wipe-free";
    private static readonly (string Type, string Verb, string Text, string Argument)[] Verbs =
    [
        ("*", "archive", "Skapa självuppackande arkiv…", ArchiveArgument),
        ("Directory", "archive", "Skapa självuppackande arkiv…", ArchiveArgument),
        ("Drive", "wipe", "Rensa ledigt utrymme…", WipeArgument),
    ];
    private static string KeyOf(string type) => @"Software\Classes\" + type + @"\shell\NordVault";
    // A quoted drive root ("E:\") would end in \" and lose its backslash, so drives pass %1 unquoted (it has no spaces).
    private static string Command(string exe, string type, string argument) => $"\"{exe}\" {argument} " + (type == "Drive" ? "%1" : "\"%1\"");

    /// <summary>The program each menu entry starts, or null when an entry is missing.</summary>
    public static string? RegisteredProgram()
    {
        string? program = null;
        foreach (var (type, verb, _, _) in Verbs)
        {
            using var command = Registry.CurrentUser.OpenSubKey(KeyOf(type) + @"\shell\" + verb + @"\command");
            if (command?.GetValue("") is not string line || !line.StartsWith('"')) return null;
            var end = line.IndexOf('"', 1); if (end < 2) return null;
            var exe = line[1..end];
            if (program is not null && !string.Equals(program, exe, StringComparison.OrdinalIgnoreCase)) return null;
            program = exe;
        }
        return program;
    }

    /// <summary>Adds the menu unless it already points at an existing NordVault (an installed copy wins over a portable one).</summary>
    public static void Ensure(string exe)
    {
        var current = RegisteredProgram();
        if (current is null || !File.Exists(current)) Register(exe);
    }

    public static void Register(string exe)
    {
        Unregister();
        foreach (var (type, verb, text, argument) in Verbs)
        {
            using var root = Registry.CurrentUser.CreateSubKey(KeyOf(type));
            root.SetValue("MUIVerb", "NordVault"); root.SetValue("Icon", exe + ",0"); root.SetValue("SubCommands", ""); root.SetValue("MultiSelectModel", "Single");
            using var entry = root.CreateSubKey(@"shell\" + verb);
            entry.SetValue("MUIVerb", text); entry.SetValue("Icon", exe + ",0");
            using var command = entry.CreateSubKey("command");
            command.SetValue("", Command(exe, type, argument));
        }
    }

    /// <summary>Removes the menu; with <paramref name="folder"/> only when it starts a program in that folder.</summary>
    public static void Unregister(string? folder = null)
    {
        if (folder is not null && RegisteredProgram() is { } program &&
            !program.StartsWith(Path.TrimEndingDirectorySeparator(folder) + "\\", StringComparison.OrdinalIgnoreCase)) return;
        foreach (var type in Verbs.Select(v => v.Type).Distinct()) Registry.CurrentUser.DeleteSubKeyTree(KeyOf(type), false);
    }
}
