# Builds every variant into engine\bin (engine\bin-$env:SA_TAG when SA_TAG is set):
#   bin\autocad\<year>\  bin\revit\<year>\  bin\cli\
# Usage: .\build.ps1 [-Test] [-Config Release] [-Years 2024,2025,2026,2027]
param(
    [switch]$Test,
    [string]$Config = "Release",
    [int[]]$Years = @(2024, 2025, 2026, 2027)
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "dotnet SDK not found"
    exit 1
}

$failed = $false
function Invoke-Build([string]$Label, [string[]]$BuildArgs) {
    & dotnet build @BuildArgs -c $Config -nologo -v:q -clp:NoSummary
    if ($LASTEXITCODE -eq 0) { Write-Host "OK    $Label" }
    else { Write-Host "FAIL  $Label"; $script:failed = $true }
}

Invoke-Build "SA.Core" @("src/SA.Core/SA.Core.csproj")
Invoke-Build "sa.exe (net48)" @("src/SA.Cli/SA.Cli.csproj")
foreach ($y in $Years) {
    Invoke-Build "SA.AutoCAD $y" @("src/SA.AutoCAD/SA.AutoCAD.csproj", "-p:AppYear=$y")
    Invoke-Build "SA.Revit $y" @("src/SA.Revit/SA.Revit.csproj", "-p:AppYear=$y")
}

if ($Test) {
    & dotnet test src/SA.Core.Tests/SA.Core.Tests.csproj -c $Config -nologo -v:q
    if ($LASTEXITCODE -eq 0) { Write-Host "OK    tests" } else { Write-Host "FAIL  tests"; $failed = $true }
}

if ($failed) { exit 1 } else { exit 0 }
