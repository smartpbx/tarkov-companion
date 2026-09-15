<#
.SYNOPSIS
    Produces the small, portable Windows-verification artifact.

.DESCRIPTION
    The desktop probes may create raw logs, reports, screenshots, and SQLite databases on a
    hosted runner. Those are useful while the job is running but are neither necessary nor safe
    to retain as a broad artifact. This script emits an allowlisted summary and bounded,
    path-sanitized failure excerpts only.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $VerificationDirectory,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [string] $LocalDataRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function ConvertTo-SafeEvidenceText {
    param([AllowNull()][object] $Value)

    if ($null -eq $Value) { return $null }

    $Text = [string]$Value
    # Keep a useful diagnostic while removing Windows/Unix absolute paths and the user segment
    # that hosted runners put in those paths. The artifact must not identify the runner account.
    $Text = $Text -replace '(?i)[a-z]:\\[^\s"''`]+', '[path]'
    $Text = $Text -replace '(?<![A-Za-z0-9])/(?:[^\s"''`]|/(?!/))+', '[path]'
    $Text = $Text -replace '(?i)\\Users\\[^\\\s"''`]+', '\\Users\\[user]'
    return $Text
}

function Read-JsonOrNull {
    param([string] $Name)

    $Path = Join-Path $VerificationDirectory $Name
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        return $null
    }
}

function Get-OptionalProperty {
    param([AllowNull()][object] $Object, [string] $Name)

    if ($null -eq $Object) { return $null }
    $Property = $Object.PSObject.Properties[$Name]
    if ($null -eq $Property) { return $null }
    return $Property.Value
}

function Get-FailedDetails {
    param([AllowNull()][object[]] $Items, [string] $NameProperty = "name")

    $Details = [System.Collections.Generic.List[string]]::new()
    foreach ($Item in @($Items)) {
        if ($null -eq $Item) { continue }
        $Passed = $Item.passed
        if ($null -ne $Passed -and -not [bool]$Passed) {
            $Name = $Item.$NameProperty
            $Details.Add("${Name}: $(ConvertTo-SafeEvidenceText $Item.detail)")
        }
    }
    return $Details.ToArray()
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$Launch = Read-JsonOrNull -Name "launch-probe.json"
$Gallery = Read-JsonOrNull -Name "page-gallery.json"
$Smoke = Read-JsonOrNull -Name "windows-smoke.json"
$Excerpts = [System.Collections.Generic.List[string]]::new()

$LaunchSummary = $null
if ($null -ne $Launch) {
    $LaunchIdentity = $Launch.packageIdentity
    $LaunchData = $Launch.localData
    $LaunchSummary = [ordered]@{
        success = [bool]$Launch.success
        packageIdentity = [ordered]@{
            version = if ($null -eq $LaunchIdentity) { $null } else { $LaunchIdentity.version }
            commit = if ($null -eq $LaunchIdentity) { $null } else { $LaunchIdentity.commit }
        }
        requiredObservations = @($Launch.observations | Where-Object { $_.required } |
            ForEach-Object { [ordered]@{ name = $_.name; passed = [bool]$_.passed } })
        databaseBytes = if ($null -eq $LaunchData) { $null } else { $LaunchData.databaseBytes }
    }
    foreach ($Detail in Get-FailedDetails -Items $Launch.observations) { $Excerpts.Add($Detail) }
    foreach ($Error in @($Launch.errors)) { $Excerpts.Add("launch: $(ConvertTo-SafeEvidenceText $Error)") }
}

$GallerySummary = $null
if ($null -ne $Gallery) {
    $GallerySummary = [ordered]@{
        failedCount = $Gallery.failedCount
        noWindowCount = $Gallery.noWindowCount
        insufficientVisualVariationCount = $Gallery.blankCount
        scope = "Responsive visual variation only; semantic/accessibility expected-page and data/tile readiness are deferred to #279 integration owned by #281."
        pages = @($Gallery.pages | ForEach-Object {
            [ordered]@{
                page = $_.page
                presented = [bool]$_.presented
                visuallyVaried = [bool]$_.visuallyVaried
                distinctColors = $_.distinctColors
                variedFraction = $_.variedFraction
            }
        })
    }
    foreach ($Page in @($Gallery.pages | Where-Object { -not $_.presented -or -not $_.visuallyVaried })) {
        $Excerpts.Add("gallery $($Page.page): $(ConvertTo-SafeEvidenceText $Page.detail)")
    }
}

$SmokeSummary = $null
if ($null -ne $Smoke) {
    $SmokePersistence = $Smoke.persistence
    $SmokeSummary = [ordered]@{
        success = [bool]$Smoke.success
        assertionCount = @($Smoke.assertions).Count
        passedAssertionCount = @($Smoke.assertions | Where-Object { $_.passed }).Count
        persistence = [ordered]@{
            durableTable = $(Get-OptionalProperty $SmokePersistence "durableTable")
            durableEventType = $(Get-OptionalProperty $SmokePersistence "durableEventType")
            scanEventCountBefore = $(Get-OptionalProperty $SmokePersistence "scanEventCountBefore")
            scanEventCountAfter = $(Get-OptionalProperty $SmokePersistence "scanEventCountAfter")
            expectedScanEventDelta = $(Get-OptionalProperty $SmokePersistence "expectedScanEventDelta")
        }
    }
    foreach ($Detail in Get-FailedDetails -Items $Smoke.assertions) { $Excerpts.Add("smoke $Detail") }
    foreach ($Error in @($Smoke.errors)) { $Excerpts.Add("smoke: $(ConvertTo-SafeEvidenceText $Error)") }
}

# Startup logs are never uploaded. If a failed run wrote one, retain at most twenty sanitized
# tail lines so the artifact explains a failure without retaining a raw runner log.
if ($Excerpts.Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($LocalDataRoot)) {
    $StartupLog = Join-Path $LocalDataRoot "Logs\\startup.log"
    if (Test-Path -LiteralPath $StartupLog) {
        foreach ($Line in @(Get-Content -LiteralPath $StartupLog -Tail 20 -ErrorAction SilentlyContinue)) {
            $Excerpts.Add("startup: $(ConvertTo-SafeEvidenceText $Line)")
        }
    }
}

$Summary = [ordered]@{
    schemaVersion = 1
    generatedUtc = [DateTimeOffset]::UtcNow.ToString("O")
    artifactPolicy = "Sanitized summary and bounded failure excerpts only; no raw startup logs, SQLite databases, screenshots, or broad directory uploads."
    launch = $LaunchSummary
    gallery = $GallerySummary
    smoke = $SmokeSummary
}

$Summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory "windows-verification-summary.json") -Encoding utf8
$FailureText = [string]::Join([Environment]::NewLine, @($Excerpts | Select-Object -First 60))
Set-Content -LiteralPath (Join-Path $OutputDirectory "windows-verification-failures.txt") -Value $FailureText -Encoding utf8
