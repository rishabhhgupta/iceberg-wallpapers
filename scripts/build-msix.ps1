<#
.SYNOPSIS
    Builds a Store-ready .msix for Lively.

.DESCRIPTION
    Lively is split into two halves:
      * the "core"  -> src\Lively\Lively\Lively.csproj      (produces Iceberg.exe)
      * the "UI"    -> src\Lively\Lively.UI.WinUI\...csproj (produces the WinUI app)

    The Store package manifest points at Build\Iceberg.exe, so the core output has
    to be staged into src\Lively\Lively.UI.WinUI\Build before the UI project is
    packaged. This script does that staging, builds the package, and copies the
    result into artifacts\msix.

.EXAMPLE
    .\build-msix.ps1

.EXAMPLE
    .\build-msix.ps1 -Clean
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Runtime = 'win-x64',
    [string]$Platform = 'x64',

    # Path to dotnet.exe. Defaults to user .dotnet if found, else PATH.
    [string]$Dotnet = '',

    # Delete previous staging output before publishing.
    [switch]$Clean,

    # Sign the package with the certificate in the project file. The Microsoft
    # Store signs submissions itself, so this is off by default.
    [switch]$Sign
)

$ErrorActionPreference = 'Stop'

if (-not $Dotnet) {
    $userDotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if (Test-Path $userDotnet) {
        $Dotnet = $userDotnet
    } else {
        $Dotnet = 'dotnet'
    }
}

function Write-Step([string]$Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

$repo      = Split-Path -Parent $PSScriptRoot
$livelyDir = Join-Path $repo 'src\Lively'
$coreProj  = Join-Path $livelyDir 'Lively\Lively.csproj'
$uiProj    = Join-Path $livelyDir 'Lively.UI.WinUI\Lively.UI.WinUI.csproj'
$stageDir  = Join-Path $livelyDir 'Lively.UI.WinUI\Build'
$publishDir = Join-Path $repo 'artifacts\core-publish'
$msixOut   = Join-Path $repo 'artifacts\msix'

foreach ($p in @($coreProj, $uiProj)) {
    if (-not (Test-Path $p)) { throw "Project not found: $p" }
}

Write-Step "dotnet version"
& $Dotnet --version

if ($Clean) {
    Write-Step "Cleaning previous output"
    foreach ($d in @($stageDir, $publishDir)) {
        if (Test-Path $d) { Remove-Item $d -Recurse -Force }
    }
}

Write-Step "Publishing core (Iceberg.exe)"
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
& $Dotnet publish $coreProj `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:Platform=$Platform `
    -o $publishDir

if ($LASTEXITCODE -ne 0) { throw "Core publish failed with exit code $LASTEXITCODE" }

$coreExe = Join-Path $publishDir 'Iceberg.exe'
if (-not (Test-Path $coreExe)) { throw "Core publish did not produce Iceberg.exe" }

Write-Step "Staging core into Lively.UI.WinUI\Build"
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
# robocopy is used because it handles deep trees and long paths reliably.
# Exit codes 0-7 are success; 8+ is a real failure.
& robocopy $publishDir $stageDir /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Staging failed (robocopy exit $LASTEXITCODE)" }
$global:LASTEXITCODE = 0

Write-Step "Staging player plugins (mpv, WebView2, watchdog)"
$pluginStore = Join-Path $repo 'artifacts\plugins'
$pluginTarget = Join-Path $stageDir 'Plugins'
if (Test-Path $pluginStore) {
    New-Item -ItemType Directory -Path $pluginTarget -Force | Out-Null
    & robocopy $pluginStore $pluginTarget /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Plugin staging failed (robocopy exit $LASTEXITCODE)" }
    $global:LASTEXITCODE = 0
} else {
    Write-Warning "No plugin store at $pluginStore. Web and video wallpapers will not play."
}

Write-Step "Packing MSIX"
$signing = if ($Sign) { 'true' } else { 'false' }
& $Dotnet build $uiProj `
    -c $Configuration `
    -p:Platform=$Platform `
    -p:RuntimeIdentifiers=$Runtime `
    -p:IsMsixRelease=true `
    -p:GenerateAppxPackageOnBuild=true `
    -p:AppxBundle=Never `
    -p:AppxPackageSigningEnabled=$signing

if ($LASTEXITCODE -ne 0) { throw "MSIX build failed with exit code $LASTEXITCODE" }

Write-Step "Collecting package"
$appPackages = Join-Path $livelyDir "Lively.UI.WinUI\bin\$Platform\$Configuration\net9.0-windows10.0.22621.0\AppPackages"
$msix = Get-ChildItem $appPackages -Recurse -Filter *.msix -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike 'Microsoft.WindowsAppRuntime*' } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

if (-not $msix) { throw "No .msix was produced under $appPackages" }

New-Item -ItemType Directory -Path $msixOut -Force | Out-Null
Copy-Item $msix.FullName $msixOut -Force

$final = Join-Path $msixOut $msix.Name
Write-Host ''
Write-Host "MSIX ready:" -ForegroundColor Green
Write-Host "  $final"
Write-Host ("  {0:N1} MB" -f ((Get-Item $final).Length / 1MB))
