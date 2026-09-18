<#
.SYNOPSIS
    Static and fixture regression checks for the Windows-verification policy.

.DESCRIPTION
    Runs twice in the workflow. First, before anything expensive, it checks the policy text,
    the page gallery's interface-fault classifier, and the evidence sanitizer against fixtures.
    Then, once packages are restored and given -SqliteLibraryPath, it also drives the developer
    smoke's SQLite reader against a fixture database through the restored copy of the win-x64
    native library the package ships.

    The functions under test are lifted out of the scripts that use them through the parser
    rather than copied here, so what passes is the code that runs later in the job.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $RepositoryRoot,

    [string] $SqliteLibraryPath = ""
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

# Both must be present. The ordering check this replaces compared two IndexOf results directly,
# so a missing first marker (-1) read as "in order" and passed.
function Require-Order {
    param([string] $Text, [string] $First, [string] $Second, [string] $Description)
    $FirstIndex = $Text.IndexOf($First, [StringComparison]::Ordinal)
    $SecondIndex = $Text.IndexOf($Second, [StringComparison]::Ordinal)
    if ($FirstIndex -lt 0 -or $SecondIndex -lt 0) { throw "Missing a marker for $Description." }
    if ($FirstIndex -gt $SecondIndex) { throw "Out of order: $Description." }
}

function Get-ScriptAst {
    param([string] $Path)
    $Tokens = $null
    $ParseErrors = $null
    $Ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref] $Tokens, [ref] $ParseErrors)
    if (@($ParseErrors).Count -gt 0) { throw "PowerShell parse errors in $(Split-Path -Leaf $Path)." }
    return $Ast
}

function Get-FunctionText {
    param([System.Management.Automation.Language.Ast] $Ast, [string] $Name)
    $Definition = $Ast.Find({
        param($Node)
        $Node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $Node.Name -eq $Name
    }, $true)
    if ($null -eq $Definition) { throw "Missing function $Name." }
    return $Definition.Extent.Text
}

# Returns the exception a block raised, and fails when it raised none.
function Get-RaisedException {
    param([scriptblock] $Action, [string] $Description)
    try {
        & $Action | Out-Null
    }
    catch {
        return $_.Exception
    }
    throw "Expected a failure: $Description."
}

$WorkflowPath = Join-Path $RepositoryRoot ".github/workflows/windows-verify.yml"
$ProbePath = Join-Path $RepositoryRoot "scripts/windows-launch-probe.ps1"
$GalleryPath = Join-Path $RepositoryRoot "scripts/windows-page-gallery.ps1"
$SmokePath = Join-Path $RepositoryRoot "scripts/windows-smoke.ps1"
$Workflow = Get-Content -LiteralPath $WorkflowPath -Raw
$Probe = Get-Content -LiteralPath $ProbePath -Raw
$Gallery = Get-Content -LiteralPath $GalleryPath -Raw
$Smoke = Get-Content -LiteralPath $SmokePath -Raw

# Package identity, and launch success decided only once persistence has been observed.
Require-Text $Probe '[string] $ExpectedCommit' 'expected GitHub commit parameter'
Require-Text $Probe 'Expected package commit' 'commit identity failure'
Require-Order $Probe 'Add-Observation -Name "database-created"' '$Success = $Errors.Count -eq 0' 'launch success finalized after the durable persistence observations'
Require-Text $Workflow '-ExpectedCommit "${{ github.sha }}"' 'expected GitHub SHA launch check'
Require-Text $Workflow 'Extracted package metadata does not match the expected GitHub version and commit.' 'extracted package identity check'
Require-Text $Workflow 'Installed package metadata does not match the expected GitHub version and commit.' 'installed package identity check'

