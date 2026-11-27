<#
.SYNOPSIS
    Per-user install (or -Uninstall) of everything CadBim needs on one Windows machine: the AutoCAD engine, the
    Revit engine, the sa.exe client, and the skill data (checks, standards, schemas).

.DESCRIPTION
    1. Runs Install-AutoCAD.ps1 and Install-Revit.ps1 from this folder (each installs only the releases it finds).
    2. Copies engine\bin\cli\* (sa.exe, SA.Core.dll...) to %LOCALAPPDATA%\CadBim\bin\.
    3. Copies the skill's data\ folder to %LOCALAPPDATA%\CadBim\data\, replacing the previous copy, so a
       relative path such as data/standards/us-ncs-style.json in a job resolves there.

    The engines also get their own copies of the checks and standards (inside the AutoCAD bundle and beside the Revit
    add-in). Nothing is written to %LOCALAPPDATA%\CadBim\checks or ...\standards: those folders are for the
    user's own files, and the AutoCAD engine loads both them and its bundled copy, so the same file in both would
    define every check twice.

    The script never elevates. -AddToPath adds the bin folder to the user's PATH (not the machine PATH).

.PARAMETER Uninstall
    Run both per-app uninstalls, remove %LOCALAPPDATA%\CadBim\bin and ...\data, and take the bin folder off
    the user's PATH. Logs, session files, the job ledger and the user's own checks and standards stay.

.PARAMETER Years
    Releases to install, passed to both per-app installers. Default: every release each one finds.

.PARAMETER BinRoot
    Build output folder (autocad\<year>, revit\<year>, cli). Default: ..\bin next to this script.

.PARAMETER Force
    Passed to Install-AutoCAD.ps1: install every built AutoCAD release even when it is not detected.

.PARAMETER SkipAutoCAD
    Do not run Install-AutoCAD.ps1.

.PARAMETER SkipRevit
    Do not run Install-Revit.ps1.

.PARAMETER AddToPath
    Add %LOCALAPPDATA%\CadBim\bin to the user's PATH. New terminals see it; the current one does not.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install.ps1
    powershell -ExecutionPolicy Bypass -File install.ps1 -AddToPath
    powershell -ExecutionPolicy Bypass -File install.ps1 -Years 2026 -SkipRevit
    powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,
    [int[]]$Years,
    [string]$BinRoot,
    [switch]$Force,
    [switch]$SkipAutoCAD,
    [switch]$SkipRevit,
    [switch]$AddToPath
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$EngineDir = Split-Path -Parent $PSScriptRoot
$DataDir   = Join-Path (Split-Path -Parent $EngineDir) 'data'
if (-not $BinRoot) { $BinRoot = Join-Path $EngineDir 'bin' }
$Root      = Join-Path $env:LOCALAPPDATA 'CadBim'
$CliTarget = Join-Path $Root 'bin'
$DataTarget = Join-Path $Root 'data'

$failed = @()

function Say([string]$text) { Write-Host $text }

# Runs one per-app script and returns its exit code. Those scripts end with exit; called with & that ends only them.
function Invoke-AppInstaller([string]$name, [hashtable]$params) {
    $script = Join-Path $PSScriptRoot $name
    if (-not (Test-Path -LiteralPath $script)) { throw "$script not found" }
    $global:LASTEXITCODE = 0
    & $script @params | Out-Host
    return [int]$global:LASTEXITCODE
}

function Get-UserPath {
    $p = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($null -eq $p) { return '' }
    return $p
}

