<#
.SYNOPSIS
  Copies a planted-defect test job into a run folder with the live document identity, a run tag on the job id and
  any {{NAME}} placeholders filled in, then lints the copy.

.DESCRIPTION
  Job ids are unique forever once committed, so every run of the test needs its own -RunTag (for example the date).
  The document named in the job must be open and active in the app. The script reads its identity with
  `sa <app> snapshot --sections document`, checks that the open file has the same file name as the job's
  document.path, and writes:
    - AutoCAD: document.path = the live path (no fingerprint).
    - Revit:   document.path and document.fingerprint = the live values. Revit's fingerprint changes after every
               commit, so stamp each Revit job right before running it.
  Text is replaced in place (the job is not re-serialised), so the stamped file stays readable.

.EXAMPLE
  .\stamp-job.ps1 -App acad -Job .\autocad\10-b-setup.json -RunTag 20261128
.EXAMPLE
  .\stamp-job.ps1 -App revit -Job .\revit\31-r1-fix-by-id.json -RunTag 20261128 -Set @{ CAD_C_LINK_ID = '412345'; LINK_DUP_ID = '412399' }
#>
param(
    [Parameter(Mandatory = $true)][ValidateSet('acad', 'revit')][string]$App,
    [Parameter(Mandatory = $true)][string]$Job,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Za-z0-9_-]+$')][string]$RunTag,
    [string]$Sa = 'sa',
    [string]$OutDir = '.\run',
    [hashtable]$Set = @{}
)
$ErrorActionPreference = 'Stop'

function JsonString([string]$s) { return ($s | ConvertTo-Json) }

$text = [System.IO.File]::ReadAllText((Resolve-Path $Job))
$parsed = $text | ConvertFrom-Json
$wantLeaf = [System.IO.Path]::GetFileName(($parsed.document.path -replace '/', '\'))

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$snapFile = Join-Path $OutDir ('snapshot-' + [System.IO.Path]::GetFileNameWithoutExtension($Job) + '.json')
& $Sa $App snapshot --sections document --out $snapFile | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sa $App snapshot failed (exit $LASTEXITCODE). Is the app running with the add-in loaded?" }
$snap = Get-Content -Raw $snapFile | ConvertFrom-Json

if ($App -eq 'revit') { $id = $snap.identity } else { $id = $snap.document }
if ($null -eq $id -and $snap.result) { if ($App -eq 'revit') { $id = $snap.result.identity } else { $id = $snap.result.document } }
if ($null -eq $id -or [string]::IsNullOrEmpty($id.path)) { throw "the snapshot has no document path: save the document first ($snapFile)" }

$liveLeaf = [System.IO.Path]::GetFileName($id.path)
if ($liveLeaf -ne $wantLeaf) { throw "the active document is $liveLeaf but $Job is for $wantLeaf: activate the right document" }

if ($App -eq 'revit') {
    if ([string]::IsNullOrEmpty($id.fingerprint)) { throw "the snapshot has no fingerprint ($snapFile)" }
    $doc = '"document": { "path": ' + (JsonString $id.path) + ', "fingerprint": ' + (JsonString $id.fingerprint) + ' }'
} else {
    $doc = '"document": { "path": ' + (JsonString $id.path) + ' }'
}
$text = [regex]::Replace($text, '"document"\s*:\s*\{[^}]*\}', { param($m) $doc }, 'Singleline')
$text = [regex]::Replace($text, '"jobId"\s*:\s*"([^"]*)"', { param($m) '"jobId": "' + $m.Groups[1].Value + '-' + $RunTag + '"' })

foreach ($k in $Set.Keys) { $text = $text.Replace('{{' + $k + '}}', [string]$Set[$k]) }
$left = [regex]::Matches($text, '\{\{([A-Z0-9_]+)\}\}') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
if ($left) { throw ("fill these placeholders with -Set: " + ($left -join ', ')) }

$out = Join-Path $OutDir ([System.IO.Path]::GetFileName($Job))
[System.IO.File]::WriteAllText($out, $text, (New-Object System.Text.UTF8Encoding($false)))
& $Sa job lint $out
if ($LASTEXITCODE -ne 0) { throw "sa job lint failed for $out" }
Write-Host "stamped: $out"