# Artifact policy, and a job summary that claims only what the artifact holds.
Require-Text $Workflow 'windows-verification-summary.json' 'allowlisted sanitized summary upload'
Require-Text $Workflow 'windows-verification-failures.txt' 'allowlisted failure excerpt upload'
Require-Text $Workflow 'retention-days: 7' 'short artifact retention'
Forbid-Text $Workflow 'verification/**' 'broad verification upload glob'
Forbid-Text $Workflow 'userName =' 'runner username collection'
Forbid-Text $Workflow 'verification/startup.log' 'raw startup-log artifact'
Forbid-Text $Workflow 'verification/tarkov-companion.db' 'SQLite artifact'
Require-Text $Workflow '$EvidencePath = Join-Path $env:GITHUB_WORKSPACE "verification/evidence/windows-verification-summary.json"' 'job summary read from the sanitized evidence'
Forbid-Text $Workflow 'page gallery, cropped launch probe' 'job summary promising images the artifact does not contain'
Forbid-Text $Workflow 'Time to interactive' 'time-to-interactive claim for a window-handle timing'
Forbid-Text $Workflow 'Simulator state assertions' 'job summary presenting diagnostic-channel assertions as simulator ingestion'
Require-Text $Workflow '-SqliteLibraryPath' 'SQLite reader fixture check'
# DemoRaidReplayReport has no success property, so reading one printed a blank on every run.
Forbid-Text $Workflow '$DemoReplay.success' 'demo-replay line reading a field the report does not have'
Require-Text $Workflow '$DemoReplay.complete' 'demo-replay line reading the field its exit code follows'

