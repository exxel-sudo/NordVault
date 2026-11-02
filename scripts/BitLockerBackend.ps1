# Windows PowerShell 5.1 backend. stdin/stdout are a single UTF-8 JSON protocol.
# Passwords and recovery material must never be written to diagnostics.
$ErrorActionPreference = 'Stop'
$WarningPreference = 'SilentlyContinue'
$VerbosePreference = 'SilentlyContinue'
$DebugPreference = 'SilentlyContinue'
$ProgressPreference = 'SilentlyContinue'
[Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$script:SafeFailure = $null
$script:Request = $null
$script:DiscoveryStorage = $null

function Stop-Safely([string] $Message) {
    $script:SafeFailure = $Message
    throw [InvalidOperationException]::new('NordVault request rejected.')
}

function Read-RequestString([string] $Name) {
    $property = $script:Request.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return '' }
    if ($property.Value -isnot [string]) { Stop-Safely 'Begäran innehåller ett fält med fel datatyp.' }
    return $property.Value
}

function Request-IsTrue([string] $Name) {
    $property = $script:Request.PSObject.Properties[$Name]
    return ($null -ne $property -and $property.Value -is [bool] -and $property.Value)
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } finally { $identity.Dispose() }
}

function Test-BitLockerAvailable {
    return ($null -ne (Get-Command Get-BitLockerVolume -ErrorAction SilentlyContinue) -and
        $null -ne (Get-Command Enable-BitLocker -ErrorAction SilentlyContinue))
}

function Get-SimpleDisk([string] $MountPoint) {
    try {
        if ($null -ne $script:DiscoveryStorage) {
            $partitions = @($script:DiscoveryStorage.Partitions | Where-Object {
                [string]::Equals([string] $_.DriveLetter, $MountPoint.Substring(0, 1), [StringComparison]::OrdinalIgnoreCase)
            })
        } else { $partitions = @(Get-Partition -DriveLetter $MountPoint[0] -ErrorAction Stop) }
        if ($partitions.Count -ne 1) { return $null }
        if ($null -ne $script:DiscoveryStorage) {
            $disks = @($script:DiscoveryStorage.Disks | Where-Object { $_.Number -eq $partitions[0].DiskNumber })
        } else { $disks = @(Get-Disk -Number $partitions[0].DiskNumber -ErrorAction Stop) }
        if ($disks.Count -ne 1) { return $null }
        return $disks[0]
    } catch { return $null }
}

function Get-MediaDescription([string] $MountPoint) {
    $result = @{ MediaKind = 'Unknown'; BusType = 'Unknown' }
    $disk = Get-SimpleDisk $MountPoint
    if ($null -eq $disk) { return $result }
    $result.BusType = [string] $disk.BusType
    # Disk.Number and PhysicalDisk.DeviceId are not interchangeable identifiers.
    # Only a unique matching storage identity can establish an HDD.
    if ([string]::IsNullOrWhiteSpace([string] $disk.UniqueId)) { return $result }
    if ($result.BusType -in @('RAID', 'Spaces', 'Virtual', 'File Backed Virtual', 'iSCSI', 'Unknown')) {
        return $result
    }
    try {
        $physicalDisks = if ($null -ne $script:DiscoveryStorage) { $script:DiscoveryStorage.PhysicalDisks } else { @(Get-PhysicalDisk -ErrorAction Stop) }
        $matches = @($physicalDisks | Where-Object {
            -not [string]::IsNullOrWhiteSpace([string] $_.UniqueId) -and
            [string]::Equals(([string] $_.UniqueId).Trim(), ([string] $disk.UniqueId).Trim(),
                [StringComparison]::OrdinalIgnoreCase)
        })
        if ($matches.Count -eq 1) {
            switch ([string] $matches[0].MediaType) {
                'HDD' { $result.MediaKind = 'HDD' }
                '3' { $result.MediaKind = 'HDD' }
                'SSD' { $result.MediaKind = 'SSD' }
                '4' { $result.MediaKind = 'SSD' }
                'SCM' { $result.MediaKind = 'SSD' }
                '5' { $result.MediaKind = 'SSD' }
            }
        }
    } catch { }
    # NVMe is flash media, but a USB or SATA bus alone does not identify an HDD.
    if ($result.MediaKind -eq 'Unknown' -and $result.BusType -eq 'NVMe') { $result.MediaKind = 'SSD' }
    return $result
}

