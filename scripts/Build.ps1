param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$projectPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$env:DOTNET_CLI_HOME = Join-Path $projectPath '.build-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:NUGET_PACKAGES = Join-Path $projectPath '.packages'
Push-Location $projectPath
try {
    if (-not $SkipTests) {
        dotnet restore tests\NordVault.Tests\NordVault.Tests.csproj --configfile NuGet.Config
        if ($LASTEXITCODE -ne 0) { throw 'Testprojektets lokala beroenden kunde inte återställas.' }
        dotnet run --project tests\NordVault.Tests\NordVault.Tests.csproj --configuration Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Säkerhetstesterna misslyckades.' }
        & (Join-Path $PSScriptRoot 'Test-Backend.ps1')
        dotnet restore tests\NordVault.FileTests\NordVault.FileTests.csproj --configfile NuGet.Config
        if ($LASTEXITCODE -ne 0) { throw 'Filtestprojektets lokala beroenden kunde inte återställas.' }
        dotnet run --project tests\NordVault.FileTests\NordVault.FileTests.csproj --configuration Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Integrationstesterna med GnuPG misslyckades.' }
    }
    dotnet restore src\NordVault.App\NordVault.App.csproj --configfile NuGet.Config
    if ($LASTEXITCODE -ne 0) { throw 'Projektets lokala beroenden kunde inte återställas.' }
    dotnet publish src\NordVault.App\NordVault.App.csproj --configuration Release --output dist --self-contained false --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Bygget misslyckades.' }
    Copy-Item -LiteralPath (Join-Path $projectPath 'README.sv.md') -Destination (Join-Path $projectPath 'dist\README.sv.md') -Force
    Copy-Item -LiteralPath (Join-Path $projectPath 'docs') -Destination (Join-Path $projectPath 'dist') -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-SDelete.ps1') -Destination (Join-Path $projectPath 'dist\scripts\Install-SDelete.ps1') -Force
    $gnuPgPath = Join-Path $projectPath 'tools\gnupg'
    if (-not (Test-Path -LiteralPath (Join-Path $gnuPgPath 'bin\gpg.exe'))) { throw 'GnuPG-filmotorn saknas. Kör scripts\Install-GnuPG.ps1 och bygg igen.' }
    $null = New-Item -ItemType Directory -Path (Join-Path $projectPath 'dist\tools') -Force
    Copy-Item -LiteralPath $gnuPgPath -Destination (Join-Path $projectPath 'dist\tools') -Recurse -Force
    # Installer stays at project level: it verifies the source-controlled runtime manifest.
    if (Test-Path -LiteralPath (Join-Path $projectPath 'tools\sdelete64.exe')) {
        $null = New-Item -ItemType Directory -Path (Join-Path $projectPath 'dist\tools') -Force
        Copy-Item -LiteralPath (Join-Path $projectPath 'tools\sdelete64.exe') -Destination (Join-Path $projectPath 'dist\tools\sdelete64.exe') -Force
        if (Test-Path -LiteralPath (Join-Path $projectPath 'tools\SDelete-Eula.txt')) {
            Copy-Item -LiteralPath (Join-Path $projectPath 'tools\SDelete-Eula.txt') -Destination (Join-Path $projectPath 'dist\tools\SDelete-Eula.txt') -Force
        }
    }
    Get-ChildItem (Join-Path $projectPath 'dist') -Recurse -File | Where-Object Name -ne 'SHA256SUMS.txt' | Get-FileHash -Algorithm SHA256 | ForEach-Object { $_.Hash + '  ' + $_.Path.Substring($projectPath.Length + 6) } | Set-Content -LiteralPath (Join-Path $projectPath 'dist\SHA256SUMS.txt') -Encoding utf8
    Write-Output 'Klart: dist\NordVault.exe. Bygget har inte krypterat eller rensat någon disk.'
}
finally { Pop-Location }