# The page gallery's interface-fault gate.
Require-Text $Gallery '$env:TARKOV_COMPANION_UI_WARNING_LOG = $WarningLog' 'per-launch UI warning capture'
Require-Text $Gallery 'warning capture was not armed' 'unarmed-capture failure'
Require-Text $Gallery 'interface faults: ' 'interface-fault failure'
Require-Text $Gallery 'UIAutomationClient' 'packaged-shell UI Automation client'
Require-Text $Gallery '[System.Windows.Automation.InvokePattern]::Pattern' 'real packaged-shell Invoke interaction'
Require-Text $Gallery '"--ui-shell", "legacy"' 'explicit legacy shell launch'
Require-Text $Gallery '"--ui-shell", "v2-a"' 'Variant A shell launch'
Require-Text $Gallery '"--ui-shell", "v2-b"' 'Variant B shell launch'
Require-Text $Gallery 'shell-v2-a-narrow' 'narrow Variant A capture'
Require-Text $Gallery 'shell-v2-a-stale-focus' 'stale-focus fallback launch'
Require-Text $Gallery 'shell-v2-a-tablet-link' 'Variant A tablet deep-link launch'
Require-Text $Gallery 'shell-v2-b-tablet-link' 'Variant B tablet deep-link launch'
Require-Text $Gallery 'shell-v2-reset-close' 'reset-close lifecycle launch'
Require-Text $Gallery 'map-renderer-wide' 'wide packaged map-renderer launch'
Require-Text $Gallery 'map-renderer-320dip-large-text' '320 DIP large-text map-renderer launch'
Require-Text $Gallery '"--map-renderer-gallery", "--map-renderer-large-text"' 'large-text map-renderer arguments'
Require-Text $Gallery 'v2-map-object-cluster-3-2-1176be92' 'dense map cluster UIA interaction'
Require-Text $Gallery 'v2-map-loot-preset' 'high-value loot preset UIA interaction'
Require-Text $Gallery 'v2-map-loot-filter-tier-exceptional-71739f81' 'high-value loot tier-filter UIA interaction'
Require-Text $Gallery 'v2-map-loot-row-map-only-cache-9594a087' 'list-only loot-details UIA interaction'
Require-Text $Gallery 'v2-map-loot-selection-live' 'typed loot selection live-region assertion'
Require-Text $Gallery 'map-renderer-loot-offline' 'offline loot-layer packaged launch'
Require-Text $Gallery '"--map-renderer-gallery", "--map-renderer-loot-offline"' 'offline loot-layer arguments'
Require-Text $Gallery 'No potential spawns match these filters' 'empty loot-filter state assertion'
Require-Text $Gallery 'includeOffscreen = $true' 'offscreen responsive loot-control assertion'
Require-Text $Gallery 'v2-map-layer-estimates-d4fc996e' 'loot preset hides an unrelated optional layer'
Require-Text $Gallery 'expectedBounds' 'packaged touch-target bounds assertion'
Require-Text $Gallery 'expectedOutsideViewportAutomationIds' 'responsive outside-viewport map-content assertion'
Require-Text $Gallery 'Wait-AutomationOutsideViewportElement' 'strict outside-viewport element wait'
Require-Text $Gallery '$Element.Current.IsOffscreen -or -not $IntersectsViewport' 'UI Automation viewport-state assertion'
Require-Text $Gallery 'outside the packaged window viewport' 'useful outside-viewport failure'
Require-Text $Gallery 'Wait-AutomationNamePattern' 'polling UI Automation name-state assertion'
Require-Text $Gallery 'TogglePattern' 'typed toggle-control UI Automation interaction'
Require-Text $Gallery 'expectedFocusAutomationId' 'packaged keyboard-focus assertion'
Require-Text $Gallery 'expectedDialogName' 'named dialog-peer assertion'
Require-Text $Gallery 'expectedCurrentAutomationIds' 'current-destination UIA assertion'
Require-Text $Gallery 'gracefulShutdown' 'graceful packaged shutdown assertion'
Require-Text $Gallery 'packaged-shell interaction failed:' 'interaction failure gate'
Forbid-Text $Gallery 'Kill($true)' '.NET Core-only process-tree Kill overload under Windows PowerShell'
# V2 rough package 30: the acceptance sweep, its measured bounds, and the captures it publishes.
Require-Text $Gallery 'v2-a-$($Route.key)-$($Size.suffix)' 'V2 acceptance route captures'
Require-Text $Gallery '$V2AcceptanceRoutes' 'the Variant A route table the sweep photographs'
Require-Text $Gallery 'Measure-DeadSpace' 'measured dead-area function'
Require-Text $Gallery 'maximumEdgeDeadFraction' 'right-edge dead-area bound'
Require-Text $Gallery 'maximumFlatBandFraction' 'widest-flat-band bound'
Require-Text $Gallery 'too much of the window is empty' 'dead-area failure gate'
Require-Text $Gallery 'insideWindow' 'control-inside-the-window bounds assertion'
Require-Text $Gallery 'outside the window: ' 'useful clipped-control failure'
Require-Text $Gallery 'Test-DesktopFits' 'desktop-size check for the ultrawide captures'
Forbid-Text $Gallery 'Set-DisplayResolution -Width $Width' 'a display-mode change between launches'
Require-Text $Gallery 'Resolve-FocusTarget' 'a seeded preview address that the store will accept'
Require-Text $Gallery 'Skipped: a $($Shot.width)x$($Shot.height) window needs a desktop' 'recorded skip rather than a cropped photograph'
# V2 rough package 32: the 1920x1080 bounds, measured inside the control that was repaired.
Require-Text $Gallery 'Measure-RegionFill' 'element-scoped flat-area measurement'
Require-Text $Gallery 'expectedFill' 'element fill assertion'
Require-Text $Gallery 'as one flat colour' 'useful empty-control failure'
Require-Text $Gallery '$Size.width -eq 1920' 'the fill bounds applying at the window the companion runs in'
Require-Text $Gallery 'v2-debrief-history' 'the Debrief history pane bound'
Require-Text $Gallery 'maximumHeightFraction' 'a bound on a control being the size of what it holds'
Require-Text $Gallery 'unlessAutomationId' 'a fill bound that stands down when the content is missing'
Require-Text $Gallery 'it is not the size of what it holds' 'useful oversized-control failure'
Require-Text $Workflow 'name: v2-route-gallery' 'V2 route capture artifact'
Require-Text $Workflow 'verification/pages/v2-a-*.png' 'narrow V2 capture upload glob'
Require-Text $Workflow '!verification/pages/v2-a-plan-events-*.png' 'Events capture excluded from the upload'
Forbid-Text $Workflow 'verification/pages/*.png' 'whole-screenshot-directory upload'
Require-Text $Gallery 'full usability/accessibility and data/tile readiness are not proven' 'gallery scope boundary'
Require-Text $Gallery 'application readiness signal owned by #281' 'residual #281 readiness ownership'