function Get-VolumeIdentity($Volume) {
    $id = [string] $Volume.UniqueId
    if ([string]::IsNullOrWhiteSpace($id)) { $id = [string] $Volume.Path }
    return $id
}

function Get-WmiBitLocker($MountPoint) {
    $volumes = @(Get-CimInstance -Namespace 'Root/CIMV2/Security/MicrosoftVolumeEncryption' `
        -ClassName Win32_EncryptableVolume -Filter "DriveLetter='$MountPoint'" -ErrorAction Stop)
    if ($volumes.Count -ne 1) { Stop-Safely 'Windows kunde inte identifiera en entydig BitLocker-volym.' }
    return $volumes[0]
}

function Invoke-CheckedVolumeMethod($Volume, [string] $Method, [hashtable] $Arguments = @{}) {
    $result = Invoke-CimMethod -InputObject $Volume -MethodName $Method -Arguments $Arguments -ErrorAction Stop
    if ($null -eq $result -or $null -eq $result.ReturnValue -or [uint32] $result.ReturnValue -ne 0) {
        Stop-Safely 'Windows nekade den begärda BitLocker-åtgärden.'
    }
    return $result
}

function Get-VolumeState($Volume, [bool] $BitLockerAvailable) {
    $mount = ([string] $Volume.DriveLetter).ToUpperInvariant() + ':'
    $media = Get-MediaDescription $mount
    $state = [ordered] @{
        MountPoint = $mount
        VolumeId = Get-VolumeIdentity $Volume
        Label = [string] $Volume.FileSystemLabel
        FileSystem = [string] $Volume.FileSystem
        DriveType = [string] $Volume.DriveType
        VolumeType = 'Data'
        VolumeStatus = 'Unknown'
        ProtectionStatus = 'Unknown'
        LockStatus = 'Unknown'
        EncryptionMethod = 'Unknown'
        EncryptionPercentage = 0
        MediaKind = $media.MediaKind
        BusType = $media.BusType
        SizeBytes = [long] $Volume.Size
        FreeBytes = [long] $Volume.SizeRemaining
        WipingStatus = -1
        WipingPercentage = 0
    }
    if ($mount -eq $env:SystemDrive) { $state.VolumeType = 'OperatingSystem' }
    if ($BitLockerAvailable) {
        try {
            $bitLocker = Get-BitLockerVolume -MountPoint $mount -ErrorAction Stop
            $state.VolumeType = [string] $bitLocker.VolumeType
            $state.VolumeStatus = [string] $bitLocker.VolumeStatus
            $state.ProtectionStatus = [string] $bitLocker.ProtectionStatus
            $state.LockStatus = [string] $bitLocker.LockStatus
            $state.EncryptionMethod = [string] $bitLocker.EncryptionMethod
            $state.EncryptionPercentage = [int] $bitLocker.EncryptionPercentage
            # Read current method results rather than stale cached CIM properties.
            $wmi = Get-WmiBitLocker $mount
            $conversion = Invoke-CheckedVolumeMethod $wmi 'GetConversionStatus' @{ PrecisionFactor = [uint32] 0 }
            foreach ($field in @('ConversionStatus', 'EncryptionPercentage', 'WipingStatus', 'WipingPercentage')) {
                if ($null -eq $conversion.PSObject.Properties[$field] -or $null -eq $conversion.$field) {
                    Stop-Safely 'Windows kunde inte ge en fullständig aktuell volymstatus.'
                }
            }
            $conversionNames = @('FullyDecrypted', 'FullyEncrypted', 'EncryptionInProgress',
                'DecryptionInProgress', 'EncryptionPaused', 'DecryptionPaused')
            $index = [int] $conversion.ConversionStatus
            if ($index -lt 0 -or $index -ge $conversionNames.Length -or
                [int] $conversion.WipingStatus -notin @(0, 1, 2, 3) -or
                [int] $conversion.EncryptionPercentage -lt 0 -or [int] $conversion.EncryptionPercentage -gt 100 -or
                [int] $conversion.WipingPercentage -lt 0 -or [int] $conversion.WipingPercentage -gt 100) {
                Stop-Safely 'Windows gav en oväntad volymstatus. Uppdatera disklistan.'
            }
            if ($index -ge 0 -and $index -lt $conversionNames.Length) {
                $state.VolumeStatus = $conversionNames[$index]
            }
            $state.EncryptionPercentage = [int] $conversion.EncryptionPercentage
            $state.WipingStatus = [int] $conversion.WipingStatus
            $state.WipingPercentage = [int] $conversion.WipingPercentage
        } catch {
            # Partial read access must never fabricate a protected or idle state.
            $state.VolumeStatus = 'Unknown'
            $state.ProtectionStatus = 'Unknown'
            # GetConversionStatus legitimately fails for a locked volume. Preserve
            # the independently read lock state so a locked USB can still be unlocked.
            $state.LockStatus = if ($null -ne $bitLocker -and [string] $bitLocker.LockStatus -eq 'Locked') { 'Locked' } else { 'Unknown' }
            $state.EncryptionMethod = 'Unknown'
            $state.EncryptionPercentage = 0
            $state.WipingStatus = -1
            $script:SafeFailure = $null
        }
    }
    return [pscustomobject] $state
}

function Get-Discovery {
    $administrator = Test-Administrator
    $available = Test-BitLockerAvailable
    $tpmReady = $false
    try {
        if ($administrator -and $null -ne (Get-Command Get-Tpm -ErrorAction SilentlyContinue)) {
            $tpm = Get-Tpm -ErrorAction Stop
            $tpmReady = [bool] $tpm.TpmPresent -and [bool] $tpm.TpmReady
        }
    } catch { }
    # Inventory each storage class once per read-only request. Never reuse these
    # snapshots for a mutation; every action starts a new process and verifies its target.
    $script:DiscoveryStorage = @{ Partitions = @(); Disks = @(); PhysicalDisks = @() }
    try { $script:DiscoveryStorage.Partitions = @(Get-Partition -ErrorAction Stop) } catch { }
    try { $script:DiscoveryStorage.Disks = @(Get-Disk -ErrorAction Stop) } catch { }
    try { $script:DiscoveryStorage.PhysicalDisks = @(Get-PhysicalDisk -ErrorAction Stop) } catch { }
    $states = @()
    try {
        $volumes = @(Get-Volume -ErrorAction Stop | Where-Object {
            $null -ne $_.DriveLetter -and ([string] $_.DriveLetter) -cmatch '^[A-Za-z]$' -and
            ([string] $_.DriveType) -in @('Fixed', 'Removable')
        } | Sort-Object DriveLetter)
        foreach ($volume in $volumes) {
            $identity = Get-VolumeIdentity $volume
            if (-not [string]::IsNullOrWhiteSpace($identity)) {
                $states += Get-VolumeState $volume ($available -and $administrator)
            }
        }
    } catch { }
    if ($states.Count -eq 0) {
        # Storage CIM may require elevation. Native read-only inventory still
        # shows real disks, with all security properties left explicitly unknown.
        try {
            if (-not ('NordVault.VolumeIdentity' -as [type])) {
                $null = Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
namespace NordVault {
    public static class VolumeIdentity {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumeNameForVolumeMountPointW(string root, StringBuilder name, uint size);
    }
}
'@ -ErrorAction Stop
            }
            foreach ($drive in [IO.DriveInfo]::GetDrives()) {
                if ([string] $drive.DriveType -notin @('Fixed', 'Removable')) { continue }
                $buffer = New-Object Text.StringBuilder(64)
                if (-not [NordVault.VolumeIdentity]::GetVolumeNameForVolumeMountPointW($drive.Name, $buffer, 64)) {
                    continue
                }
                $volume = [pscustomobject] @{
                    DriveLetter = $drive.Name.Substring(0, 1); UniqueId = $buffer.ToString()
                    Path = $buffer.ToString(); FileSystemLabel = ''; FileSystem = ''
                    DriveType = [string] $drive.DriveType; Size = [long] 0; SizeRemaining = [long] 0
                }
                try {
                    if ($drive.IsReady) {
                        $volume.FileSystemLabel = $drive.VolumeLabel
                        $volume.FileSystem = $drive.DriveFormat
                        $volume.Size = $drive.TotalSize
                        $volume.SizeRemaining = $drive.AvailableFreeSpace
                    }
                } catch { }
                $states += Get-VolumeState $volume ($available -and $administrator)
            }
        } catch { }
    }
    $message = 'Redo.'
    if (-not $available) { $message = 'BitLocker-hantering är inte tillgänglig i denna Windows-installation.' }
    elseif (-not $administrator) { $message = 'Starta om NordVault som administratör för att hantera kryptering och filradering.' }
    return [pscustomobject] @{
        IsAdministrator = $administrator; BitLockerAvailable = $available
        TpmReady = $tpmReady; Message = $message; Volumes = @($states)
    }
}

function Get-VerifiedContext {
    $mount = Read-RequestString 'MountPoint'
    $expectedId = Read-RequestString 'VolumeId'
    if ($mount -cnotmatch '^[A-Z]:$' -or [string]::IsNullOrWhiteSpace($expectedId) -or $expectedId.Length -gt 2048) {
        Stop-Safely 'Välj en giltig volym med dess aktuella identitet.'
    }
    $volumes = @(Get-Volume -DriveLetter $mount[0] -ErrorAction Stop)
    if ($volumes.Count -ne 1 -or ([string] $volumes[0].DriveType) -notin @('Fixed', 'Removable')) {
        Stop-Safely 'Den valda lokala volymen är inte längre tillgänglig.'
    }
    $id = Get-VolumeIdentity $volumes[0]
    if ([string]::IsNullOrWhiteSpace($id) -or -not [string]::Equals($id, $expectedId, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Safely 'Den valda disken har bytts ut. Uppdatera disklistan och välj den igen.'
    }
    return [pscustomobject] @{ Volume = $volumes[0]; MountPoint = $mount; VolumeId = $id }
}

function Get-VerifiedBitLockerContext {
    if (-not (Test-BitLockerAvailable)) { Stop-Safely 'BitLocker-hantering är inte tillgänglig.' }
    $context = Get-VerifiedContext
    $disk = Get-SimpleDisk $context.MountPoint
    if ($null -eq $disk -or $disk.IsOffline -or $disk.IsReadOnly) {
        Stop-Safely 'Den valda disken är otillgänglig eller kan inte identifieras säkert för skrivning.'
    }
    $bitLocker = Get-BitLockerVolume -MountPoint $context.MountPoint -ErrorAction Stop
    $state = Get-VolumeState $context.Volume $true
    return [pscustomobject] @{ Context = $context; BitLocker = $bitLocker; State = $state; Disk = $disk }
}

function Assert-Decrypted($Context) {
    if ($Context.State.VolumeStatus -ne 'FullyDecrypted' -or $Context.State.ProtectionStatus -ne 'Off' -or
        $Context.State.LockStatus -ne 'Unlocked') {
        Stop-Safely 'Krypteringen kräver en upplåst och helt okrypterad volym med skyddet avstängt.'
    }
    if ($Context.State.FileSystem -notin @('NTFS', 'FAT', 'FAT16', 'FAT32', 'exFAT')) {
        Stop-Safely 'Det valda filsystemet stöds inte för krypteringen.'
    }
    if ($Context.State.WipingStatus -notin @(0, 1)) { Stop-Safely 'Vänta tills disken är ledig och dess status kan verifieras.' }
}

function Get-RecoveryProtectors($BitLocker) {
    return @($BitLocker.KeyProtector | Where-Object { [string] $_.KeyProtectorType -eq 'RecoveryPassword' })
}

function Assert-PlainPassword([string] $Password) {
    if ([string]::IsNullOrWhiteSpace($Password) -or $Password.Length -lt 14 -or $Password.Length -gt 256 -or $Password -match '[\p{Cc}\p{Cf}]') {
        Stop-Safely 'Använd ett lösenord med 14 till 256 tecken utan kontrolltecken.'
    }
}

function Assert-NoReparseAncestors([string] $Path) {
    $current = $Path
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if ([IO.File]::Exists($current) -or [IO.Directory]::Exists($current)) {
            $attributes = [IO.File]::GetAttributes($current)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                Stop-Safely 'Symboliska länkar, kataloglänkar och andra omdirigerade sökvägar tillåts inte.'
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current.TrimEnd('\'))
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $current) { break }
        $current = $parent
    }
}

function Get-CanonicalLocalPath([string] $Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path.Length -gt 240 -or
        $Path -cnotmatch '^[A-Za-z]:\\' -or $Path.Substring(2).Contains(':') -or
        $Path -match '[*?\[\]<>|"\p{Cc}\p{Cf}]' -or $Path.Contains('/') -or
        $Path -match '(?:^|\\)\.{1,2}(?:\\|$)' -or $Path -match '[. ](?:\\|$)' -or
        $Path -match '(?:^|\\)(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.[^\\]*)?(?:\\|$)') {
        Stop-Safely 'Använd en vanlig absolut lokal sökväg utan jokertecken, dataströmmar eller enhetsprefix.'
    }
    return [IO.Path]::GetFullPath($Path)
}

function Assert-SafeBackupPath($Context, [string] $Path) {
    $path = Get-CanonicalLocalPath $Path
    if ([IO.Directory]::Exists($path)) { Stop-Safely 'Välj en filsökväg för säkerhetskopian av återställningsnyckeln.' }
    $directory = [IO.Path]::GetDirectoryName($path)
    if (-not [IO.Directory]::Exists($directory)) { Stop-Safely 'Katalogen för säkerhetskopian måste redan finnas.' }
    Assert-NoReparseAncestors $path
    $targetMount = [IO.Path]::GetPathRoot($path).Substring(0, 2).ToUpperInvariant()
    if ($targetMount -eq $Context.MountPoint) { Stop-Safely 'Spara säkerhetskopian på en annan fysisk disk.' }
    $sourceDisk = Get-SimpleDisk $Context.MountPoint
    $targetDisk = Get-SimpleDisk $targetMount
    if ($null -eq $sourceDisk -or $null -eq $targetDisk -or
        [string] $sourceDisk.BusType -in @('RAID', 'Spaces', 'Virtual', 'File Backed Virtual', 'iSCSI', 'Unknown') -or
        [string] $targetDisk.BusType -in @('RAID', 'Spaces', 'Virtual', 'File Backed Virtual', 'iSCSI', 'Unknown')) {
        Stop-Safely 'Det går inte att verifiera att säkerhetskopian ligger på en annan fysisk disk.'
    }
    if ([int] $sourceDisk.Number -eq [int] $targetDisk.Number) {
        Stop-Safely 'En annan partition på samma disk är ingen säker plats för återställningsnyckeln.'
    }
    $targetVolumes = @(Get-Volume -DriveLetter $targetMount[0] -ErrorAction Stop)
    if ($targetVolumes.Count -ne 1 -or ([string] $targetVolumes[0].DriveType) -notin @('Fixed', 'Removable') -or
        $targetDisk.IsOffline -or $targetDisk.IsReadOnly) {
        Stop-Safely 'Måldisken för säkerhetskopian är inte en skrivbar lokal disk.'
    }
    # A mounted volume directory could otherwise disguise a destination on the source disk.
    $actualVolumes = @(Get-Volume -FilePath $directory -ErrorAction Stop)
    if ($actualVolumes.Count -ne 1 -or
        -not [string]::Equals((Get-VolumeIdentity $actualVolumes[0]), (Get-VolumeIdentity $targetVolumes[0]),
            [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Safely 'Säkerhetskopians mål kan inte kopplas entydigt till en volym.'
    }
    return $path
}

function Invoke-VettedProcess([string] $Executable, [string] $Arguments) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $start.Arguments = $Arguments
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { Stop-Safely 'Det betrodda diskverktyget kunde inte startas.' }
        # Read both streams asynchronously to prevent pipe deadlock. Never forward tool output.
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $null = $outputTask.GetAwaiter().GetResult()
        $null = $errorTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { Stop-Safely 'Det betrodda diskverktyget rapporterade att åtgärden misslyckades.' }
    } finally { $process.Dispose() }
}

function Assert-SafeShredTarget($Context, [string] $Path) {
    $path = Get-CanonicalLocalPath $Path
    if (-not [IO.File]::Exists($path) -or [IO.Directory]::Exists($path)) {
        Stop-Safely 'Välj en befintlig vanlig fil för säker radering.'
    }
    if ([IO.Path]::GetPathRoot($path).Substring(0, 2).ToUpperInvariant() -ne $Context.Context.MountPoint) {
        Stop-Safely 'Filen måste ligga på den valda disken.'
    }
    if ($Context.State.MediaKind -ne 'HDD' -or $Context.State.FileSystem -ne 'NTFS' -or
        $Context.State.LockStatus -ne 'Unlocked') {
        Stop-Safely 'Säker filradering är tillgänglig endast på en upplåst, bekräftad HDD med NTFS.'
    }
    if ($Context.State.VolumeStatus -notin @('FullyDecrypted', 'FullyEncrypted') -or
        $Context.State.WipingStatus -notin @(0, 1)) {
        Stop-Safely 'Säker filradering kräver en ledig disk med verifierad status.'
    }
    Assert-NoReparseAncestors $path
    $programRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
    $blockedRoots = @($env:SystemRoot, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData, $programRoot)
    foreach ($root in $blockedRoots) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        $root = [IO.Path]::GetFullPath($root).TrimEnd('\')
        if ($path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or
            [string]::Equals($path, $root, [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Safely 'Systemkataloger och NordVault-installationen undantas från säker filradering.'
        }
    }
    if ($path -match '\\(?:System Volume Information|\$Recycle\.Bin|Recovery|Boot|EFI)(?:\\|$)' -or
        [IO.Path]::GetFileName($path) -match '^(?:pagefile\.sys|hiberfil\.sys|swapfile\.sys|bootmgr|ntldr)$') {
        Stop-Safely 'Systemfiler och återställningsfiler undantas från säker filradering.'
    }
    if (-not ('NordVault.FileIdentity' -as [type])) {
        $null = Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace NordVault {
    public static class FileIdentity {
        [StructLayout(LayoutKind.Sequential)]
        public struct FileInfo {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
            public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfo info);
    }
}
'@ -ErrorAction Stop
    }
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $info = New-Object NordVault.FileIdentity+FileInfo
        if (-not [NordVault.FileIdentity]::GetFileInformationByHandle($stream.SafeFileHandle, [ref] $info) -or
            $info.Links -ne 1 -or ($info.Attributes -band 0x400) -ne 0) {
            Stop-Safely 'Filer med flera hårda länkar eller omdirigeringar får inte raderas säkert.'
        }
    } finally { $stream.Dispose() }
    return $path
}

try {
    $json = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($json) -or $json.Length -gt 1048576) {
        Stop-Safely 'Begäran är tom eller överskrider den tillåtna storleken.'
    }
    $script:Request = ConvertFrom-Json -InputObject $json -ErrorAction Stop
    if ($null -eq $script:Request -or $script:Request -is [array] -or $script:Request -is [string]) {
        Stop-Safely 'Begäran måste vara ett JSON-objekt.'
    }
    $action = Read-RequestString 'Action'
    $actions = @('Discover', 'ValidateBackup', 'PrepareRecovery', 'EnableEncryption', 'Unlock', 'Lock',
        'WipeFreeSpace', 'CancelWipe', 'ResumeProtection', 'ShredFile')
    if ($actions -cnotcontains $action) { Stop-Safely 'Den begärda åtgärden stöds inte.' }
    if ($action -notin @('Discover', 'ValidateBackup')) {
        if (-not (Test-Administrator)) { Stop-Safely 'Starta om NordVault som administratör innan du ändrar en disk.' }
        if (-not (Request-IsTrue 'Confirmed')) { Stop-Safely 'Uttrycklig bekräftelse krävs innan en disk ändras.' }
    }
    $data = $null
    $message = 'Klart.'
    switch -Exact ($action) {
        'Discover' { $data = Get-Discovery; $message = $data.Message }
        'ValidateBackup' {
            $context = Get-VerifiedContext
            $null = Assert-SafeBackupPath $context (Read-RequestString 'FilePath')
            $message = 'Säkerhetskopians mål ligger på en annan fysisk disk.'
            $data = @{ Message = $message }
        }
        'PrepareRecovery' {
            $context = Get-VerifiedBitLockerContext
            Assert-Decrypted $context
            $protectors = @(Get-RecoveryProtectors $context.BitLocker)
            if ($protectors.Count -eq 0) {
                $null = Get-VerifiedContext
                $null = Add-BitLockerKeyProtector -MountPoint $context.Context.MountPoint `
                    -RecoveryPasswordProtector -ErrorAction Stop
                $null = Get-VerifiedContext
                $bitLocker = Get-BitLockerVolume -MountPoint $context.Context.MountPoint -ErrorAction Stop
                $protectors = @(Get-RecoveryProtectors $bitLocker)
            }
            if ($protectors.Count -eq 0 -or [string]::IsNullOrWhiteSpace([string] $protectors[0].RecoveryPassword)) {
                Stop-Safely 'Windows kunde inte lämna ut återställningsinformationen.'
            }
            $data = @{
                MountPoint = $context.Context.MountPoint; VolumeId = $context.Context.VolumeId
                ProtectorId = [string] $protectors[0].KeyProtectorId
                RecoveryPassword = [string] $protectors[0].RecoveryPassword
            }
            $message = 'Återställningsnyckeln är förberedd. Spara och verifiera den innan krypteringen startas.'
        }
        'EnableEncryption' {
            $context = Get-VerifiedBitLockerContext
            Assert-Decrypted $context
            if (-not (Request-IsTrue 'BackupVerified')) { Stop-Safely 'Verifiera säkerhetskopian av återställningsnyckeln innan krypteringen startas.' }
            $protectorId = Read-RequestString 'ProtectorId'
            $protectors = @(Get-RecoveryProtectors $context.BitLocker | Where-Object {
                [string]::Equals([string] $_.KeyProtectorId, $protectorId, [StringComparison]::OrdinalIgnoreCase)
            })
            if ([string]::IsNullOrWhiteSpace($protectorId) -or $protectors.Count -ne 1) {
                Stop-Safely 'Den verifierade återställningsnyckeln finns inte längre på denna disk.'
            }
            $secret = $null
            try {
                if ($context.State.VolumeType -eq 'OperatingSystem') {
                    $pin = Read-RequestString 'Pin'
                    if ($pin -cnotmatch '^[0-9]{6,20}$') { Stop-Safely 'Startkoden måste innehålla 6 till 20 siffror.' }
                    $tpm = Get-Tpm -ErrorAction Stop
                    if (-not $tpm.TpmPresent -or -not $tpm.TpmReady) { Stop-Safely 'Systemkrypteringen kräver en tillgänglig och förberedd TPM.' }
                    $secret = ConvertTo-SecureString -String $pin -AsPlainText -Force
                    $null = Get-VerifiedContext
                    # Do not skip the OS boot hardware test, use hardware encryption or used-space-only mode.
                    $null = Enable-BitLocker -MountPoint $context.Context.MountPoint -EncryptionMethod XtsAes256 `
                        -TpmAndPinProtector -Pin $secret -ErrorAction Stop
                    $message = 'Systemkrypteringen är förberedd. Följ Windows anvisningar om omstart och maskinvarutest.'
                } else {
                    $password = Read-RequestString 'Password'
                    Assert-PlainPassword $password
                    $secret = ConvertTo-SecureString -String $password -AsPlainText -Force
                    $null = Get-VerifiedContext
                    $null = Enable-BitLocker -MountPoint $context.Context.MountPoint -EncryptionMethod XtsAes256 `
                        -PasswordProtector -Password $secret -ErrorAction Stop
                    $message = 'Windows har accepterat kryptering av hela volymen. Uppdatera för att följa förloppet.'
                }
            } finally { if ($null -ne $secret) { $secret.Dispose() }; $password = $null; $pin = $null }
            $data = @{ Message = $message }
        }
        'Unlock' {
            $context = Get-VerifiedBitLockerContext
            if ($context.State.LockStatus -ne 'Locked' -or $context.State.VolumeType -eq 'OperatingSystem') {
                Stop-Safely 'Välj en låst datavolym för upplåsning.'
            }
            $password = Read-RequestString 'Password'
            if ($password -cmatch '^[0-9]{6}(?:-[0-9]{6}){7}$') {
                $null = Get-VerifiedContext
                $null = Unlock-BitLocker -MountPoint $context.Context.MountPoint -RecoveryPassword $password -ErrorAction Stop
            } else {
                if ($password.Length -lt 1 -or $password.Length -gt 256 -or $password -match '[\p{Cc}\p{Cf}]') {
                    Stop-Safely 'Ange volymens lösenord eller en återställningsnyckel med 48 siffror och bindestreck.'
                }
                $secret = ConvertTo-SecureString -String $password -AsPlainText -Force
                try {
                    $null = Get-VerifiedContext
                    $null = Unlock-BitLocker -MountPoint $context.Context.MountPoint -Password $secret -ErrorAction Stop
                } finally { $secret.Dispose() }
            }
            $password = $null
            $message = 'Datavolymen är upplåst.'; $data = @{ Message = $message }
        }
        'Lock' {
            $context = Get-VerifiedBitLockerContext
            if ($context.State.VolumeType -eq 'OperatingSystem' -or $context.State.LockStatus -ne 'Unlocked' -or
                $context.State.VolumeStatus -ne 'FullyEncrypted' -or $context.State.WipingStatus -notin @(0, 1)) {
                Stop-Safely 'Endast en ledig, helt krypterad datavolym får låsas.'
            }
            $null = Get-VerifiedContext
            # Refuse busy disks instead of forcibly dismounting files that applications are using.
            $null = Lock-BitLocker -MountPoint $context.Context.MountPoint -ErrorAction Stop
            $message = 'Datavolymen är låst.'; $data = @{ Message = $message }
        }
        'WipeFreeSpace' {
            $context = Get-VerifiedBitLockerContext
            if ($context.State.VolumeStatus -ne 'FullyEncrypted' -or
                $context.State.ProtectionStatus -ne 'On' -or $context.State.LockStatus -ne 'Unlocked' -or
                $context.State.WipingStatus -notin @(0, 1)) {
                Stop-Safely 'Rensning av ledigt utrymme kräver en skyddad, upplåst och ledig volym.'
            }
            $wmi = Get-WmiBitLocker $context.Context.MountPoint
            $null = Get-VerifiedContext
            $null = Invoke-CheckedVolumeMethod $wmi 'Encrypt' @{ EncryptionMethod = [uint32] 0; EncryptionFlags = [uint32] 2 }
            $message = 'Windows har startat logisk rensning av ledigt utrymme. Den garanterar inte radering av omfördelade SSD- eller flashceller.'
            $data = @{ Message = $message }
        }
        'CancelWipe' {
            $context = Get-VerifiedBitLockerContext
            if ($context.State.WipingStatus -notin @(2, 3)) { Stop-Safely 'Det finns ingen pågående rensning att avbryta.' }
            $null = Get-VerifiedContext
            $tool = Join-Path $env:SystemRoot 'System32\manage-bde.exe'
            Invoke-VettedProcess $tool ('-wipefreespace -cancel ' + $context.Context.MountPoint)
            $message = 'Windows har accepterat avbrottet av rensningen.'; $data = @{ Message = $message }
        }
        'ResumeProtection' {
            $context = Get-VerifiedBitLockerContext
            if ($context.State.VolumeStatus -ne 'FullyEncrypted' -or $context.State.ProtectionStatus -ne 'Off' -or
                $context.State.LockStatus -ne 'Unlocked' -or @($context.BitLocker.KeyProtector).Count -eq 0) {
                Stop-Safely 'Skyddet får aktiveras endast på en helt krypterad volym med befintliga nyckelskydd.'
            }
            $null = Get-VerifiedContext
            $null = Resume-BitLocker -MountPoint $context.Context.MountPoint -ErrorAction Stop
            $message = 'Windows har accepterat aktivering av skyddet.'; $data = @{ Message = $message }
        }
        'ShredFile' {
            if (-not (Request-IsTrue 'EulaAccepted')) { Stop-Safely 'Godkänn Microsofts SDelete-licens innan säker filradering används.' }
            $context = Get-VerifiedBitLockerContext
            $path = Assert-SafeShredTarget $context (Read-RequestString 'FilePath')
            $expectedTool = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $PSScriptRoot) 'tools\sdelete64.exe'))
            $tool = Get-CanonicalLocalPath (Read-RequestString 'ToolPath')
            if (-not [string]::Equals($tool, $expectedTool, [StringComparison]::OrdinalIgnoreCase) -or
                -not [IO.File]::Exists($tool)) {
                Stop-Safely 'Installera Microsoft SDelete på den angivna sökvägen för NordVault-verktyg.'
            }
            Assert-NoReparseAncestors $tool
            $signature = Get-AuthenticodeSignature -LiteralPath $tool -ErrorAction Stop
            if ([string] $signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
                $signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Microsoft Corporation(?:,|$)') {
                Stop-Safely 'SDelete måste ha en giltig Authenticode-signatur från Microsoft.'
            }
            $null = Get-VerifiedContext
            $null = Assert-SafeShredTarget $context $path
            # A validated Windows filename cannot contain a quote; no shell is involved.
            Invoke-VettedProcess $tool ('-accepteula -p 1 -f "' + $path + '"')
            if ([IO.File]::Exists($path)) { Stop-Safely 'SDelete tog inte bort den valda filen.' }
            $message = 'SDelete har slutfört filåtgärden. Säkerhetskopior, ögonblicksbilder och andra kopior finns kvar.'
            $data = @{ Message = $message }
        }
    }
    $envelope = @{ Ok = $true; Message = $message; Data = $data }
} catch {
    $message = $script:SafeFailure
    if ([string]::IsNullOrWhiteSpace($message)) {
        $message = 'Windows kunde inte slutföra åtgärden. Kontrollera diskens tillgänglighet, behörigheter och BitLocker-policy.'
    }
    $envelope = @{ Ok = $false; Message = $message; Data = $null }
} finally {
    # Managed strings cannot be guaranteed erased; keep their lifetime and exposure minimal.
    $json = $null
    if ($null -ne $script:Request) {
        foreach ($name in @('Password', 'Pin')) {
            $property = $script:Request.PSObject.Properties[$name]
            if ($null -ne $property) { $property.Value = $null }
        }
    }
    $script:Request = $null
}
$response = ConvertTo-Json -InputObject $envelope -Depth 8 -Compress
[Console]::WriteLine($response)
