<#
.SYNOPSIS
    Publishes the agent and builds the Windows installer from that publish.

.DESCRIPTION
    One script rather than two steps, because the failure it exists to prevent
    is shipping an MSI built from a stale publish directory - which looks
    entirely successful and installs the previous version.

    The version is read from the csproj, so the number in Add or Remove Programs
    is the number in the assembly. Setting it in two places is how they drift.

.PARAMETER SkipPublish
    Use the publish directory as it stands. For iterating on the installer
    itself, where re-publishing 158MB each time is just waiting.

.PARAMETER OutputDir
    Where the .msi is written. Defaults to NET\dist.
#>
[CmdletBinding()]
param(
    [switch] $SkipPublish,
    [string] $OutputDir
)

$ErrorActionPreference = 'Stop'

$installerDir = $PSScriptRoot
$netRoot      = Split-Path -Parent $installerDir
$csproj       = Join-Path $netRoot 'src\PrintlyAgent\PrintlyAgent.csproj'
$publishDir   = Join-Path $netRoot 'publish'
if (-not $OutputDir) { $OutputDir = Join-Path $netRoot 'dist' }

# --- the version, from the one place that already states it ------------------
[xml] $project = Get-Content $csproj
$version = ($project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw "no <Version> in $csproj" }

# MSI versions are strictly numeric: major.minor.build, each field bounded. A
# suffix like 1.0.0-beta is legal in a csproj and not in an MSI, so it is cut
# here rather than left to fail inside WiX with a less obvious message.
$msiVersion = ($version -split '-')[0]
if ($msiVersion -notmatch '^\d+\.\d+(\.\d+)?$') {
    throw "version '$version' cannot be used as an MSI version (needs major.minor[.build])"
}

Write-Host "Printly Print Agent $version" -ForegroundColor Cyan

# --- publish -----------------------------------------------------------------
if ($SkipPublish) {
    if (-not (Test-Path (Join-Path $publishDir 'PrintlyAgentNet.exe'))) {
        throw "-SkipPublish was given but $publishDir holds no PrintlyAgentNet.exe"
    }
    Write-Host "  using the existing publish in $publishDir" -ForegroundColor Yellow
} else {
    # The publish directory is also where the agent is usually run from during
    # development, and a running one holds its own files open. Left to itself
    # that surfaces as "Access to the path 'Accessibility.dll' is denied", which
    # says nothing about the actual cause.
    $running = Get-Process PrintlyAgentNet -ErrorAction SilentlyContinue
    if ($running) {
        throw ("Printly Print Agent is running (pid $($running.Id -join ', ')) and is holding " +
               "files in $publishDir open. Close it and run this again.")
    }

    Write-Host '  publishing...'
    # Cleared first. dotnet publish overwrites but does not remove, so a file
    # dropped from the project would otherwise stay in the MSI forever.
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    & dotnet publish $csproj -c Release -o $publishDir | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
}

$fileCount = (Get-ChildItem $publishDir -Recurse -File).Count
$sizeMb    = [math]::Round(((Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "  $fileCount files, $sizeMb MB"

# --- build the MSI -----------------------------------------------------------
if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir | Out-Null }
$msi = Join-Path $OutputDir "PrintlyPrintAgent-$version-x64.msi"

Write-Host '  building the installer...'
& wix build `
    (Join-Path $installerDir 'PrintlyAgent.wxs') `
    -arch x64 `
    -b "$installerDir" `
    -b "$netRoot" `
    -d "PublishDir=$publishDir" `
    -d "ProductVersion=$msiVersion" `
    -ext WixToolset.Util.wixext `
    -ext WixToolset.UI.wixext `
    -o $msi
if ($LASTEXITCODE -ne 0) { throw "wix build failed ($LASTEXITCODE)" }

$msiMb = [math]::Round((Get-Item $msi).Length / 1MB, 1)
Write-Host ''
Write-Host "  $msi" -ForegroundColor Green
Write-Host "  $msiMb MB"
