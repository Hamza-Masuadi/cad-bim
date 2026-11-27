<#
.SYNOPSIS
    Per-user install (or -Uninstall) of the CadBim Revit engine for every Revit release found (2024-2027).

.DESCRIPTION
    For each release that is installed (or listed with -Years):
      1. copies SA.Revit.dll, SA.Core.dll (and SA.Revit.deps.json for 2025+) from engine\bin\revit\<year>\
         to %LOCALAPPDATA%\CadBim\engine\revit\<year>\, with data\checks\*.json and data\standards\*.json
         in its checks\ and standards\ subfolders (the bundled checks and default standards)
      2. writes %APPDATA%\Autodesk\Revit\Addins\<year>\CadBim.addin pointing at that copy.

    The per-user manifest folder %APPDATA%\Autodesk\Revit\Addins\<year> is used for every release. Revit 2027
    moved the ALL-USERS folder from %ProgramData% to %ProgramFiles%\Autodesk\Revit\Addins\<year>; per-user
    manifests are still read from %APPDATA% (Autodesk's Revit 2027 SDK notes: "Per-user add-in locations are
    still supported"). Nothing is written to ProgramData or Program Files, the script never elevates, and it
    never touches another add-in's files.

    Manifests cannot contain environment variables, so the absolute path of the copied DLL is written.

.PARAMETER Uninstall
    Remove the manifests this script wrote (only files carrying this engine's AddInId) and the copied DLLs.
    Logs, session files and the job ledger under %LOCALAPPDATA%\CadBim are left in place.

.PARAMETER Years
    Releases to act on. Default: every release from 2024 to 2027 that appears to be installed.

.PARAMETER BinRoot
    Folder holding revit\<year>\ build output. Default: ..\bin next to this script.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Install-Revit.ps1
    powershell -ExecutionPolicy Bypass -File Install-Revit.ps1 -Uninstall
    powershell -ExecutionPolicy Bypass -File Install-Revit.ps1 -Years 2026
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,
    [int[]]$Years,
    [string]$BinRoot
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# Fixed identity of this add-in. Never change these: uninstall and upgrades find the manifest by AddInId.
$AddInId       = '3DEDE79C-B3B5-42EB-AF2D-985096A14B0F'
$VendorId      = 'CadBim'
$FullClassName = 'SA.Revit.Host.EngineApplication'
$ManifestName  = 'CadBim.addin'
$Supported     = 2024, 2025, 2026, 2027

if (-not $BinRoot) { $BinRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'bin' }
$EngineRoot = Join-Path $env:LOCALAPPDATA 'CadBim\engine\revit'
$AddinsRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins'

function Test-RevitInstalled([int]$year) {
    $exe = Join-Path $env:ProgramFiles "Autodesk\Revit $year\Revit.exe"
    if (Test-Path -LiteralPath $exe) { return $true }
    foreach ($key in "HKLM:\SOFTWARE\Autodesk\Revit\$year", "HKLM:\SOFTWARE\Autodesk\Revit\Autodesk Revit $year") {
        if (Test-Path -LiteralPath $key) { return $true }
    }
    return $false
}

function Test-OurManifest([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    try { [xml]$x = Get-Content -LiteralPath $path -Raw } catch { return $false }
    foreach ($a in @($x.RevitAddIns.AddIn)) {
        if ($a -and ("$($a.AddInId)".Trim().Trim('{', '}') -ieq $AddInId)) { return $true }
    }
    return $false
}

function New-Manifest([string]$assemblyPath) {
    $esc = [System.Security.SecurityElement]::Escape($assemblyPath)
    return @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>CadBim Engine</Name>
    <Assembly>$esc</Assembly>
    <AddInId>$AddInId</AddInId>
    <FullClassName>$FullClassName</FullClassName>
    <VendorId>$VendorId</VendorId>
    <VendorDescription>CadBim: typed, validated jobs for Revit over a local named pipe.</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
}

$running = @(Get-Process -Name 'Revit' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Warning ("Revit is running (process id " + (($running | ForEach-Object { $_.Id }) -join ', ') +
        "). Changes take effect the next time Revit starts; a running Revit keeps the DLLs it loaded, so copying over them may fail.")
}

if (-not $Years) {
    $Years = @($Supported | Where-Object { Test-RevitInstalled $_ })
    if ($Uninstall) {
        $Years = @($Supported | Where-Object { (Test-OurManifest (Join-Path $AddinsRoot "$_\$ManifestName")) -or (Test-Path -LiteralPath (Join-Path $EngineRoot "$_")) })
    }
}
$Years = @($Years | Where-Object { $Supported -contains $_ })
if ($Years.Count -eq 0) {
    Write-Host ($(if ($Uninstall) { 'Nothing to uninstall.' } else { 'No supported Revit release (2024-2027) was found. Use -Years to install anyway.' }))
    exit 0
}

$utf8 = New-Object System.Text.UTF8Encoding($false)
$failed = 0
foreach ($year in $Years) {
    $manifest = Join-Path $AddinsRoot "$year\$ManifestName"
    $target = Join-Path $EngineRoot "$year"
    try {
        if ($Uninstall) {
            if (Test-OurManifest $manifest) {
                Remove-Item -LiteralPath $manifest -Force
                Write-Host "[$year] removed $manifest"
            } elseif (Test-Path -LiteralPath $manifest) {
                Write-Warning "[$year] $manifest does not carry this engine's AddInId; left alone."
            }
            if (Test-Path -LiteralPath $target) {
                Remove-Item -LiteralPath $target -Recurse -Force
                Write-Host "[$year] removed $target"
            }
            continue
        }

        $source = Join-Path $BinRoot "revit\$year"
        $files = @('SA.Revit.dll', 'SA.Core.dll')
        if ($year -ge 2025) { $files += 'SA.Revit.deps.json' }
        foreach ($f in $files) {
            if (-not (Test-Path -LiteralPath (Join-Path $source $f))) { throw "missing $f in $source (build with: dotnet build src/SA.Revit -c Release -p:AppYear=$year)" }
        }
        if ((Test-Path -LiteralPath $manifest) -and -not (Test-OurManifest $manifest)) {
            throw "$manifest exists and belongs to something else; not overwriting it"
        }
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        foreach ($f in $files + @('SA.Revit.pdb', 'SA.Core.pdb')) {
            $from = Join-Path $source $f
            if (Test-Path -LiteralPath $from) {
                Copy-Item -LiteralPath $from -Destination (Join-Path $target $f) -Force
                Unblock-File -LiteralPath (Join-Path $target $f) -ErrorAction SilentlyContinue
            }
        }
        # Copy the *.json files into fresh folders: copying a folder onto an existing one nests it (checks\checks).
        $dataRoot = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'data'
        foreach ($sub in 'checks', 'standards') {
            $from = Join-Path $dataRoot $sub
            $to = Join-Path $target $sub
            if (Test-Path -LiteralPath $to) { Remove-Item -LiteralPath $to -Recurse -Force }
            if (Test-Path -LiteralPath $from) {
                New-Item -ItemType Directory -Force -Path $to | Out-Null
                Copy-Item -Path (Join-Path $from '*.json') -Destination $to -Force
            } else {
                Write-Warning "[$year] $from not found; the engine has no bundled $sub."
            }
        }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $manifest) | Out-Null
        [System.IO.File]::WriteAllText($manifest, (New-Manifest (Join-Path $target 'SA.Revit.dll')), $utf8)
        Write-Host "[$year] copied engine to $target"
        Write-Host "[$year] wrote $manifest"
    } catch {
        $failed++
        Write-Warning "[$year] $($_.Exception.Message)"
    }
}
if ($failed -gt 0) { exit 1 }
Write-Host ($(if ($Uninstall) { 'Uninstall finished.' } else { 'Install finished. Start Revit; the engine writes %LOCALAPPDATA%\CadBim\sessions\revit-<pid>.json when it is listening.' }))
