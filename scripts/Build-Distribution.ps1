param([switch]$SkipTests)
$ErrorActionPreference='Stop'
$projectPath=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$env:DOTNET_CLI_HOME=Join-Path $projectPath '.build-home'
$env:NUGET_PACKAGES=Join-Path $projectPath '.packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
function Check-Exit([string]$Step) {if($LASTEXITCODE -ne 0){throw ($Step+' misslyckades.')}}
function Create-Zip([string]$Root,[string]$ZipPath) {
    if(Test-Path -LiteralPath $ZipPath){Remove-Item -LiteralPath $ZipPath -Force}
    $archive=[IO.Compression.ZipFile]::Open($ZipPath,[IO.Compression.ZipArchiveMode]::Create)
    try {foreach($file in Get-ChildItem -LiteralPath $Root -Recurse -File){$relative=$file.FullName.Substring($Root.TrimEnd('\').Length+1).Replace('\','/');[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,$relative,[IO.Compression.CompressionLevel]::Optimal)|Out-Null}}
    finally{$archive.Dispose()}
}
Push-Location $projectPath
try {
    $null=New-Item -ItemType Directory -Path artifacts,release -Force
    & (Join-Path $PSScriptRoot 'Generate-Icon.ps1')
    & (Join-Path $PSScriptRoot 'Install-GnuPG.ps1')
    Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
    $engineZip=Join-Path $projectPath 'artifacts\gnupg-runtime.zip'
    if(Test-Path -LiteralPath $engineZip){Remove-Item -LiteralPath $engineZip -Force}
    $zip=[IO.Compression.ZipFile]::Open($engineZip,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach($file in Get-ChildItem tools\gnupg\bin -File){[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,'bin/'+$file.Name,[IO.Compression.CompressionLevel]::Optimal)|Out-Null}
        foreach($name in @('LICENSES.txt','ORIGIN.txt')){[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $projectPath ('tools\gnupg\'+$name)),$name,[IO.Compression.CompressionLevel]::Optimal)|Out-Null}
    }finally{$zip.Dispose()}
    if(-not $SkipTests){
        & (Join-Path $PSScriptRoot 'Build.ps1')
        dotnet restore tests\NordVault.PortableTests\NordVault.PortableTests.csproj --configfile NuGet.Config;Check-Exit 'Återställning av portabla tester'
        dotnet run --project tests\NordVault.PortableTests\NordVault.PortableTests.csproj --configuration Release --no-restore;Check-Exit 'Portabla tester'
    }
    $publish=@('--configuration','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:EnableCompressionInSingleFile=true','-p:DebugType=embedded','--no-restore')
    $restore=@('-r','win-x64','-p:PublishSingleFile=true','-p:SelfContained=true','--configfile','NuGet.Portable.Config')
    dotnet restore src\NordVault.Reader\NordVault.Reader.csproj @restore;Check-Exit 'Läsarens runtimepaket'
    dotnet publish src\NordVault.Reader\NordVault.Reader.csproj @publish --output artifacts\reader;Check-Exit 'Portabel läsare'
    [IO.File]::WriteAllText((Join-Path $projectPath 'artifacts\ArchiveTemplate.sha256'),(Get-FileHash artifacts\reader\NordVault.Reader.exe -Algorithm SHA256).Hash,[Text.UTF8Encoding]::new($false))
    $stage=Join-Path $projectPath ('artifacts\distribution-'+[Guid]::NewGuid().ToString('N'))
    $portable=Join-Path $stage 'NordVault-Portable'
    dotnet restore src\NordVault.App\NordVault.App.csproj @restore;Check-Exit 'Appens runtimepaket'
    dotnet publish src\NordVault.App\NordVault.App.csproj @publish --output $portable;Check-Exit 'Portabel app'
    Copy-Item -LiteralPath artifacts\reader\NordVault.Reader.exe -Destination (Join-Path $portable 'NordVault.Reader.exe')
    Copy-Item -LiteralPath README.sv.md -Destination (Join-Path $portable 'README.sv.md')
    Copy-Item -LiteralPath docs -Destination $portable -Recurse
    $null=New-Item -ItemType Directory -Path (Join-Path $portable 'tools') -Force
    Copy-Item -LiteralPath tools\gnupg -Destination (Join-Path $portable 'tools') -Recurse
    if(Test-Path -LiteralPath tools\sdelete64.exe){Copy-Item -LiteralPath tools\sdelete64.exe -Destination (Join-Path $portable 'tools\sdelete64.exe');Copy-Item -LiteralPath tools\SDelete-Eula.txt -Destination (Join-Path $portable 'tools\SDelete-Eula.txt')}
    Copy-Item -LiteralPath scripts\Install-SDelete.ps1 -Destination (Join-Path $portable 'scripts\Install-SDelete.ps1')
    Copy-Item -LiteralPath scripts\Uninstall-NordVault.cmd -Destination (Join-Path $portable 'Avinstallera NordVault.cmd')
    $files=@(Get-ChildItem -LiteralPath $portable -Recurse -File|ForEach-Object{[ordered]@{Path=$_.FullName.Substring($portable.Length+1).Replace('\','/');Size=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})
    $manifest=[ordered]@{Product='NordVault.Private.v1';Version='0.4.3';Files=$files}
    [IO.File]::WriteAllText((Join-Path $projectPath 'artifacts\setup-manifest.json'),($manifest|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
    Create-Zip $portable (Join-Path $projectPath 'artifacts\setup-payload.zip')
    dotnet restore src\NordVault.Setup\NordVault.Setup.csproj @restore;Check-Exit 'Installationsprogrammets runtimepaket'
    dotnet publish src\NordVault.Setup\NordVault.Setup.csproj @publish --output artifacts\setup;Check-Exit 'Installationsprogram'
    Copy-Item -LiteralPath artifacts\setup\NordVault.Setup.exe -Destination (Join-Path $projectPath 'release\NordVault-Setup-0.4.3.exe') -Force
    Copy-Item -LiteralPath artifacts\setup\NordVault.Setup.exe -Destination (Join-Path $projectPath 'Setup-NordVault-0.4.3.exe') -Force
    Copy-Item -LiteralPath artifacts\setup\NordVault.Setup.exe -Destination (Join-Path $portable 'NordVault.Setup.exe')
    Create-Zip $portable (Join-Path $projectPath 'release\NordVault-Portable-0.4.3.zip')
    $releaseFolder=Join-Path $projectPath 'release\NordVault-Portable'
    $null=New-Item -ItemType Directory -Path $releaseFolder -Force
    Copy-Item -Path (Join-Path $portable '*') -Destination $releaseFolder -Recurse -Force
    Get-FileHash release\NordVault-Setup-0.4.3.exe,release\NordVault-Portable-0.4.3.zip -Algorithm SHA256|ForEach-Object{$_.Hash+'  '+[IO.Path]::GetFileName($_.Path)}|Set-Content release\SHA256SUMS.txt -Encoding utf8
    Get-FileHash Setup-NordVault-0.4.3.exe -Algorithm SHA256|ForEach-Object{$_.Hash+'  '+[IO.Path]::GetFileName($_.Path)}|Set-Content SHA256SUMS.txt -Encoding utf8
    if(-not $SkipTests){dotnet run --project tests\NordVault.PortableTests\NordVault.PortableTests.csproj --configuration Release --no-restore -- --published;Check-Exit 'Tester av publicerad distribution'}
    Write-Output 'Klart: Setup-NordVault-0.4.3.exe i rotmappen och release\NordVault-Portable-0.4.3.zip. Ingen systeminstallation eller diskåtgärd har körts.'
}finally{Pop-Location}
