param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\tools\gnupg'),
    [string]$PackagePath = '',
    [string]$SevenZipPath = (Join-Path $env:ProgramFiles '7-Zip\7z.exe')
)
$ErrorActionPreference = 'Stop'
$workspacePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$destinationPath = [IO.Path]::GetFullPath($Destination)
$workspacePrefix = $workspacePath.TrimEnd('\') + '\'
function Assert-LocalPath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installationen måste ligga i NordVault-mappen.' }
    $current = $resolved
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Omdirigerade mappar tillåts inte vid installation.' }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}
function Assert-Runtime([string]$Path) {
    $manifestPath = Join-Path $workspacePath 'src\NordVault.Core\GnuPgManifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($entry in $manifest.PSObject.Properties) {
        $file = Join-Path $Path $entry.Name
        Assert-LocalPath $file
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Value) { throw ('GnuPG-komponenten har fel kontrollsumma: ' + $entry.Name) }
    }
    $binFiles = @(Get-ChildItem -LiteralPath (Join-Path $Path 'bin') -Force)
    if ($binFiles.Count -ne @($manifest.PSObject.Properties).Count) { throw 'GnuPG-katalogen innehåller oväntade komponenter.' }
    foreach ($file in $binFiles) {
        if ($file.PSIsContainer -or -not ($manifest.PSObject.Properties.Name -contains ('bin\' + $file.Name))) { throw 'En okänd komponent finns i GnuPG-katalogen.' }
    }
}
Assert-LocalPath $destinationPath
if (Test-Path -LiteralPath $destinationPath) {
    Assert-Runtime $destinationPath
    Write-Output 'Verifierad GnuPG 2.5.24 finns redan lokalt. Inga filer ändrades.'
    return
}
if (-not (Test-Path -LiteralPath $SevenZipPath -PathType Leaf)) { throw '7-Zip behövs för säker uppackning. Ange -SevenZipPath om det ligger på annan plats.' }
$toolsPath = Join-Path $workspacePath 'tools'
Assert-LocalPath $toolsPath
$null = New-Item -ItemType Directory -Path $toolsPath -Force
$staging = Join-Path $toolsPath ('gnupg-stage-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $staging
try {
    if ([string]::IsNullOrWhiteSpace($PackagePath)) {
        $PackagePath = Join-Path $staging 'gpg4win-5.1.1.exe'
        Write-Output 'Hämtar det signerade Gpg4win-paketet från tillverkaren. Installationsprogrammet körs inte.'
        Invoke-WebRequest -Uri 'https://files.gpg4win.org/gpg4win-5.1.1.exe' -OutFile $PackagePath
    }
    $PackagePath = [IO.Path]::GetFullPath($PackagePath)
    if ((Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash -ne 'ED19C0C89EC42FE32C52A7ABD6E3DA5F491752700546B9EED7DDF82FD303CDDF') { throw 'Gpg4win-paketets SHA-256 stämmer inte med tillverkarens publicerade värde.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $PackagePath
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne '83CC4E382E5E4AF554C66E429E8F66FFE499910D') { throw 'Gpg4win-paketets signatur eller signeringscertifikat kunde inte verifieras.' }
    $nestedPath = Join-Path $staging 'nested'
    $runtimePath = Join-Path $staging 'runtime'
    $null = New-Item -ItemType Directory -Path $nestedPath
    $null = New-Item -ItemType Directory -Path $runtimePath
    # The outer archive contains parent-directory paths. Extract only two exact entries,
    # flattening their paths. Never extract all entries from the outer NSIS package.
    & $SevenZipPath e -y ('-o' + $nestedPath) $PackagePath '$TEMP\gnupg-w32-2.5.24_20260923-bin.exe' 'pkg-licenses.txt' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Gpg4win-paketet kunde inte packas upp.' }
    $nestedPackage = Join-Path $nestedPath 'gnupg-w32-2.5.24_20260923-bin.exe'
    if ((Get-FileHash -LiteralPath $nestedPackage -Algorithm SHA256).Hash -ne 'EA6F9DEE2CCCD83287432625B8B399173805ADD81D27067FCAF019C0C4DA1AD8') { throw 'Det inre GnuPG-paketet har fel kontrollsumma.' }
    $manifest = Get-Content -LiteralPath (Join-Path $workspacePath 'src\NordVault.Core\GnuPgManifest.json') -Raw | ConvertFrom-Json
    $entries = @($manifest.PSObject.Properties.Name) + @('share\gnupg\*')
    & $SevenZipPath x -y ('-o' + $runtimePath) $nestedPackage @entries | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'GnuPG-komponenterna kunde inte packas upp.' }
    Copy-Item -LiteralPath (Join-Path $nestedPath 'pkg-licenses.txt') -Destination (Join-Path $runtimePath 'LICENSES.txt')
    [IO.File]::WriteAllText((Join-Path $runtimePath 'ORIGIN.txt'), "GnuPG 2.5.24 from Gpg4win 5.1.1`r`nhttps://www.gpg4win.org/package-integrity.html`r`nhttps://files.gpg4win.org/gpg4win-5.1.1.exe`r`nhttps://gnupg.org/download/index.html`r`nSource: https://files.gpg4win.org/gpg4win-5.1.1.tar.xz`r`nPackage SHA256: ED19C0C89EC42FE32C52A7ABD6E3DA5F491752700546B9EED7DDF82FD303CDDF`r`n")
    Assert-Runtime $runtimePath
    Assert-LocalPath $destinationPath
    $null = New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destinationPath)) -Force
    Move-Item -LiteralPath $runtimePath -Destination $destinationPath
    Write-Output 'Verifierad GnuPG 2.5.24 är uppackad lokalt. Ingen global installation, kryptering eller rensning har körts.'
}
finally {
    Assert-LocalPath $staging
    if ([IO.Path]::GetDirectoryName($staging) -ne $toolsPath -or -not [IO.Path]::GetFileName($staging).StartsWith('gnupg-stage-')) { throw 'Arbetsmappens identitet kunde inte verifieras.' }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