# The developer smoke's durable-state assertion.
Require-Text $Smoke "SELECT COUNT(*) FROM raid_events WHERE type = 'scan';" 'specific durable scan-row query'
Require-Text $Smoke 'expectedScanEventDelta' 'durable scan-row report'
Require-Text $Smoke "julianday(start_utc) >= julianday('{0}')" 'raid-opened-by-this-launch readiness query'
Forbid-Text $Smoke 'Get-DirectoryFingerprint' 'directory-mtime persistence assertion'
Forbid-Text $Smoke '[Microsoft.Data.Sqlite.SqliteConnection]' 'net10.0 SQLite provider that Windows PowerShell 5.1 cannot load'
Forbid-Text $Smoke 'Assembly]::LoadFrom' 'managed provider load under Windows PowerShell 5.1'
Require-Order $Smoke 'Add-Assertion -Name "demo-raid-open"' '$ScanEventsBefore = Get-SqliteScalar' 'scan baseline taken only after the demo raid is open'
Require-Order $Smoke 'Add-Assertion -Name ("scan-row-committed-"' 'Add-Assertion -Name "scan-persisted"' 'each durable scan row awaited before the delta is asserted'

# Interface-fault classification, through the gallery's own function and default pattern. The
# previous gate's pattern never matched "[Binding]"; this is the check that would have said so.
$GalleryAst = Get-ScriptAst -Path $GalleryPath
. ([ScriptBlock]::Create((Get-FunctionText -Ast $GalleryAst -Name "Get-InterfaceFaultLines")))
$PatternParameter = @($GalleryAst.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq "FailOnWarningPattern" })
if ($PatternParameter.Count -ne 1 -or
    -not ($PatternParameter[0].DefaultValue -is [System.Management.Automation.Language.StringConstantExpressionAst])) {
    throw "The gallery's FailOnWarningPattern must default to a constant string."
}
$FailPattern = $PatternParameter[0].DefaultValue.Value
$TraceLines = [string[]]@(
    "[Information] TarkovCompanion.Application.Services.Runtime.ApplicationStartupCoordinator: The windows-media-ocr recogniser is available. No reason was reported.",
    "[Warning] TarkovCompanion.Infrastructure.Sync: Could not find a cached catalog; it will be downloaded.",
    "[Binding] An error occurred binding 'Text' to 'Summary.Title' at 'Summary': 'Value is null.' (TextBlock #7)",
    # Avalonia 12's StringLogSink writes "[Area] " from one overload and "[Area]" from the other.
    "[Property]Error in binding to 'Avalonia.Controls.TextBlock'.'Foreground': 'Unable to convert object of type 'System.String'.' (TextBlock #2)",
    "[Win32Platform] Unable to resolve the requested composition mode.",
    "[Win32Platform] Composition fell back to software rendering.",
    "System.InvalidOperationException: Could not find the requested resource.",
    "   at TarkovCompanion.App.Services.Example.Run()"
)
$ExpectedFaults = @($TraceLines[2], $TraceLines[3], $TraceLines[4])
$ObservedFaults = Get-InterfaceFaultLines -Lines $TraceLines -Pattern $FailPattern
if (($ObservedFaults -join "`n") -cne ($ExpectedFaults -join "`n")) {
    throw "Interface-fault classification changed. Observed: $($ObservedFaults -join ' | ')"
}
if ((Get-InterfaceFaultLines -Lines ([string[]]@()) -Pattern $FailPattern).Count -ne 0) {
    throw "An empty trace must classify as no interface faults."
}

