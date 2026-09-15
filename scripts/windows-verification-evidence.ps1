<#
.SYNOPSIS
    Produces the small, portable Windows-verification artifact.

.DESCRIPTION
    The desktop probes may create raw logs, reports, screenshots, and SQLite databases on a
    hosted runner. Those are useful while the job is running but are neither necessary nor safe
    to retain as a broad artifact. This script emits an allowlisted summary and bounded,
    path-sanitized failure excerpts only.

    The job summary is written from this output rather than from the raw reports, so it can
    only describe what the published artifact actually contains.
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

$MaximumExcerptLength = 400
$MaximumExcerptLines = 60

function ConvertTo-SafeEvidenceText {
    param([AllowNull()][object] $Value)

    if ($null -eq $Value) { return $null }

    $Text = [string]$Value
    # Keep a useful diagnostic while removing Windows/Unix absolute paths and the user segment
    # that hosted runners put in those paths. The artifact must not identify the runner account.
    $Text = $Text -replace '(?i)[a-z]:\\[^\s"''`]+', '[path]'
    $Text = $Text -replace '(?<![A-Za-z0-9])/(?:[^\s"''`]|/(?!/))+', '[path]'
    $Text = $Text -replace '(?i)\\Users\\[^\\\s"''`]+', '\\Users\\[user]'
    # Bounded after sanitizing, so a cut can never leave the front half of a path behind.
    if ($Text.Length -gt $MaximumExcerptLength) {
        $Text = $Text.Substring(0, $MaximumExcerptLength) + " [truncated]"
    }
    return $Text
}

