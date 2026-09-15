<#
.SYNOPSIS
    Static and fixture regression checks for the Windows-verification policy.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Require-Text {
    param([string] $Text, [string] $Needle, [string] $Description)
    if ($Text.IndexOf($Needle, [StringComparison]::Ordinal) -lt 0) { throw "Missing $Description." }
}

function Forbid-Text {
    param([string] $Text, [string] $Needle, [string] $Description)
    if ($Text.IndexOf($Needle, [StringComparison]::Ordinal) -ge 0) { throw "Forbidden $Description." }
}

$Workflow = Get-Content -LiteralPath (Join-Path $RepositoryRoot ".github/workflows/windows-verify.yml") -Raw
$Probe = Get-Content -LiteralPath (Join-Path $RepositoryRoot "scripts/windows-launch-probe.ps1") -Raw
$Gallery = Get-Content -LiteralPath (Join-Path $RepositoryRoot "scripts/windows-page-gallery.ps1") -Raw
$Smoke = Get-Content -LiteralPath (Join-Path $RepositoryRoot "scripts/windows-smoke.ps1") -Raw

Require-Text $Probe '[string] $ExpectedCommit' 'expected GitHub commit parameter'
Require-Text $Probe 'Expected package commit' 'commit identity failure'
if ($Probe.IndexOf('Add-Observation -Name "database-created"', [StringComparison]::Ordinal) -gt
    $Probe.IndexOf('$Success = $Errors.Count -eq 0', [StringComparison]::Ordinal)) {
    throw 'Launch success is calculated before durable persistence observations.'
}
Require-Text $Workflow '-ExpectedCommit "${{ github.sha }}"' 'expected GitHub SHA launch check'
Require-Text $Workflow 'Extracted package metadata does not match the expected GitHub version and commit.' 'extracted package identity check'
Require-Text $Workflow 'Installed package metadata does not match the expected GitHub version and commit.' 'installed package identity check'
Require-Text $Workflow 'windows-verification-summary.json' 'allowlisted sanitized summary upload'
Require-Text $Workflow 'windows-verification-failures.txt' 'allowlisted failure excerpt upload'
Require-Text $Workflow 'retention-days: 7' 'short artifact retention'
Forbid-Text $Workflow 'verification/**' 'broad verification upload glob'
Forbid-Text $Workflow 'userName =' 'runner username collection'
Forbid-Text $Workflow 'verification/startup.log' 'raw startup-log artifact'
Forbid-Text $Workflow 'verification/tarkov-companion.db' 'SQLite artifact'
Require-Text $Gallery 'semantic expected-page, accessibility, and data/tile readiness' 'gallery scope boundary'
Require-Text $Gallery 'deferred to #281' 'open #281 integration ownership'
Require-Text $Smoke "SELECT COUNT(*) FROM raid_events WHERE type = 'scan';" 'specific durable scan-row query'
Require-Text $Smoke 'expectedScanEventDelta' 'durable scan-row report'
Forbid-Text $Smoke 'Get-DirectoryFingerprint' 'directory-mtime persistence assertion'

$FixtureRoot = Join-Path $RepositoryRoot "tests/fixtures/windows-verification-evidence"
$TemporaryOutput = Join-Path ([System.IO.Path]::GetTempPath()) ("tarkov-windows-evidence-" + [Guid]::NewGuid().ToString("N"))
try {
    & (Join-Path $RepositoryRoot "scripts/windows-verification-evidence.ps1") `
        -VerificationDirectory $FixtureRoot `
        -OutputDirectory $TemporaryOutput
    if ($LASTEXITCODE -ne 0) { throw 'Evidence sanitizer fixture failed.' }
    $ArtifactText = Get-Content -LiteralPath (Join-Path $TemporaryOutput "windows-verification-summary.json") -Raw
    $ArtifactText += Get-Content -LiteralPath (Join-Path $TemporaryOutput "windows-verification-failures.txt") -Raw
    if ($ArtifactText -match '(?i)[a-z]:\\|\\Users\\build-user') {
        throw 'Evidence sanitizer fixture leaked an absolute path or runner username.'
    }
}
finally {
    if (Test-Path -LiteralPath $TemporaryOutput) { Remove-Item -LiteralPath $TemporaryOutput -Recurse -Force }
}
