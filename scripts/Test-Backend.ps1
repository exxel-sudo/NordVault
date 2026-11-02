# Isolated protocol/policy tests. No BitLocker cmdlet or external disk tool is allowed to run.
# Each scenario uses a fresh Windows PowerShell 5.1 process, with all OS-facing calls mocked.
[CmdletBinding()]
param([switch] $Child, [string] $Scenario)

$ErrorActionPreference = 'Stop'
$backendPath = Join-Path $PSScriptRoot 'BitLockerBackend.ps1'

if ($Child) {
    $script:Calls = New-Object 'System.Collections.Generic.List[string]'
    $script:VolumeReads = 0
    $script:Scenario = $Scenario
    $script:EnableArguments = $null
    $script:ReadCalls = @{ Partition = 0; Disk = 0; Physical = 0; BitLocker = 0; Tpm = 0 }
    $script:Protectors = @([pscustomobject] @{
        KeyProtectorType = 'RecoveryPassword'; KeyProtectorId = '{44444444-4444-4444-4444-444444444444}'
        RecoveryPassword = '000011-000022-000033-000044-000055-000066-000077-000088'
    })
    $request = @{
        Action = 'EnableEncryption'; MountPoint = 'Z:'; VolumeId = 'mock-volume-z'
        ProtectorId = '{44444444-4444-4444-4444-444444444444}'; Password = 'test-only-long-password'
        Pin = '123456'; BackupVerified = $true; Confirmed = $true; EulaAccepted = $true
    }
    switch ($Scenario) {
        'WrongIdentity' { $request.VolumeId = 'wrong-volume' }
        'StaleIdentity' { }
        'BackupUnverified' { $request.BackupVerified = $false }
        'NoConfirmation' { $request.Confirmed = $false }
        'SsdShredDenied' { $request.Action = 'ShredFile'; $request.FilePath = $PSCommandPath; $request.MountPoint = [IO.Path]::GetPathRoot($PSCommandPath).Substring(0, 2).ToUpperInvariant() }
        'MetacharactersDenied' { $request.Action = 'ValidateBackup'; $request.FilePath = 'Z:\unsafe*.txt' }
        'FullVolumeEncryption' { }
        'RecoveryPreservedOnFailure' { }
        'RecoveryPreparationPreservesExisting' { $request.Action = 'PrepareRecovery' }
        'SystemFullEncryption' { }
        'InvalidWmiStateDenied' { }
        'MissingWmiFieldsDenied' { }
        'WmiErrorDenied' { }
        'LockedDiscovery' { $request.Action = 'Discover' }
        'UnprivilegedDiscovery' { $request.Action = 'Discover' }
        'AmbiguousDiscoveryDisk' { $request.Action = 'Discover' }
        'FailedDiscoveryInventory' { $request.Action = 'Discover' }
        'LockedUnlock' { $request.Action = 'Unlock' }
        'SsdLogicalWipe' { $request.Action = 'WipeFreeSpace' }
        'MalformedMountDenied' { $request.MountPoint = 'Z:;bad' }
        default { throw "Unknown test scenario: $Scenario" }
    }
    $script:TestMountPoint = $request.MountPoint
    $script:MockRequestJson = ConvertTo-Json -InputObject $request -Compress
    $mockSource = @'
function Test-Administrator { return $script:Scenario -notin @('UnprivilegedDiscovery','AmbiguousDiscoveryDisk','FailedDiscoveryInventory') }
function Test-BitLockerAvailable { return $true }
function Get-Volume {
    param($DriveLetter, $FilePath, $ErrorAction)
    $script:VolumeReads++
    $id = 'mock-volume-z'
    if ($script:Scenario -eq 'StaleIdentity' -and $script:VolumeReads -ge 2) { $id = 'swapped-volume-z' }
    $value = [pscustomobject] @{ DriveLetter = $script:TestMountPoint.Substring(0, 1); UniqueId = $id; Path = $id; DriveType = 'Fixed'
        FileSystemLabel = 'MOCK ONLY'; FileSystem = 'NTFS'; Size = [long] 1048576; SizeRemaining = [long] 524288 }
    if ($script:Scenario -eq 'UnprivilegedDiscovery') {
        return @($value,[pscustomobject] @{ DriveLetter = 'Y'; UniqueId = 'mock-volume-y'; Path = 'mock-volume-y'; DriveType = 'Fixed'; FileSystemLabel = 'MOCK TWO'; FileSystem = 'NTFS'; Size = [long] 1048576; SizeRemaining = [long] 524288 })
    }
    return $value
}
function Get-Partition {
    param($DriveLetter, $ErrorAction); $script:ReadCalls.Partition++
    $value = [pscustomobject] @{ DiskNumber = 991; DriveLetter = $script:TestMountPoint.Substring(0, 1) }
    if ($script:Scenario -eq 'UnprivilegedDiscovery') { return @($value,[pscustomobject] @{ DiskNumber = 991; DriveLetter = 'Y' }) }
    if ($script:Scenario -eq 'AmbiguousDiscoveryDisk') { return @($value,$value) }
    return $value
}
function Get-Disk {
    param($Number, $ErrorAction); $script:ReadCalls.Disk++
    if ($script:Scenario -eq 'FailedDiscoveryInventory') { throw 'Test-only storage inventory failure.' }
    return [pscustomobject] @{ Number = 991; UniqueId = 'mock-disk'; BusType = 'SATA'; IsOffline = $false; IsReadOnly = $false }
}
function Get-PhysicalDisk {
    param($ErrorAction)
    $script:ReadCalls.Physical++
    $media = 'HDD'; if ($script:Scenario -in @('SsdShredDenied','SsdLogicalWipe')) { $media = 'SSD' }
    return [pscustomobject] @{ UniqueId = 'mock-disk'; MediaType = $media }
}
function Get-BitLockerVolume {
    param($MountPoint, $ErrorAction)
    $script:ReadCalls.BitLocker++
    $value = [pscustomobject] @{ MountPoint = 'Z:'; VolumeType = 'FixedData'; VolumeStatus = 'FullyDecrypted'
        ProtectionStatus = 'Off'; LockStatus = 'Unlocked'; EncryptionMethod = 'None'; EncryptionPercentage = 0
        KeyProtector = $script:Protectors }
    if ($script:Scenario -eq 'SystemFullEncryption') { $value.VolumeType = 'OperatingSystem' }
    if ($script:Scenario -in @('LockedDiscovery','LockedUnlock')) { $value.LockStatus = 'Locked' }
    if ($script:Scenario -eq 'SsdLogicalWipe') { $value.VolumeStatus = 'FullyEncrypted'; $value.ProtectionStatus = 'On' }
    return $value
}
function Get-CimInstance { param($Namespace, $ClassName, $Filter, $ErrorAction); return [pscustomobject] @{ DriveLetter = $script:TestMountPoint } }
function Invoke-CimMethod {
    param($InputObject, $MethodName, $Arguments, $ErrorAction)
    if ($script:Scenario -eq 'SsdLogicalWipe' -and $MethodName -eq 'Encrypt') {
        $script:Calls.Add('Encrypt')
        $script:EnableArguments = $Arguments
        return [pscustomobject] @{ ReturnValue = [uint32] 0 }
    }
    if ($MethodName -ne 'GetConversionStatus') { throw 'An unexpected mutating CIM call was attempted in a mock test.' }
    if ($script:Scenario -eq 'MissingWmiFieldsDenied') { return [pscustomobject] @{ ReturnValue = [uint32] 0; ConversionStatus = 0 } }
    $value = [pscustomobject] @{ ReturnValue = [uint32] 0; ConversionStatus = 0; EncryptionPercentage = 0
        WipingStatus = 0; WipingPercentage = 0 }
    if ($script:Scenario -in @('LockedDiscovery','LockedUnlock')) { $value.ReturnValue = [uint32] 2150694912 }
    if ($script:Scenario -eq 'WmiErrorDenied') { $value.ReturnValue = [uint32] 5 }
    if ($script:Scenario -eq 'InvalidWmiStateDenied') { $value.ConversionStatus = 99; $value.EncryptionPercentage = 100 }
    if ($script:Scenario -eq 'SsdLogicalWipe') { $value.ConversionStatus = 1; $value.EncryptionPercentage = 100 }
    return $value
}
function Get-Tpm { param($ErrorAction); $script:ReadCalls.Tpm++; return [pscustomobject] @{ TpmPresent = $true; TpmReady = $true } }
function Enable-BitLocker {
    param($MountPoint, $EncryptionMethod, [switch] $PasswordProtector, $Password, [switch] $TpmAndPinProtector,
        $Pin, [switch] $UsedSpaceOnly, [switch] $HardwareEncryption, [switch] $SkipHardwareTest, $ErrorAction)
    $script:Calls.Add('Enable-BitLocker')
    $script:EnableArguments = @{ EncryptionMethod = [string] $EncryptionMethod; UsedSpaceOnly = [bool] $UsedSpaceOnly
        HardwareEncryption = [bool] $HardwareEncryption; SkipHardwareTest = [bool] $SkipHardwareTest
        PasswordProtector = [bool] $PasswordProtector; TpmAndPinProtector = [bool] $TpmAndPinProtector }
    if ($script:Scenario -eq 'RecoveryPreservedOnFailure') { throw 'Injected engine failure.' }
}
function Add-BitLockerKeyProtector { $script:Calls.Add('Add-BitLockerKeyProtector'); throw 'Unexpected protector addition in this mock scenario.' }
function Remove-BitLockerKeyProtector { $script:Calls.Add('Remove-BitLockerKeyProtector'); throw 'Unexpected protector removal in this mock scenario.' }
function Unlock-BitLocker {
    param($MountPoint, $Password, $RecoveryPassword, $ErrorAction)
    $script:Calls.Add('Unlock-BitLocker')
    if ($script:Scenario -ne 'LockedUnlock') { throw 'Unexpected unlock in this mock scenario.' }
}
function Lock-BitLocker { $script:Calls.Add('Lock-BitLocker'); throw 'Unexpected lock in this mock scenario.' }
function Resume-BitLocker { $script:Calls.Add('Resume-BitLocker'); throw 'Unexpected resume in this mock scenario.' }
function Disable-BitLocker { $script:Calls.Add('Disable-BitLocker'); throw 'Unexpected decryption in this mock scenario.' }
function Invoke-VettedProcess { $script:Calls.Add('ExternalProcess'); throw 'External processes are forbidden in mock tests.' }
[Console]::SetIn([IO.StringReader]::new($script:MockRequestJson))
[Console]::SetOut($script:MockOutputWriter)
'@
    # Inject mocks after the backend function definitions, before its JSON entry point.
    # The production file itself is never edited. Refuse to proceed if that entry point moves.
    $source = [IO.File]::ReadAllText($backendPath)
    $marker = [regex]::Match($source, '(?m)^try \{\r?\n\s+\$json = \[Console\]::In\.ReadToEnd\(\)')
    if (-not $marker.Success) { throw 'Cannot identify the backend entry point safely.' }
    $combined = $source.Substring(0, $marker.Index) + $mockSource + [Environment]::NewLine + $source.Substring($marker.Index)
    $writer = New-Object IO.StringWriter
    $script:MockOutputWriter = $writer
    $originalOut = [Console]::Out; $originalIn = [Console]::In
    try {
        . ([scriptblock]::Create($combined))
    } finally { [Console]::SetOut($originalOut); [Console]::SetIn($originalIn) }
    $envelope = ConvertFrom-Json -InputObject $writer.ToString()
    $expectedSuccess = $Scenario -in @('FullVolumeEncryption', 'RecoveryPreparationPreservesExisting', 'SystemFullEncryption', 'LockedDiscovery', 'LockedUnlock', 'SsdLogicalWipe','UnprivilegedDiscovery','AmbiguousDiscoveryDisk','FailedDiscoveryInventory')
    if ([bool] $envelope.Ok -ne $expectedSuccess) { throw "Unexpected result in $Scenario`: $($envelope.Message)" }
    $expectedMessage = switch ($Scenario) {
        'WrongIdentity' { 'Den valda disken har bytts ut.' }
        'StaleIdentity' { 'Den valda disken har bytts ut.' }
        'BackupUnverified' { 'Verifiera s' }
        'NoConfirmation' { 'bekräftelse' }
        'SsdShredDenied' { 'HDD' }
        'MetacharactersDenied' { 'utan jokertecken' }
        'RecoveryPreservedOnFailure' { 'Windows kunde inte slutföra åtgärden' }
        default { '' }
    }
    if ($expectedMessage -and -not $envelope.Message.Contains($expectedMessage)) { throw "Wrong rejection reason in $Scenario`: $($envelope.Message)" }
    if ($Scenario -in @('WrongIdentity', 'StaleIdentity', 'BackupUnverified', 'NoConfirmation', 'SsdShredDenied', 'MetacharactersDenied', 'RecoveryPreparationPreservesExisting') -and $script:Calls.Count -ne 0) {
        throw "Mutation reached before rejection in $Scenario"
    }
    if ($Scenario -eq 'FullVolumeEncryption') {
        if ($script:Calls.Count -ne 1 -or $script:Calls[0] -ne 'Enable-BitLocker' -or
            $script:EnableArguments.EncryptionMethod -ne 'XtsAes256' -or $script:EnableArguments.UsedSpaceOnly -or
            $script:EnableArguments.HardwareEncryption -or $script:EnableArguments.SkipHardwareTest -or
            -not $script:EnableArguments.PasswordProtector) { throw 'Full-volume software encryption arguments changed.' }
    }
    if ($Scenario -eq 'RecoveryPreservedOnFailure' -and
        ($script:Calls.Count -ne 1 -or $script:Calls[0] -ne 'Enable-BitLocker' -or $script:Protectors.Count -ne 1)) {
        throw 'An encryption failure changed the existing recovery protector.'
    }
    if ($Scenario -eq 'SystemFullEncryption' -and
        ($script:Calls.Count -ne 1 -or -not $script:EnableArguments.TpmAndPinProtector -or $script:EnableArguments.SkipHardwareTest -or $script:EnableArguments.UsedSpaceOnly -or $script:EnableArguments.HardwareEncryption)) {
        throw 'System encryption no longer preserves the full-volume TPM+PIN hardware test.'
    }
    if ($Scenario -eq 'LockedDiscovery' -and
        ($envelope.Data.Volumes.Count -ne 1 -or $envelope.Data.Volumes[0].LockStatus -ne 'Locked' -or $envelope.Data.Volumes[0].ProtectionStatus -ne 'Unknown')) { throw 'Locked volume was hidden or fabricated as protected.' }
    if ($Scenario -eq 'LockedUnlock' -and ($script:Calls.Count -ne 1 -or $script:Calls[0] -ne 'Unlock-BitLocker')) { throw 'Locked-volume unlock did not reach the correct mocked cmdlet.' }
    if ($Scenario -in @('UnprivilegedDiscovery','AmbiguousDiscoveryDisk','FailedDiscoveryInventory')) {
        if ($script:Calls.Count -ne 0 -or $script:ReadCalls.BitLocker -ne 0 -or $script:ReadCalls.Tpm -ne 0 -or
            $script:ReadCalls.Partition -ne 1 -or $script:ReadCalls.Disk -ne 1 -or $script:ReadCalls.Physical -ne 1) { throw 'Discovery repeated inventory queries or attempted privileged/mutating calls.' }
        if ($envelope.Data.IsAdministrator -or $envelope.Data.TpmReady -or $envelope.Data.Volumes[0].VolumeStatus -ne 'Unknown' -or $envelope.Data.Volumes[0].WipingStatus -ne -1) { throw 'Unprivileged discovery fabricated security status.' }
        if ($Scenario -ne 'UnprivilegedDiscovery' -and $envelope.Data.Volumes[0].MediaKind -ne 'Unknown') { throw 'Ambiguous/failed inventory fabricated an HDD.' }
        if ($Scenario -eq 'UnprivilegedDiscovery' -and ($envelope.Data.Volumes.Count -ne 2 -or @($envelope.Data.Volumes | Where-Object { $_.VolumeStatus -ne 'Unknown' -or $_.MediaKind -ne 'HDD' }).Count -ne 0)) { throw 'Batched inventory lost or misidentified a test volume.' }
    }
    if ($Scenario -eq 'SsdLogicalWipe' -and
        ($script:Calls.Count -ne 1 -or $script:Calls[0] -ne 'Encrypt' -or $script:EnableArguments.EncryptionFlags -ne 2 -or $script:EnableArguments.EncryptionMethod -ne 0)) { throw 'Logical free-space wipe used incorrect Windows flags.' }
    if ($Scenario -in @('InvalidWmiStateDenied','MissingWmiFieldsDenied','WmiErrorDenied','MalformedMountDenied') -and $script:Calls.Count -ne 0) { throw 'Invalid state reached a mutation.' }
    if ($Scenario -ne 'RecoveryPreparationPreservesExisting' -and $writer.ToString().Contains('test-only-long-password')) { throw 'A password leaked into the response.' }
    Write-Output (ConvertTo-Json -InputObject @{ Passed = $true; Scenario = $Scenario; MutationCalls = $script:Calls.Count } -Compress)
    exit 0
}

$powerShellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$scenarios = @('WrongIdentity', 'StaleIdentity', 'BackupUnverified', 'NoConfirmation', 'SsdShredDenied',
    'MetacharactersDenied', 'FullVolumeEncryption', 'RecoveryPreservedOnFailure', 'RecoveryPreparationPreservesExisting',
    'SystemFullEncryption','InvalidWmiStateDenied','MissingWmiFieldsDenied','WmiErrorDenied','LockedDiscovery','LockedUnlock','SsdLogicalWipe','MalformedMountDenied',
    'UnprivilegedDiscovery','AmbiguousDiscoveryDisk','FailedDiscoveryInventory')
foreach ($testCase in $scenarios) {
    $result = & $powerShellPath -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $PSCommandPath -Child -Scenario $testCase
    if ($LASTEXITCODE -ne 0) { throw "Backend mock test failed: $testCase" }
    $parsed = ConvertFrom-Json -InputObject ($result -join [Environment]::NewLine)
    if (-not $parsed.Passed) { throw "Backend mock test did not pass: $testCase" }
    Write-Output ("PASS: " + $testCase)
}
Write-Output ("Completed {0} isolated backend mock tests. No real disk was changed." -f $scenarios.Count)