# An absent report is a step that did not get that far, and says so by being absent. A report
# that is present and unreadable is a defect in the probe that wrote it; it used to become the
# same silent null.
function Read-JsonOrNull {
    param([string] $Name)

    $Path = Join-Path $VerificationDirectory $Name
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        $Excerpts.Add("${Name}: unreadable report: $(ConvertTo-SafeEvidenceText $_.Exception.Message)")
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
$Excerpts = [System.Collections.Generic.List[string]]::new()
$Launch = Read-JsonOrNull -Name "launch-probe.json"
$Gallery = Read-JsonOrNull -Name "page-gallery.json"
$Smoke = Read-JsonOrNull -Name "windows-smoke.json"

$LaunchSummary = $null
if ($null -ne $Launch) {
    $LaunchIdentity = Get-OptionalProperty $Launch "packageIdentity"
    $LaunchData = Get-OptionalProperty $Launch "localData"
    $LaunchWindow = Get-OptionalProperty $Launch "window"
    $LaunchShutdown = Get-OptionalProperty $Launch "shutdown"
    $LaunchSummary = [ordered]@{
        success = [bool]$Launch.success
        packageIdentity = [ordered]@{
            version = $(Get-OptionalProperty $LaunchIdentity "version")
            commit = $(Get-OptionalProperty $LaunchIdentity "commit")
        }
        # A main-window handle and a live process, not semantic interactivity: nothing in the
        # probe knows whether a page has filled in.
        mainWindowAfterSeconds = $(Get-OptionalProperty $LaunchWindow "appearedAfterSeconds")
        windowReleasedAfterCloseSeconds = $(Get-OptionalProperty $LaunchShutdown "windowClosedAfterSeconds")
        exitCode = $(Get-OptionalProperty $LaunchShutdown "exitCode")
        requiredObservations = @($Launch.observations | Where-Object { $_.required } |
            ForEach-Object { [ordered]@{ name = $_.name; passed = [bool]$_.passed } })
        databaseBytes = $(Get-OptionalProperty $LaunchData "databaseBytes")
    }
    foreach ($Detail in Get-FailedDetails -Items $Launch.observations) { $Excerpts.Add("launch $Detail") }
    foreach ($Message in @($Launch.errors)) { $Excerpts.Add("launch: $(ConvertTo-SafeEvidenceText $Message)") }
}

$GallerySummary = $null
if ($null -ne $Gallery) {
    $GalleryPages = @($Gallery.pages | Where-Object { $null -ne $_ })
    $InteractionRequired = @($GalleryPages | Where-Object { [bool]$(Get-OptionalProperty $_ "interactionRequired") })
    $InteractionPassed = @($InteractionRequired | Where-Object { [bool]$(Get-OptionalProperty $_ "interactionSmoke") })
    $GallerySummary = [ordered]@{
        launchCount = $GalleryPages.Count
        failedCount = $Gallery.failedCount
        noWindowCount = $Gallery.noWindowCount
        insufficientVisualVariationCount = $Gallery.blankCount
        interfaceFaultLaunchCount = $(Get-OptionalProperty $Gallery "interfaceFaultCount")
        warningCaptureUnarmedCount = $(Get-OptionalProperty $Gallery "warningCaptureUnarmedCount")
        interactionRequiredLaunchCount = $InteractionRequired.Count
        interactionPassedLaunchCount = $InteractionPassed.Count
        interactionFailureCount = $(Get-OptionalProperty $Gallery "interactionFailureCount")
        scope = "Responsive visual variation, packaged-shell UI Automation interactions, and toolkit interface faults; semantic expected-page, accessibility, and data/tile readiness are not proven here and remain an open #279 criterion that depends on the application readiness signal owned by #281."
        pages = @($GalleryPages | ForEach-Object {
            [ordered]@{
                page = $_.page
                shellMode = $(Get-OptionalProperty $_ "shellMode")
                presented = [bool]$_.presented
                visuallyVaried = [bool]$_.visuallyVaried
                interactionRequired = [bool]$(Get-OptionalProperty $_ "interactionRequired")
                interactionSmoke = [bool]$(Get-OptionalProperty $_ "interactionSmoke")
                distinctColors = $_.distinctColors
                variedFraction = $_.variedFraction
                warningCaptureArmed = [bool]$(Get-OptionalProperty $_ "warningCaptureArmed")
                interfaceFaultCount = $(Get-OptionalProperty $_ "interfaceFaultCount")
            }
        })
    }
    foreach ($Page in $GalleryPages) {
        $Faults = @(Get-OptionalProperty $Page "interfaceFaults" | Where-Object { $null -ne $_ })
        $Armed = [bool]$(Get-OptionalProperty $Page "warningCaptureArmed")
        $InteractionRequiredForPage = [bool]$(Get-OptionalProperty $Page "interactionRequired")
        $InteractionPassedForPage = [bool]$(Get-OptionalProperty $Page "interactionSmoke")
        if (-not $Page.presented -or -not $Page.visuallyVaried -or -not $Armed -or $Faults.Count -gt 0 -or
            ($InteractionRequiredForPage -and -not $InteractionPassedForPage)) {
            $Excerpts.Add("gallery $($Page.page): $(ConvertTo-SafeEvidenceText $Page.detail)")
            if ($InteractionRequiredForPage -and -not $InteractionPassedForPage) {
                $InteractionDetail = Get-OptionalProperty $Page "interactionDetail"
                $Excerpts.Add("gallery $($Page.page) interaction: $(ConvertTo-SafeEvidenceText $InteractionDetail)")
            }
        }
        # Five per launch: enough to name the binding, few enough that one broken page cannot
        # push every other failure out of the bounded excerpt.
        foreach ($Fault in @($Faults | Select-Object -First 5)) {
            $Excerpts.Add("gallery $($Page.page) interface fault: $(ConvertTo-SafeEvidenceText $Fault)")
        }
    }
}

$SmokeSummary = $null
if ($null -ne $Smoke) {
    $SmokePersistence = Get-OptionalProperty $Smoke "persistence"
    $SmokeSummary = [ordered]@{
        success = [bool]$Smoke.success
        assertionCount = @($Smoke.assertions).Count
        passedAssertionCount = @($Smoke.assertions | Where-Object { $_.passed }).Count
        persistence = [ordered]@{
            reader = $(Get-OptionalProperty $SmokePersistence "reader")
            sqliteVersion = $(Get-OptionalProperty $SmokePersistence "sqliteVersion")
            readiness = $(Get-OptionalProperty $SmokePersistence "readiness")
            durableTable = $(Get-OptionalProperty $SmokePersistence "durableTable")
            durableEventType = $(Get-OptionalProperty $SmokePersistence "durableEventType")
            scanEventCountBefore = $(Get-OptionalProperty $SmokePersistence "scanEventCountBefore")
            scanEventCountAfter = $(Get-OptionalProperty $SmokePersistence "scanEventCountAfter")
            expectedScanEventDelta = $(Get-OptionalProperty $SmokePersistence "expectedScanEventDelta")
        }
    }
    foreach ($Detail in Get-FailedDetails -Items $Smoke.assertions) { $Excerpts.Add("smoke $Detail") }
    foreach ($Message in @($Smoke.errors)) { $Excerpts.Add("smoke: $(ConvertTo-SafeEvidenceText $Message)") }
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
$FailureText = [string]::Join([Environment]::NewLine, @($Excerpts | Select-Object -First $MaximumExcerptLines))
Set-Content -LiteralPath (Join-Path $OutputDirectory "windows-verification-failures.txt") -Value $FailureText -Encoding utf8