function Test-OnPath([string]$pathValue, [string]$dir) {
    foreach ($part in $pathValue.Split(';')) {
        if ($part.Trim().TrimEnd('\') -ieq $dir.TrimEnd('\')) { return $true }
    }
    return $false
}

# ---------------------------------------------------------------- per-app engines

$common = @{}
if ($Uninstall) { $common['Uninstall'] = $true }
if ($Years) { $common['Years'] = $Years }
if ($PSBoundParameters.ContainsKey('BinRoot')) { $common['BinRoot'] = $BinRoot }

if ($SkipAutoCAD) {
    Say "AutoCAD: skipped (-SkipAutoCAD)."
} else {
    $p = $common.Clone()
    if ($Force -and -not $Uninstall) { $p['Force'] = $true }
    try {
        $code = Invoke-AppInstaller 'Install-AutoCAD.ps1' $p
        # Install-AutoCAD.ps1: 1 = no release both built and installed (not an error here), 2 = AutoCAD holds the bundle.
        if ($code -eq 1 -and -not $Uninstall) { Say "AutoCAD: nothing installed (no release is both built and detected)." }
        elseif ($code -ne 0) { $failed += "AutoCAD (exit $code)" }
    } catch {
        $failed += "AutoCAD ($($_.Exception.Message))"
        Write-Warning "AutoCAD: $($_.Exception.Message)"
    }
}

if ($SkipRevit) {
    Say "Revit: skipped (-SkipRevit)."
} else {
    try {
        $code = Invoke-AppInstaller 'Install-Revit.ps1' $common.Clone()
        if ($code -ne 0) { $failed += "Revit (exit $code)" }
    } catch {
        $failed += "Revit ($($_.Exception.Message))"
        Write-Warning "Revit: $($_.Exception.Message)"
    }
}

# ---------------------------------------------------------------- client and data

if ($Uninstall) {
    foreach ($dir in $CliTarget, $DataTarget) {
        try {
            if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force; Say "Removed $dir" }
        } catch {
            $failed += "remove $dir ($($_.Exception.Message))"
            Write-Warning "Could not remove $dir : $($_.Exception.Message)"
        }
    }
    $userPath = Get-UserPath
    if (Test-OnPath $userPath $CliTarget) {
        $kept = @($userPath.Split(';') | Where-Object { $_ -and ($_.Trim().TrimEnd('\') -ine $CliTarget.TrimEnd('\')) })
        [Environment]::SetEnvironmentVariable('Path', ($kept -join ';'), 'User')
        Say "Removed $CliTarget from the user PATH."
    }
    Say "Left in place: $Root (logs, session files, job ledger, your own checks and standards)."
} else {
    New-Item -ItemType Directory -Force -Path $Root | Out-Null

    # sa.exe: stage beside the target, then swap, so a failed copy never leaves a mixed client.
    $cliSource = Join-Path $BinRoot 'cli'
    if (-not (Test-Path -LiteralPath (Join-Path $cliSource 'sa.exe'))) {
        $failed += "sa.exe (not built: $cliSource\sa.exe missing; run build.ps1)"
        Write-Warning "sa.exe not found in $cliSource; the client was not installed. Run build.ps1 first."
    } else {
        $stage = "$CliTarget.new-" + [Guid]::NewGuid().ToString('N').Substring(0, 8)
        try {
            New-Item -ItemType Directory -Path $stage | Out-Null
            Copy-Item -Path (Join-Path $cliSource '*') -Destination $stage -Force
            Get-ChildItem -LiteralPath $stage -File | ForEach-Object { Unblock-File -LiteralPath $_.FullName -ErrorAction SilentlyContinue }
            if (Test-Path -LiteralPath $CliTarget) { Remove-Item -LiteralPath $CliTarget -Recurse -Force }
            Rename-Item -LiteralPath $stage -NewName (Split-Path -Leaf $CliTarget)
            $stage = $null
            Say "Installed sa.exe to $CliTarget"
        } catch {
            $failed += "sa.exe ($($_.Exception.Message))"
            Write-Warning "sa.exe: $($_.Exception.Message) (a running sa.exe locks its files)"
        } finally {
            if ($stage -and (Test-Path -LiteralPath $stage)) { Remove-Item -LiteralPath $stage -Recurse -Force }
        }
    }

    # Data: remove the old copy first; Copy-Item of a folder onto an existing folder nests it (data\data).
    if (-not (Test-Path -LiteralPath $DataDir)) {
        $failed += "data ($DataDir not found)"
        Write-Warning "$DataDir not found; the data was not installed."
    } else {
        try {
            if (Test-Path -LiteralPath $DataTarget) { Remove-Item -LiteralPath $DataTarget -Recurse -Force }
            Copy-Item -LiteralPath $DataDir -Destination $DataTarget -Recurse -Force
            Get-ChildItem -LiteralPath $DataTarget -Directory -Recurse -Filter '__pycache__' |
                ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
            Say "Installed data (checks, standards, schemas) to $DataTarget"
        } catch {
            $failed += "data ($($_.Exception.Message))"
            Write-Warning "data: $($_.Exception.Message)"
        }
    }

    if ($AddToPath) {
        $userPath = Get-UserPath
        if (Test-OnPath $userPath $CliTarget) {
            Say "$CliTarget is already on the user PATH."
        } else {
            $new = if ($userPath.Trim()) { $userPath.TrimEnd(';') + ';' + $CliTarget } else { $CliTarget }
            [Environment]::SetEnvironmentVariable('Path', $new, 'User')
            Say "Added $CliTarget to the user PATH (new terminals only)."
        }
    } else {
        Say "Client: $(Join-Path $CliTarget 'sa.exe') (use -AddToPath to call it as sa)."
    }
}

if ($failed.Count -gt 0) {
    Say "Finished with problems:"
    foreach ($f in $failed) { Say "  $f" }
    exit 1
}
Say ($(if ($Uninstall) { 'Uninstall finished.' } else { 'Install finished.' }))
exit 0
