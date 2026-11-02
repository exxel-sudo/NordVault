using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NordVault.Core;

public sealed record DeploymentFile(string Path,long Size,string Sha256);
public sealed record DeploymentManifest(string Product,string Version,DeploymentFile[] Files);
public sealed record InstallationState(string Product,string Version,string Mode,string Folder,string SetupSha256);
public sealed record UninstallResult(int RemovedFiles,int PreservedFiles);

[SupportedOSPlatform("windows")]
public sealed class DeploymentService
{
    private readonly DeploymentManifest manifest;
    public DeploymentService(DeploymentManifest manifest)
    {
        // Validate and snapshot even manifests supplied directly by callers.
        this.manifest=ReadManifest(JsonSerializer.SerializeToUtf8Bytes(manifest));
    }
    public const string ProductId="NordVault.Private.v1";
    public const string StateFile="installation.json";
    private static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true};
    public static DeploymentManifest ReadManifest(ReadOnlySpan<byte> bytes)
    {
        var manifest=JsonSerializer.Deserialize<DeploymentManifest>(bytes)??throw new IOException("Installationsmanifestet saknas.");
        if(manifest.Product!=ProductId||string.IsNullOrWhiteSpace(manifest.Version)||manifest.Version.Length>40||manifest.Files is null||manifest.Files.Length is <1 or >2000) throw new IOException("Installationsmanifestet är ogiltigt.");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
        foreach(var file in manifest.Files)
        {
            if(file is null||string.IsNullOrEmpty(file.Path)||file.Sha256 is null)throw new IOException("Installationsobjektet är ogiltigt.");
            SafeZip.Relative(file.Path);
            if(!names.Add(file.Path)||file.Size is <0 or >300L*1024*1024||file.Sha256.Length!=64||!file.Sha256.All(Uri.IsHexDigit)) throw new IOException("Installationsobjektet är ogiltigt.");
            total=checked(total+file.Size);if(total>1024L*1024*1024) throw new IOException("Installationspaketet är för stort.");
            if(file.Path.Equals(StateFile,StringComparison.OrdinalIgnoreCase)||file.Path.Equals("Data",StringComparison.OrdinalIgnoreCase)||file.Path.StartsWith("Data/",StringComparison.OrdinalIgnoreCase)||file.Path.Equals("NordVault.Setup.exe",StringComparison.OrdinalIgnoreCase)) throw new IOException("Installationspaketet använder en reserverad sökväg.");
        }
        return manifest;
    }
    public static string ValidateDestination(string folder)
    {
        folder=FileCryptoPaths.Validate(folder).TrimEnd('\\');
        if(folder.Length<4||folder.Equals(Path.GetPathRoot(folder)?.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)) throw new IOException("Välj en egen undermapp för NordVault.");
        foreach(var special in new[]{Environment.SpecialFolder.UserProfile,Environment.SpecialFolder.LocalApplicationData,Environment.SpecialFolder.ApplicationData,Environment.SpecialFolder.DesktopDirectory,Environment.SpecialFolder.MyDocuments,Environment.SpecialFolder.ProgramFiles,Environment.SpecialFolder.ProgramFilesX86})
        { var path=Environment.GetFolderPath(special).TrimEnd('\\');if(path.Length>0&&folder.Equals(path,StringComparison.OrdinalIgnoreCase))throw new IOException("Installera i en egen undermapp, inte direkt i en Windows- eller användarmapp."); }
        var windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        if(folder.Equals(windows,StringComparison.OrdinalIgnoreCase)||folder.StartsWith(windows+"\\",StringComparison.OrdinalIgnoreCase))throw new IOException("Windows systemmappar är inte ett installationsmål.");
        return folder;
    }
    public static InstallationState ReadState(string folder)
    {
        folder=ValidateDestination(folder);var path=FileCryptoPaths.Validate(Path.Combine(folder,StateFile));
        if(new FileInfo(path).Length>8192)throw new IOException("Installationsinformationen är för stor.");
        var state=JsonSerializer.Deserialize<InstallationState>(File.ReadAllBytes(path))??throw new IOException("Installationsinformationen saknas.");
        if(state.Product!=ProductId||!folder.Equals(state.Folder,StringComparison.OrdinalIgnoreCase)||state.Mode is not ("User" or "AllUsers" or "Portable")||string.IsNullOrEmpty(state.Version)||state.SetupSha256 is null||state.SetupSha256.Length!=64||!state.SetupSha256.All(Uri.IsHexDigit))throw new IOException("Installationsinformationen är ogiltig.");
        return state;
    }
    public async Task UpdateAsync(Stream payload,string folder,string mode,string setupPath,DeploymentManifest previous,string expectedPreviousSetupHash,CancellationToken cancellation=default)
    {
        folder=ValidateDestination(folder);previous=ReadManifest(JsonSerializer.SerializeToUtf8Bytes(previous));
        using var lease=new DeploymentLease(folder);
        var state=ReadState(folder);
        if(state.Version!=previous.Version||state.Mode!=mode||state.SetupSha256!=expectedPreviousSetupHash)throw new IOException("Den befintliga installationen matchar inte denna uppdatering. Installationsläget måste vara detsamma.");
        var previousFiles=previous.Files.Concat(new[]{new DeploymentFile("NordVault.Setup.exe",new FileInfo(Path.Combine(folder,"NordVault.Setup.exe")).Length,expectedPreviousSetupHash)}).ToArray();
        foreach(var file in previousFiles)
        {
            var path=FileCryptoPaths.Validate(Path.Combine(folder,file.Path.Replace('/',Path.DirectorySeparatorChar)));
            if(!File.Exists(path))continue;
            if(VaultSession.Hash(path)!=file.Sha256)throw new IOException("En programfil har ändrats och behålls. Flytta eller återställ den innan uppdatering: "+file.Path);
            using var held=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None); // Detect running/locked program files before modifying the installation.
        }
        var known=previousFiles.Select(f=>f.Path).Append(StateFile).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var file in manifest.Files)
            if(!known.Contains(file.Path)&&Path.Exists(Path.Combine(folder,file.Path.Replace('/',Path.DirectorySeparatorChar))))throw new IOException("En egen fil finns där uppdateringen behöver skriva. Den behålls: "+file.Path);
        var work=new PrivateWorkspace(Path.GetDirectoryName(folder));var safeCleanup=true;
        var ready=Path.Combine(work.Path,"updated");var backup=Path.Combine(work.Path,"previous");
        var moved=new List<string>();var published=new List<string>();var publishedHashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var newFiles=manifest.Files.Select(f=>f.Path).Append("NordVault.Setup.exe").Append(StateFile).ToArray();
        try
        {
            await InstallAsync(payload,ready,mode,setupPath,cancellation);
            var next=ReadState(ready) with{Folder=folder};File.WriteAllText(Path.Combine(ready,StateFile),JsonSerializer.Serialize(next,JsonOptions));
            foreach(var relative in newFiles)publishedHashes[relative]=VaultSession.Hash(Path.Combine(ready,relative.Replace('/',Path.DirectorySeparatorChar)));
            cancellation.ThrowIfCancellationRequested();
            foreach(var relative in known)
            {
                var original=FileCryptoPaths.Validate(Path.Combine(folder,relative.Replace('/',Path.DirectorySeparatorChar)));if(!File.Exists(original))continue;
                var saved=Path.Combine(backup,relative.Replace('/',Path.DirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(saved)!);File.Move(original,saved,false);moved.Add(relative);
            }
            foreach(var relative in newFiles)
            {
                cancellation.ThrowIfCancellationRequested();var destination=FileCryptoPaths.Validate(Path.Combine(folder,relative.Replace('/',Path.DirectorySeparatorChar)));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Move(Path.Combine(ready,relative.Replace('/',Path.DirectorySeparatorChar)),destination,false);published.Add(relative);
            }
            InheritDestinationAccess(folder);
        }
        catch(Exception originalError)
        {
            try
            {
                foreach(var relative in published.AsEnumerable().Reverse())
                {
                    var destination=FileCryptoPaths.Validate(Path.Combine(folder,relative.Replace('/',Path.DirectorySeparatorChar)));
                    if(File.Exists(destination))
                    {
                        if(VaultSession.Hash(destination)!=publishedHashes[relative])throw new IOException("En ny programfil har ändrats under uppdateringen och behålls.");
                        File.Delete(destination); // Only unchanged files this update just published.
                    }
                }
                foreach(var relative in moved)
                {
                    var destination=FileCryptoPaths.Validate(Path.Combine(folder,relative.Replace('/',Path.DirectorySeparatorChar)));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Move(Path.Combine(backup,relative.Replace('/',Path.DirectorySeparatorChar)),destination,false);
                }
            }
            catch(Exception rollbackError)when(rollbackError is IOException or UnauthorizedAccessException)
            {safeCleanup=false;throw new IOException("Uppdateringen avbröts och alla programfiler kunde inte återställas. Backup finns kvar i: "+backup,new AggregateException(originalError,rollbackError));}
            throw;
        }
        finally {if(safeCleanup)work.Dispose();}
    }
    public async Task InstallAsync(Stream payload,string folder,string mode,string setupPath,CancellationToken cancellation=default)
    {
        folder=ValidateDestination(folder);
        using var lease=new DeploymentLease(folder);
        if(mode is not ("User" or "AllUsers" or "Portable"))throw new IOException("Okänt installationsläge.");
        if(Path.Exists(folder)&&(!Directory.Exists(folder)||Directory.EnumerateFileSystemEntries(folder).Any()))throw new IOException("Välj en tom mapp. Befintlig installation eller egna filer skrivs inte över.");
        var parent=Path.GetDirectoryName(folder)!; Directory.CreateDirectory(parent); FileCryptoPaths.AssertNoReparsePoints(parent);
        using var work=new PrivateWorkspace(parent);var ready=Path.Combine(work.Path,"program");Directory.CreateDirectory(ready);
        using var zip=new ZipArchive(payload,ZipArchiveMode.Read,true);var index=SafeZip.Index(zip,2000);
        if(index.Count!=manifest.Files.Length)throw new IOException("Installationspaketets objekt stämmer inte med manifestet.");
        foreach(var file in manifest.Files)
        {
            cancellation.ThrowIfCancellationRequested();if(!index.TryGetValue(file.Path,out var entry)||entry.Length!=file.Size)throw new IOException("Ett installationsobjekt saknas eller har fel storlek.");
            var destination=Path.Combine(ready,file.Path.Replace('/',Path.DirectorySeparatorChar));FileCryptoPaths.Validate(destination);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using(var input=entry.Open())using(var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {await SafeZip.CopyBoundedAsync(input,output,file.Size,cancellation);await output.FlushAsync(cancellation);output.Flush(true);}
            if(VaultSession.Hash(destination)!=file.Sha256)throw new IOException("Kontrollsumman stämmer inte för ett installationsobjekt.");
        }
        var setup=Path.Combine(ready,"NordVault.Setup.exe");
        setupPath=FileCryptoPaths.Validate(setupPath);File.Copy(setupPath,setup,false);
        var state=new InstallationState(ProductId,manifest.Version,mode,folder,VaultSession.Hash(setup));
        File.WriteAllText(Path.Combine(ready,StateFile),JsonSerializer.Serialize(state,JsonOptions));
        cancellation.ThrowIfCancellationRequested();FileCryptoPaths.AssertNoReparsePoints(folder);
        if(Directory.Exists(folder))Directory.Delete(folder,false); // Empty only; a concurrent file prevents removal.
        Directory.Move(ready,folder);
        InheritDestinationAccess(folder);
    }
    private void InheritDestinationAccess(string folder)
    {
        if(!string.Equals(new DriveInfo(Path.GetPathRoot(folder)!).DriveFormat,"NTFS",StringComparison.OrdinalIgnoreCase))return;
        // Staging is private. After publishing, remove its account-specific inheritance
        // and inherit the selected destination's policy, including Program Files read access.
        var directories=new HashSet<string>(StringComparer.OrdinalIgnoreCase){folder};
        foreach(var file in manifest.Files)
        {
            var directory=Path.GetDirectoryName(Path.Combine(folder,file.Path.Replace('/',Path.DirectorySeparatorChar)))!;
            while(directory.Length>=folder.Length){directories.Add(directory);if(directory==folder)break;directory=Path.GetDirectoryName(directory)!;}
        }
        foreach(var directory in directories.OrderBy(p=>p.Length))
        {
            FileCryptoPaths.AssertNoReparsePoints(directory);var item=new DirectoryInfo(directory);var access=item.GetAccessControl(AccessControlSections.Access);
            ResetAccess(access);item.SetAccessControl(access);
        }
        foreach(var relative in manifest.Files.Select(f=>f.Path).Append("NordVault.Setup.exe").Append(StateFile))
        {
            var path=FileCryptoPaths.Validate(Path.Combine(folder,relative.Replace('/',Path.DirectorySeparatorChar)));var item=new FileInfo(path);var access=item.GetAccessControl(AccessControlSections.Access);
            ResetAccess(access);item.SetAccessControl(access);
        }
    }
    private static void ResetAccess(FileSystemSecurity access)
    {
        access.SetAccessRuleProtection(true,false);
        foreach(FileSystemAccessRule rule in access.GetAccessRules(true,false,typeof(SecurityIdentifier)))access.RemoveAccessRuleSpecific(rule);
        access.SetAccessRuleProtection(false,false);
    }
    public UninstallResult Uninstall(string folder,string setupPath)
    {
        folder=ValidateDestination(folder);var statePath=Path.Combine(folder,StateFile);FileCryptoPaths.Validate(statePath);
        using var lease=new DeploymentLease(folder);
        if(new FileInfo(statePath).Length>8192)throw new IOException("Installationsinformationen är för stor.");
        var state=JsonSerializer.Deserialize<InstallationState>(File.ReadAllBytes(statePath))??throw new IOException("Installationsinformationen saknas.");
        if(state.Product!=ProductId||!folder.Equals(state.Folder,StringComparison.OrdinalIgnoreCase)||state.Version!=manifest.Version||state.Mode is not ("User" or "AllUsers" or "Portable")||state.SetupSha256 is null||state.SetupSha256.Length!=64||!state.SetupSha256.All(Uri.IsHexDigit))throw new IOException("Installationen matchar inte avinstalleraren.");
        var removed=0;var preserved=0;
        foreach(var file in manifest.Files)
        {
            var path=Path.Combine(folder,file.Path.Replace('/',Path.DirectorySeparatorChar));FileCryptoPaths.Validate(path);
            if(!File.Exists(path))continue;
            // Only delete known files that still match the installer's embedded manifest.
            if(VaultSession.Hash(path)!=file.Sha256){preserved++;continue;}
            File.Delete(path);removed++;
        }
        var setup=Path.Combine(folder,"NordVault.Setup.exe");FileCryptoPaths.Validate(setup);
        if(File.Exists(setup)&&VaultSession.Hash(setup)==state.SetupSha256&&VaultSession.Hash(setupPath)==state.SetupSha256){File.Delete(setup);removed++;}
        else if(File.Exists(setup))preserved++;
        File.Delete(statePath);
        // Remove empty known directories only. Never recurse through the user's data.
        var dirs=manifest.Files.Select(f=>Path.GetDirectoryName(Path.Combine(folder,f.Path.Replace('/',Path.DirectorySeparatorChar)))).Where(p=>p is not null&&p!=folder).Distinct().OrderByDescending(p=>p!.Length);
        foreach(var directory in dirs){FileCryptoPaths.AssertNoReparsePoints(directory!);if(Directory.Exists(directory)&&!Directory.EnumerateFileSystemEntries(directory!).Any())Directory.Delete(directory!,false);}
        if(Directory.Exists(folder)&&!Directory.EnumerateFileSystemEntries(folder).Any())Directory.Delete(folder,false);
        return new(removed,preserved);
    }
}
