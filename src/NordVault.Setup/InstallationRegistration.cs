using System.IO;
using Microsoft.Win32;
using NordVault.Core;

namespace NordVault.Setup;

internal static class InstallationRegistration
{
    private const string RegistryPath=@"Software\Microsoft\Windows\CurrentVersion\Uninstall\NordVault.Private";
    public static void Register(string folder,string mode,bool desktop,bool startMenu)
    {
        if(mode=="Portable")return;
        using(var hive=RegistryKey.OpenBaseKey(mode=="AllUsers"?RegistryHive.LocalMachine:RegistryHive.CurrentUser,RegistryView.Registry64))
        using(var key=hive.CreateSubKey(RegistryPath))
        {
            key.SetValue("DisplayName","NordVault");key.SetValue("DisplayVersion",PackageResources.Version);key.SetValue("Publisher","NordVault");key.SetValue("InstallLocation",folder);
            key.SetValue("DisplayIcon",Path.Combine(folder,"NordVault.exe")+",0");key.SetValue("UninstallString","\""+Path.Combine(folder,"NordVault.Setup.exe")+"\" --uninstall \""+folder+"\"");
            key.SetValue("ModifyPath","\""+Path.Combine(folder,"NordVault.Setup.exe")+"\" --manage \""+folder+"\"");key.SetValue("NoModify",0);key.SetValue("NoRepair",1);
            key.SetValue("EstimatedSize",checked((int)((PackageResources.Manifest().Files.Sum(f=>f.Size)+new FileInfo(Path.Combine(folder,"NordVault.Setup.exe")).Length)/1024)));
        }
        var app=Path.Combine(folder,"NordVault.exe");var setup=Path.Combine(folder,"NordVault.Setup.exe");
        var programs=Environment.GetFolderPath(mode=="AllUsers"?Environment.SpecialFolder.CommonPrograms:Environment.SpecialFolder.Programs);
        var menu=Path.Combine(programs,"NordVault");
        var desktopFolder=Environment.GetFolderPath(mode=="AllUsers"?Environment.SpecialFolder.CommonDesktopDirectory:Environment.SpecialFolder.DesktopDirectory);
        if(startMenu)
        {
            WindowsShortcut.Create(Path.Combine(menu,"NordVault.lnk"),app,description:"NordVault — diskar, USB och filer");
            WindowsShortcut.Create(Path.Combine(menu,"Avinstallera NordVault.lnk"),setup,"--uninstall \""+folder+"\"", "Avinstallera NordVault — dina egna filer behålls");
        }
        else
        {WindowsShortcut.RemoveIfMatches(Path.Combine(menu,"NordVault.lnk"),app);WindowsShortcut.RemoveIfMatches(Path.Combine(menu,"Avinstallera NordVault.lnk"),setup,"--uninstall \""+folder+"\"");}
        if(desktop)WindowsShortcut.Create(Path.Combine(desktopFolder,"NordVault.lnk"),app);
        else WindowsShortcut.RemoveIfMatches(Path.Combine(desktopFolder,"NordVault.lnk"),app);
        WindowsShortcut.RemoveIfMatches(Path.Combine(programs,"NordVault.lnk"),app); // Replace the verified 0.3.0 flat menu link.
        NordVault.Core.ExplorerMenu.Register(app); // right-click menu for the installing user
    }
    public static void Unregister(string folder,string mode)
    {
        NordVault.Core.ExplorerMenu.Unregister(folder);
        if(mode=="Portable")return;
        using(var hive=RegistryKey.OpenBaseKey(mode=="AllUsers"?RegistryHive.LocalMachine:RegistryHive.CurrentUser,RegistryView.Registry64))
        {
            using(var key=hive.OpenSubKey(RegistryPath))
                if(string.Equals(key?.GetValue("InstallLocation") as string,folder,StringComparison.OrdinalIgnoreCase))hive.DeleteSubKeyTree(RegistryPath,false);
        }
        var app=Path.Combine(folder,"NordVault.exe");var setup=Path.Combine(folder,"NordVault.Setup.exe");
        var programs=Environment.GetFolderPath(mode=="AllUsers"?Environment.SpecialFolder.CommonPrograms:Environment.SpecialFolder.Programs);var menu=Path.Combine(programs,"NordVault");
        var desktop=Environment.GetFolderPath(mode=="AllUsers"?Environment.SpecialFolder.CommonDesktopDirectory:Environment.SpecialFolder.DesktopDirectory);
        WindowsShortcut.RemoveIfMatches(Path.Combine(menu,"NordVault.lnk"),app);
        WindowsShortcut.RemoveIfMatches(Path.Combine(menu,"Avinstallera NordVault.lnk"),setup,"--uninstall \""+folder+"\"");
        WindowsShortcut.RemoveIfMatches(Path.Combine(programs,"NordVault.lnk"),app);WindowsShortcut.RemoveIfMatches(Path.Combine(desktop,"NordVault.lnk"),app);
        if(Directory.Exists(menu)&&!Directory.EnumerateFileSystemEntries(menu).Any())Directory.Delete(menu,false);
    }
}
