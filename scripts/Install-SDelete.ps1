param([string]$Destination = (Join-Path $PSScriptRoot '..\tools'))
$ErrorActionPreference = 'Stop'
$destinationPath = [IO.Path]::GetFullPath($Destination)
$workspacePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $destinationPath.StartsWith($workspacePath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Destinationen måste ligga i NordVault-mappen.'
}
$null = New-Item -ItemType Directory -Path $destinationPath -Force
$archive = Join-Path $destinationPath ('download-' + [Guid]::NewGuid().ToString('N') + '.zip')
$staged = Join-Path $destinationPath ('sdelete-' + [Guid]::NewGuid().ToString('N') + '.exe')
$installed = Join-Path $destinationPath 'sdelete64.exe'
try {
    Write-Output 'Hämtar SDelete direkt från Microsoft. Licensen accepteras separat i GUI före första filåtgärden.'
    Invoke-WebRequest -Uri 'https://download.sysinternals.com/files/SDelete.zip' -OutFile $archive
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $entry = $zip.GetEntry('sdelete64.exe')
        if ($null -eq $entry) { throw 'Microsoft-arkivet innehåller inte sdelete64.exe.' }
        $inputStream = $entry.Open()
        $outputStream = [IO.File]::Open($staged, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $inputStream.CopyTo($outputStream); $outputStream.Flush($true) }
        finally { $inputStream.Dispose(); $outputStream.Dispose() }
        $eula = $zip.GetEntry('Eula.txt')
        if ($null -ne $eula) {
            $reader = New-Object IO.StreamReader($eula.Open())
            try { [IO.File]::WriteAllText((Join-Path $destinationPath 'SDelete-Eula.txt'), $reader.ReadToEnd()) }
            finally { $reader.Dispose() }
        }
    }
    finally { $zip.Dispose() }
    $signature = Get-AuthenticodeSignature -LiteralPath $staged
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -ne 'Microsoft Corporation') {
        throw 'Signaturen kunde inte verifieras som giltig Microsoft-signatur. Ingen installation gjordes.'
    }
    Move-Item -LiteralPath $staged -Destination $installed -Force
    Get-FileHash -LiteralPath $installed -Algorithm SHA256
    Write-Output 'Klart. SDelete är installerat, men har inte körts.'
}
finally {
    foreach ($temporaryFile in @($archive, $staged)) {
        if ([IO.Path]::GetDirectoryName($temporaryFile) -eq $destinationPath -and (Test-Path -LiteralPath $temporaryFile)) { Remove-Item -LiteralPath $temporaryFile -Force }
    }
}