# Evidence sanitizer, against fixtures carrying runner paths and an over-long fault line.
$FixtureRoot = Join-Path $RepositoryRoot "tests/fixtures/windows-verification-evidence"
$TemporaryOutput = Join-Path ([System.IO.Path]::GetTempPath()) ("tarkov-windows-evidence-" + [Guid]::NewGuid().ToString("N"))
try {
    # A script invoked with & reports failure by throwing; it sets no exit code unless it calls
    # exit. Reading $LASTEXITCODE here under StrictMode was what failed the job at ff8f1d5.
    try {
        & (Join-Path $RepositoryRoot "scripts/windows-verification-evidence.ps1") `
            -VerificationDirectory $FixtureRoot `
            -OutputDirectory $TemporaryOutput
    }
    catch {
        throw "Evidence sanitizer fixture failed: $($_.Exception.Message)"
    }

    $SummaryText = Get-Content -LiteralPath (Join-Path $TemporaryOutput "windows-verification-summary.json") -Raw
    $FailureText = Get-Content -LiteralPath (Join-Path $TemporaryOutput "windows-verification-failures.txt") -Raw
    if (($SummaryText + $FailureText) -match '(?i)[a-z]:\\|\\Users\\build-user|build-user') {
        throw 'Evidence sanitizer fixture leaked an absolute path or runner username.'
    }

    $Evidence = $SummaryText | ConvertFrom-Json
    if ($Evidence.launch.mainWindowAfterSeconds -ne 2.5 -or $Evidence.launch.exitCode -ne 0) {
        throw 'Evidence summary lost the launch timings the job summary reads.'
    }
    if ($Evidence.gallery.interfaceFaultLaunchCount -ne 1 -or -not $Evidence.gallery.pages[0].warningCaptureArmed) {
        throw 'Evidence summary lost the gallery interface-fault gate.'
    }
    if ($Evidence.gallery.interactionRequiredLaunchCount -ne 0 -or $Evidence.gallery.interactionPassedLaunchCount -ne 0) {
        throw 'Evidence summary invented packaged-shell interactions for an older fixture.'
    }
    if ($Evidence.smoke.persistence.sqliteVersion -cne '3.53.3') {
        throw 'Evidence summary lost the smoke SQLite reader identity.'
    }
    if ($FailureText -notmatch 'gallery Raid interface fault: \[Binding\]' -or $FailureText -notmatch '\[truncated\]') {
        throw 'Evidence excerpts lost the bounded interface-fault line.'
    }
    if (@($FailureText -split "`r?`n" | Where-Object { $_.Length -gt 480 }).Count -gt 0) {
        throw 'Evidence excerpts contain an unbounded line.'
    }
}
finally {
    if (Test-Path -LiteralPath $TemporaryOutput) { Remove-Item -LiteralPath $TemporaryOutput -Recurse -Force }
}

if ([string]::IsNullOrWhiteSpace($SqliteLibraryPath)) {
    Write-Host "Windows verification policy, interface-fault classification and evidence fixtures passed."
    return
}

# The smoke's SQLite reader, under this Windows PowerShell, against a WAL database held open by
# a writer - the state it reads the running application's database in.
$SmokeAst = Get-ScriptAst -Path $SmokePath
foreach ($FunctionName in @("Initialize-SqliteReader", "Get-SqliteScalar", "Get-SqliteFailure", "Test-TransientSqliteFailure", "Wait-SqliteCondition")) {
    . ([ScriptBlock]::Create((Get-FunctionText -Ast $SmokeAst -Name $FunctionName)))
}

$SqliteVersion = Initialize-SqliteReader -LibraryPath $SqliteLibraryPath
if ([string]::IsNullOrWhiteSpace($SqliteVersion)) { throw "The SQLite reader loaded but reported no version." }

