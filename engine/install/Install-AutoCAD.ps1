<#
.SYNOPSIS
    Per-user install (or -Uninstall) of the CadBim AutoCAD engine as an autoloader bundle, for every AutoCAD
    release found (2024-2027).

.DESCRIPTION
    Installs %APPDATA%\Autodesk\ApplicationPlugins\CadBim.bundle\:

        PackageContents.xml
        Contents\2024\SA.AutoCAD.dll  SA.Core.dll  (pdb files when present)
        Contents\2025\SA.AutoCAD.dll  SA.Core.dll  SA.AutoCAD.deps.json
        Contents\2026\...             Contents\2027\...
        Contents\data\checks\         (check definitions; copied from ..\..\data\checks when that folder exists)
        Contents\data\standards\      (standard presets, the bundled default standard; from ..\..\data\standards)

    PackageContents.xml has one Components element per installed release, each with RuntimeRequirements
    OS="Win64" Platform="AutoCAD*" SeriesMin=SeriesMax=<release number>, and one .Net ComponentEntry that loads that
    release's DLL at start-up. Release numbers (ACADVER): 2024 = R24.3, 2025 = R25.0, 2026 = R25.1, 2027 = R26.0.
    SeriesMax is always set: from 2025 the autoloader checks RuntimeRequirements for .NET components and treats a
    missing SeriesMax as "compatible with everything", which loads the wrong build into a newer release.
    SA.Core.dll is copied beside each engine DLL; .NET resolves it from the engine's own folder.

    The script never elevates and writes nothing outside the bundle folder. It refuses to run while an AutoCAD process
    has a DLL from this bundle loaded, because Windows locks loaded DLLs and a half-replaced bundle would mix builds.

.PARAMETER Uninstall
    Remove the bundle folder. Logs, session files and the job ledger under %LOCALAPPDATA%\CadBim stay.

.PARAMETER Years
    Releases to install. Default: every release from 2024 to 2027 that is installed on this machine and built.

.PARAMETER BinRoot
    Folder holding autocad\<year>\ build output. Default: ..\bin next to this script.

.PARAMETER Force
    Install every built release even when AutoCAD of that release is not detected.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Install-AutoCAD.ps1
    powershell -ExecutionPolicy Bypass -File Install-AutoCAD.ps1 -Years 2026
    powershell -ExecutionPolicy Bypass -File Install-AutoCAD.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,
    [int[]]$Years,
    [string]$BinRoot,
    [switch]$Force
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# Fixed identity of the bundle. Never change: upgrades replace the bundle with the same codes.
$ProductCode = '{6C1F0E2A-6E5B-4B0C-9A57-2B8E3D41C7A1}'
$UpgradeCode = '{0B4D7F63-21C9-4E4D-8F1A-9D3C55E2A6B4}'
$BundleName  = 'CadBim.bundle'
# A plain hashtable: an [ordered] dictionary indexed with an integer would index by position, not by key.
$Series      = @{ 2024 = 'R24.3'; 2025 = 'R25.0'; 2026 = 'R25.1'; 2027 = 'R26.0' }

if (-not $BinRoot) { $BinRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'bin' }
$PluginsRoot = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins'
$Bundle      = Join-Path $PluginsRoot $BundleName
$DataChecks  = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'data\checks'
$DataStandards = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'data\standards'

function Say([string]$text) { Write-Host $text }

function Test-Elevated {
    try {
        $id = [Security.Principal.WindowsIdentity]::GetCurrent()
        return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch { return $false }
}

function Test-AutoCadInstalled([int]$year) {
    $r = $Series[$year]
    foreach ($hive in 'HKLM:', 'HKCU:') {
        if (Test-Path -LiteralPath "$hive\SOFTWARE\Autodesk\AutoCAD\$r") { return $true }
    }
    return $false
}

# Returns a list of problems: AutoCAD processes that have this bundle loaded, or whose modules cannot be inspected.
function Get-BundleLocks {
    $problems = @()
    $procs = @(Get-Process -Name 'acad', 'accoreconsole' -ErrorAction SilentlyContinue)
    foreach ($p in $procs) {
        $mods = $null
        try { $mods = $p.Modules } catch { $mods = $null }
        if ($null -eq $mods) {
            $problems += "AutoCAD process $($p.Id) is running and its loaded modules cannot be read (run this script as the same user, or close AutoCAD)."
            continue
        }
        foreach ($m in $mods) {
            $f = $null
            try { $f = $m.FileName } catch { }
            if ($f -and $f.StartsWith($Bundle, [StringComparison]::OrdinalIgnoreCase)) {
                $problems += "AutoCAD process $($p.Id) has $f loaded."
                break
            }
        }
    }
    return ,$problems
}

function New-PackageContents([int[]]$installed) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
    [void]$sb.AppendLine("<ApplicationPackage SchemaVersion=""1.0"" AutodeskProduct=""AutoCAD"" ProductType=""Application"" Name=""CadBim"" AppVersion=""1.0.0"" Description=""CadBim engine: typed, validated drawing jobs over a local named pipe."" Author=""CadBim"" ProductCode=""$ProductCode"" UpgradeCode=""$UpgradeCode"">")
    [void]$sb.AppendLine('  <CompanyDetails Name="CadBim" />')
    foreach ($y in $installed) {
        $r = $Series[$y]
        [void]$sb.AppendLine("  <Components Description=""AutoCAD $y"">")
        [void]$sb.AppendLine("    <RuntimeRequirements OS=""Win64"" Platform=""AutoCAD*"" SeriesMin=""$r"" SeriesMax=""$r"" />")
        [void]$sb.AppendLine("    <ComponentEntry AppName=""CadBim$y"" AppDescription=""CadBim engine for AutoCAD $y"" ModuleName=""./Contents/$y/SA.AutoCAD.dll"" AppType="".Net"" LoadOnAutoCADStartup=""True"">")
        [void]$sb.AppendLine("      <RuntimeRequirements OS=""Win64"" Platform=""AutoCAD*"" SeriesMin=""$r"" SeriesMax=""$r"" />")
        [void]$sb.AppendLine('    </ComponentEntry>')
        [void]$sb.AppendLine('  </Components>')
    }
    [void]$sb.AppendLine('</ApplicationPackage>')
    return $sb.ToString()
}

