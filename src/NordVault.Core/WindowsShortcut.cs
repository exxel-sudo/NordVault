using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace NordVault.Core;

public sealed record ShortcutInfo(string Target,string Arguments,string WorkingDirectory);

[SupportedOSPlatform("windows")]
public static class WindowsShortcut
{
    public static ShortcutInfo Read(string path)
    {
        path=FileCryptoPaths.Validate(path);
        object shell=new ShellLink();
        try
        {
            ((IPersistFile)shell).Load(path,0);
            var link=(IShellLinkW)shell;var target=new StringBuilder(1024);var arguments=new StringBuilder(2048);var directory=new StringBuilder(1024);
            link.GetPath(target,target.Capacity,IntPtr.Zero,4); // Raw stored path; do not resolve or launch it.
            link.GetArguments(arguments,arguments.Capacity);link.GetWorkingDirectory(directory,directory.Capacity);
            return new(target.ToString(),arguments.ToString(),directory.ToString());
        }
        finally {Marshal.FinalReleaseComObject(shell);}
    }
    public static void Create(string path,string target,string arguments="",string? description=null)
    {
        path=FileCryptoPaths.Validate(path);target=FileCryptoPaths.Validate(target);
        if(!path.EndsWith(".lnk",StringComparison.OrdinalIgnoreCase)||!File.Exists(target))throw new IOException("Genvägens namn eller mål är ogiltigt.");
        if(File.Exists(path)&&!string.Equals(Read(path).Target,target,StringComparison.OrdinalIgnoreCase))throw new IOException("En annan genväg finns redan på denna plats. Den behålls: "+path);
        var parent=Path.GetDirectoryName(path)!;Directory.CreateDirectory(parent);FileCryptoPaths.AssertNoReparsePoints(parent);
        var temporary=Path.Combine(parent,"nv-"+Guid.NewGuid().ToString("N")+".lnk");
        object shell=new ShellLink();
        try
        {
            var link=(IShellLinkW)shell;link.SetPath(target);link.SetArguments(arguments);link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            link.SetIconLocation(target,0);link.SetShowCmd(1);link.SetDescription(description??"NordVault");
            ((IPersistFile)shell).Save(temporary,true);
            var saved=Read(temporary);
            if(!string.Equals(saved.Target,target,StringComparison.OrdinalIgnoreCase)||saved.Arguments!=arguments)throw new IOException("Windows-genvägen kunde inte verifieras.");
            File.Move(temporary,path,true);
        }
        finally {Marshal.FinalReleaseComObject(shell);if(File.Exists(temporary))File.Delete(temporary);}
    }
    public static bool RemoveIfMatches(string path,string target,string arguments="")
    {
        path=FileCryptoPaths.Validate(path);if(!File.Exists(path))return false;
        var actual=Read(path);
        if(!string.Equals(actual.Target,target,StringComparison.OrdinalIgnoreCase)||actual.Arguments!=arguments)return false;
        File.Delete(path);return true;
    }
    [ComImport,Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink { }
    [ComImport,Guid("000214F9-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out,MarshalAs(UnmanagedType.LPWStr)]StringBuilder path,int count,IntPtr data,uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out,MarshalAs(UnmanagedType.LPWStr)]StringBuilder text,int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)]string text);
        void GetWorkingDirectory([Out,MarshalAs(UnmanagedType.LPWStr)]StringBuilder text,int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)]string text);
        void GetArguments([Out,MarshalAs(UnmanagedType.LPWStr)]StringBuilder text,int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)]string text);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out,MarshalAs(UnmanagedType.LPWStr)]StringBuilder path,int count,out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)]string path,int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)]string path,uint reserved);
        void Resolve(IntPtr window,uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)]string path);
    }
}