# Fixture setup only. Bound by name to the module Initialize-SqliteReader has already loaded.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class TarkovCompanionSqliteFixtureWriter
{
    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr database, int flags, IntPtr vfs);

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr database, byte[] sql, IntPtr callback, IntPtr argument, IntPtr error);

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_errmsg(IntPtr database);

    [DllImport("e_sqlite3", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(IntPtr database);

    public static IntPtr Open(string path)
    {
        IntPtr database;
        int code = sqlite3_open_v2(Encoding.UTF8.GetBytes(path + "\0"), out database, 0x2 | 0x4, IntPtr.Zero);
        if (code != 0)
        {
            if (database != IntPtr.Zero)
            {
                sqlite3_close_v2(database);
            }

            throw new InvalidOperationException("Fixture database open failed with result code " + code + ".");
        }

        return database;
    }

    public static void Execute(IntPtr database, string sql)
    {
        int code = sqlite3_exec(database, Encoding.UTF8.GetBytes(sql + "\0"), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (code != 0)
        {
            throw new InvalidOperationException(
                "Fixture statement failed with result code " + code + ": " + Marshal.PtrToStringAnsi(sqlite3_errmsg(database)));
        }
    }

    public static void Close(IntPtr database)
    {
        sqlite3_close_v2(database);
    }
}
'@

$SqliteFixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tarkov-sqlite-reader-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $SqliteFixtureRoot -Force | Out-Null
$Writer = [IntPtr]::Zero
try {
    $ScanQuery = "SELECT COUNT(*) FROM raid_events WHERE type = 'scan';"
    $FixtureDatabase = Join-Path $SqliteFixtureRoot "fixture.db"
    $Writer = [TarkovCompanionSqliteFixtureWriter]::Open($FixtureDatabase)
    [TarkovCompanionSqliteFixtureWriter]::Execute($Writer, @"
PRAGMA journal_mode = WAL;
CREATE TABLE schema_migrations(version INTEGER NOT NULL);
INSERT INTO schema_migrations VALUES (1);
CREATE TABLE raids(id TEXT PRIMARY KEY, start_utc TEXT);
INSERT INTO raids VALUES ('r1', '2026-09-15T00:56:20.1234567+00:00');
CREATE TABLE raid_events(id INTEGER PRIMARY KEY AUTOINCREMENT, raid_id TEXT NOT NULL, type TEXT NOT NULL);
INSERT INTO raid_events(raid_id, type) VALUES ('r1', 'scan'), ('r1', 'scan'), ('r1', 'position');
"@)

    if ((Get-SqliteScalar -DatabasePath $FixtureDatabase -CommandText $ScanQuery) -ne 2) {
        throw "The SQLite reader miscounted committed scan rows."
    }

    # A row committed by the open writer after the first read is visible to the next read.
    [TarkovCompanionSqliteFixtureWriter]::Execute($Writer, "INSERT INTO raid_events(raid_id, type) VALUES ('r1', 'scan');")
    if ((Wait-SqliteCondition -DatabasePath $FixtureDatabase -CommandText $ScanQuery -Minimum 3 -Description "fixture scan rows" -TimeoutSeconds 5) -ne 3) {
        throw "The SQLite reader did not observe a row committed by a concurrent writer."
    }

    # The smoke's raid readiness compares the application's round-trip ("O") timestamps.
    $RaidQuery = "SELECT COUNT(*) FROM raids WHERE start_utc IS NOT NULL AND julianday(start_utc) >= julianday('{0}');"
    if ((Get-SqliteScalar -DatabasePath $FixtureDatabase -CommandText ($RaidQuery -f "2026-09-15T00:56:19.000Z")) -ne 1 -or
        (Get-SqliteScalar -DatabasePath $FixtureDatabase -CommandText ($RaidQuery -f "2026-09-15T00:56:21.000Z")) -ne 0) {
        throw "Raid readiness does not order the application's round-trip timestamps."
    }

    # Read-only: a write through the reader is refused, and nothing changed.
    $ReadOnly = Get-SqliteFailure -Exception (Get-RaisedException -Description "a write through the read-only reader" -Action {
        Get-SqliteScalar -DatabasePath $FixtureDatabase -CommandText "INSERT INTO raid_events(raid_id, type) VALUES ('r1', 'scan') RETURNING id;"
    })
    if ($ReadOnly.code -ne 8 -or (Get-SqliteScalar -DatabasePath $FixtureDatabase -CommandText $ScanQuery) -ne 3) {
        throw "The SQLite reader is not read-only (result code $($ReadOnly.code))."
    }

    # A table that is not there yet is surfaced with its code, and classified as initialization.
    $MissingTable = Get-SqliteFailure -Exception (Get-RaisedException -Description "a query against a missing table" -Action {
        Get-SqliteScalar -DatabasePath $FixtureDatabase -CommandText "SELECT COUNT(*) FROM not_migrated_yet;"
    })
    if ($MissingTable.code -ne 1 -or -not (Test-TransientSqliteFailure -Failure $MissingTable)) {
        throw "A missing table was not reported as a transient SQLite failure."
    }

    # A wait that never becomes ready says what it last saw rather than only that it timed out.
    $TimedOut = Get-RaisedException -Description "a readiness wait on a missing table" -Action {
        Wait-SqliteCondition -DatabasePath $FixtureDatabase -CommandText "SELECT COUNT(*) FROM not_migrated_yet;" -Minimum 1 -Description "a fixture table" -TimeoutSeconds 1
    }
    if ($TimedOut.Message -notmatch 'Timed out' -or $TimedOut.Message -notmatch 'no such table') {
        throw "A readiness timeout hid the last SQLite error: $($TimedOut.Message)"
    }

    # An absent database is waited for and never created by the reader.
    $AbsentDatabase = Join-Path $SqliteFixtureRoot "absent.db"
    $Absent = Get-RaisedException -Description "a readiness wait on an absent database" -Action {
        Wait-SqliteCondition -DatabasePath $AbsentDatabase -CommandText $ScanQuery -Minimum 1 -Description "an absent database" -TimeoutSeconds 1
    }
    if ($Absent.Message -notmatch 'did not exist' -or (Test-Path -LiteralPath $AbsentDatabase)) {
        throw "A readiness wait on an absent database misreported or created it: $($Absent.Message)"
    }

    # A defect is raised at once, not retried until the deadline. This is the failure the old
    # reader swallowed for ninety seconds on every run.
    $NotDatabase = Join-Path $SqliteFixtureRoot "not-a-database.db"
    Set-Content -LiteralPath $NotDatabase -Value ("this is not a SQLite database " * 20) -Encoding ascii
    $Watch = [System.Diagnostics.Stopwatch]::StartNew()
    $Defect = Get-RaisedException -Description "a readiness wait on a file that is not a database" -Action {
        Wait-SqliteCondition -DatabasePath $NotDatabase -CommandText $ScanQuery -Minimum 1 -Description "a corrupt fixture" -TimeoutSeconds 30
    }
    $Watch.Stop()
    if ($Defect.Message -match 'Timed out' -or $Watch.Elapsed.TotalSeconds -gt 10) {
        throw "A non-transient SQLite failure was retried instead of surfaced: $($Defect.Message)"
    }

    Write-Host "SQLite reader passed against the packaged engine $SqliteVersion under PowerShell $($PSVersionTable.PSVersion)."
}
finally {
    if ($Writer -ne [IntPtr]::Zero) { [TarkovCompanionSqliteFixtureWriter]::Close($Writer) }
    Remove-Item -LiteralPath $SqliteFixtureRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Windows verification policy, interface-fault classification, evidence fixtures and SQLite reader passed."