if (Test-Elevated) {
    Say "Note: this PowerShell is elevated. The engine is installed for the account running it ($env:USERNAME) under $env:APPDATA."
}

$locks = Get-BundleLocks
if ($locks.Count -gt 0) {
    Say "Refusing to change the bundle while AutoCAD may be using it:"
    foreach ($l in $locks) { Say "  $l" }
    Say "Close AutoCAD (every session) and run this script again. Windows keeps loaded DLLs locked, and replacing only some files would leave a mixed install."
    exit 2
}

if ($Uninstall) {
    if (Test-Path -LiteralPath $Bundle) {
        Remove-Item -LiteralPath $Bundle -Recurse -Force
        Say "Removed $Bundle"
    } else {
        Say "Nothing to remove: $Bundle does not exist."
    }
    Say "Left in place: $(Join-Path $env:LOCALAPPDATA 'CadBim') (logs, session files, job ledger)."
    exit 0
}

# Choose releases.
$candidates = if ($Years) { $Years } else { @($Series.Keys | Sort-Object) }
$toInstall = @()
foreach ($y in $candidates) {
    if (-not $Series.ContainsKey([int]$y)) { Say "Skipping $y : not a supported release (2024-2027)."; continue }
    $src = Join-Path $BinRoot "autocad\$y"
    if (-not (Test-Path -LiteralPath (Join-Path $src 'SA.AutoCAD.dll'))) { Say "Skipping $y : no build at $src (run build.ps1 first)."; continue }
    if (-not (Test-Path -LiteralPath (Join-Path $src 'SA.Core.dll'))) { Say "Skipping $y : SA.Core.dll missing in $src."; continue }
    if (-not $Force -and -not (Test-AutoCadInstalled $y)) { Say "Skipping $y : AutoCAD $y ($($Series[[int]$y])) not detected (use -Force to install anyway)."; continue }
    $toInstall += [int]$y
}
if ($toInstall.Count -eq 0) {
    Say "Nothing installed: no release is both built and installed."
    exit 1
}

# Stage the whole bundle in a sibling folder, then swap it in, so a failure never leaves a half-written bundle.
New-Item -ItemType Directory -Force -Path $PluginsRoot | Out-Null
$stage = Join-Path $PluginsRoot ("$BundleName.new-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    New-Item -ItemType Directory -Path $stage | Out-Null
    foreach ($y in $toInstall) {
        $src = Join-Path $BinRoot "autocad\$y"
        $dst = Join-Path $stage "Contents\$y"
        New-Item -ItemType Directory -Force -Path $dst | Out-Null
        foreach ($f in 'SA.AutoCAD.dll', 'SA.Core.dll', 'SA.AutoCAD.deps.json', 'SA.AutoCAD.pdb', 'SA.Core.pdb') {
            $p = Join-Path $src $f
            if (Test-Path -LiteralPath $p) { Copy-Item -LiteralPath $p -Destination $dst }
        }
        Say "Staged AutoCAD $y ($($Series[$y])) from $src"
    }
    $checksDst = Join-Path $stage 'Contents\data\checks'
    New-Item -ItemType Directory -Force -Path $checksDst | Out-Null
    if (Test-Path -LiteralPath $DataChecks) {
        Copy-Item -Path (Join-Path $DataChecks '*.json') -Destination $checksDst -ErrorAction SilentlyContinue
        Say "Staged check definitions from $DataChecks"
    }
    $standardsDst = Join-Path $stage 'Contents\data\standards'
    New-Item -ItemType Directory -Force -Path $standardsDst | Out-Null
    if (Test-Path -LiteralPath $DataStandards) {
        Copy-Item -Path (Join-Path $DataStandards '*.json') -Destination $standardsDst -ErrorAction SilentlyContinue
        Say "Staged standards from $DataStandards"
    } else {
        Say "Warning: $DataStandards not found; there is no bundled default standard, so checks that need one report error unless the request names a standard."
    }
    $xml = New-PackageContents $toInstall
    [IO.File]::WriteAllText((Join-Path $stage 'PackageContents.xml'), $xml, (New-Object Text.UTF8Encoding($false)))
    # Fail here, not in AutoCAD, if the manifest is not well-formed XML.
    [void]([xml]$xml)

    if (Test-Path -LiteralPath $Bundle) {
        $old = "$Bundle.old-" + [Guid]::NewGuid().ToString('N').Substring(0, 8)
        Rename-Item -LiteralPath $Bundle -NewName (Split-Path -Leaf $old)
        Rename-Item -LiteralPath $stage -NewName $BundleName
        Remove-Item -LiteralPath $old -Recurse -Force
        Say "Replaced the existing bundle."
    } else {
        Rename-Item -LiteralPath $stage -NewName $BundleName
    }
    $stage = $null
} finally {
    if ($stage -and (Test-Path -LiteralPath $stage)) { Remove-Item -LiteralPath $stage -Recurse -Force }
}

Say "Installed $Bundle for AutoCAD $($toInstall -join ', ')."
Say "Start AutoCAD; the engine loads at start-up. Type SAENGINE to see its state. Log folder: $(Join-Path $env:LOCALAPPDATA 'CadBim\logs')"
exit 0
