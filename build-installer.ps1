<#
.SYNOPSIS
    Builds the Orbitale installer: dist\Orbitale-Setup-<version>.exe.

.DESCRIPTION
    1. Runs the tests (skip with -SkipTests).
    2. Publishes the app self-contained for win-x64 into artifacts\publish.
    3. Compiles installer\Orbitale.iss with Inno Setup 6.

    The version comes from <Version> in src\Orbitale.App\Orbitale.App.csproj.
    Inno Setup 6 is required; install it once with:
        winget install --id JRSoftware.InnoSetup -e

.EXAMPLE
    .\build-installer.ps1
.EXAMPLE
    .\build-installer.ps1 -SkipTests
#>
[CmdletBinding()]
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\Orbitale.App\Orbitale.App.csproj'
$publishDir = Join-Path $root 'artifacts\publish'
$distDir = Join-Path $root 'dist'
$script = Join-Path $root 'installer\Orbitale.iss'

function Find-InnoCompiler {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $keys = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
    )
    foreach ($key in $keys) {
        $location = (Get-ItemProperty -Path $key -ErrorAction SilentlyContinue).InstallLocation
        if ($location -and (Test-Path (Join-Path $location 'ISCC.exe'))) { return Join-Path $location 'ISCC.exe' }
    }
    foreach ($dir in @("${env:ProgramFiles(x86)}\Inno Setup 6", "$env:ProgramFiles\Inno Setup 6", "$env:LOCALAPPDATA\Programs\Inno Setup 6")) {
        if (Test-Path (Join-Path $dir 'ISCC.exe')) { return Join-Path $dir 'ISCC.exe' }
    }
    return $null
}

# Check the tool first, so a missing Inno Setup fails before minutes of building.
$iscc = Find-InnoCompiler
if (-not $iscc) {
    Write-Error "Inno Setup 6 was not found. Install it once with:`n    winget install --id JRSoftware.InnoSetup -e`nthen run this script again."
}

[xml]$csproj = Get-Content $project
$version = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { $version = '1.0.0' }
Write-Host "Orbitale $version" -ForegroundColor Cyan

if (-not $SkipTests) {
    Write-Host '-- Tests' -ForegroundColor Cyan
    dotnet test (Join-Path $root 'Orbitale.sln') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed: no installer built.' }
}

Write-Host '-- Publishing (self-contained, win-x64)' -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained -p:Platform=x64 -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
foreach ($required in 'Orbitale.exe', 'Orbitale.pri', 'MainWindow.xbf') {
    if (-not (Test-Path (Join-Path $publishDir $required))) { throw "The publish output lacks $required; the installed app would not start." }
}

Write-Host '-- Building the installer' -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
& $iscc "/DAppVersion=$version" "/DPublishDir=$publishDir" "/DOutputDir=$distDir" $script
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }

$setup = Join-Path $distDir "Orbitale-Setup-$version.exe"
$sizeMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host "Installer ready: $setup ($sizeMb MB)" -ForegroundColor Green
