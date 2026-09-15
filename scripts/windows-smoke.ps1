[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $AppPath,

    [Parameter(Mandatory = $true)]
    [string] $SimulatorPath,

    [string] $OutputPath = (Join-Path $PWD "windows-smoke-report.json"),

    [string] $WorkRoot = (Join-Path $env:TEMP ("tarkov-companion-smoke-" + [Guid]::NewGuid().ToString("N")))
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

[string[]] $Scenarios = @(
    "RaidStart_Customs",
    "Inspect_GraphicsCard",
    "Inspect_AmmoPack",
    "Inspect_Key",
    "Inspect_Consumable",
    "ExtractList_Customs",
    "Container_Mixed",
    "Flea_VisibleListings",
    "PositionUpdate",
    "RaidEnd"
)

$Assertions = [System.Collections.Generic.List[object]]::new()
$Errors = [System.Collections.Generic.List[string]]::new()
$StartedProcesses = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$PreviousDiagnosticToken = $env:TARKOV_COMPANION_DIAGNOSTIC_TOKEN
$PreviousOffline = $env:TARKOV_COMPANION_OFFLINE
$DemoDataRoot = Join-Path (Join-Path $env:LOCALAPPDATA "TarkovCompanion") "Demo"
$ScanEventQuery = "SELECT COUNT(*) FROM raid_events WHERE type = 'scan';"
$ScanEventsBefore = $null
$ScanEventsAfter = $null
$SqliteVersion = $null
$RaidOpenedAfterUtc = $null

function Add-Assertion {
    param(
        [string] $Name,
        [bool] $Passed,
        [string] $Detail
    )

    $Assertions.Add([ordered]@{
        name = $Name
        passed = $Passed
        detail = $Detail
    })

    if (-not $Passed) {
        throw "Assertion failed: $Name - $Detail"
    }
}

function Wait-Path {
    param(
        [string] $Path,
        # A first run now populates several thousand items before the application settles,
        # so fifteen seconds was no longer a realistic ceiling for a diagnostic round trip.
        [int] $TimeoutSeconds = 90
    )

    $Deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not (Test-Path -LiteralPath $Path)) {
        if ([DateTime]::UtcNow -ge $Deadline) {
            throw "Timed out waiting for $Path"
        }

        Start-Sleep -Milliseconds 100
    }
}

<#
    Loads a read-only SQLite reader over the native library the package ships.

    The previous reader loaded the packaged Microsoft.Data.Sqlite.dll. That assembly is built
    for net10.0 and the workflow runs this script in Windows PowerShell 5.1, which is .NET
    Framework: the load could never succeed there, and the readiness loop swallowed the
    exception on every attempt, so the smoke could only ever report a misleading "timed out
    waiting for initialized SQLite database".

    e_sqlite3.dll is plain native code with the standard sqlite3_* exports, so it loads into
    either PowerShell. Preloading it by full path makes the DllImport below bind to exactly
    the engine the application writes with rather than to anything else on the search path.
    The C# stays at C# 5, the language Windows PowerShell's Add-Type compiles.

    Opened SQLITE_OPEN_READONLY, so this can observe the application's database and cannot
    change it. Every failure carries the SQLite result code in Exception.Data so a caller can
    tell "not initialized yet" from "broken" instead of treating both as "try again".
#>
function Initialize-SqliteReader {
    param([Parameter(Mandatory = $true)] [string] $LibraryPath)

    if (-not [Environment]::Is64BitProcess) {
        throw "The packaged win-x64 SQLite library needs a 64-bit PowerShell host."
    }
    if (-not (Test-Path -LiteralPath $LibraryPath -PathType Leaf)) {
        throw "The package does not contain the native SQLite library (e_sqlite3.dll)."
    }

    if (-not ("TarkovCompanionSqliteReader" -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class TarkovCompanionSqliteReader
{
    private const string Library = "e_sqlite3";
    private const int Ok = 0;
    private const int Row = 100;
    private const int Done = 101;
    private const int OpenReadOnly = 0x00000001;
    private const int IntegerColumn = 1;
    private static IntPtr loadedModule = IntPtr.Zero;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_libversion();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr database, int flags, IntPtr vfs);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(IntPtr database);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr database, byte[] sql, int length, out IntPtr statement, IntPtr tail);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr statement);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_count(IntPtr statement);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_type(IntPtr statement, int column);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern long sqlite3_column_int64(IntPtr statement, int column);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr statement);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_errmsg(IntPtr database);

    public static string Load(string libraryPath)
    {
        if (loadedModule == IntPtr.Zero)
        {
            IntPtr module = LoadLibraryW(libraryPath);
            if (module == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "The native SQLite library could not be loaded (Win32 error " + Marshal.GetLastWin32Error() + ").");
            }

            loadedModule = module;
        }

        return ReadUtf8(sqlite3_libversion());
    }

    public static long QueryInt64(string databasePath, string sql, int busyTimeoutMilliseconds)
    {
        if (loadedModule == IntPtr.Zero)
        {
            throw new InvalidOperationException("The native SQLite library has not been loaded.");
        }

        IntPtr database = IntPtr.Zero;
        IntPtr statement = IntPtr.Zero;
        try
        {
            int code = sqlite3_open_v2(Utf8(databasePath), out database, OpenReadOnly, IntPtr.Zero);
            if (code != Ok)
            {
                throw Failure("open", code, database);
            }

            sqlite3_busy_timeout(database, busyTimeoutMilliseconds);
            code = sqlite3_prepare_v2(database, Utf8(sql), -1, out statement, IntPtr.Zero);
            if (code != Ok)
            {
                throw Failure("prepare", code, database);
            }

            if (statement == IntPtr.Zero)
            {
                throw new InvalidOperationException("The SQLite query text contained no statement.");
            }

            code = sqlite3_step(statement);
            if (code == Done)
            {
                throw Coded("The SQLite query returned no row.", code);
            }

            if (code != Row)
            {
                throw Failure("step", code, database);
            }

            if (sqlite3_column_count(statement) != 1 || sqlite3_column_type(statement, 0) != IntegerColumn)
            {
                throw new InvalidOperationException("The SQLite query did not return exactly one integer column.");
            }

            long value = sqlite3_column_int64(statement, 0);
            code = sqlite3_step(statement);
            if (code == Row)
            {
                throw Coded("The SQLite query returned more than one row.", code);
            }

            if (code != Done)
            {
                throw Failure("step", code, database);
            }

            return value;
        }
        finally
        {
            if (statement != IntPtr.Zero)
            {
                sqlite3_finalize(statement);
            }

            if (database != IntPtr.Zero)
            {
                sqlite3_close_v2(database);
            }
        }
    }

    private static Exception Failure(string operation, int code, IntPtr database)
    {
        string message = database == IntPtr.Zero ? "no connection" : ReadUtf8(sqlite3_errmsg(database));
        return Coded("SQLite " + operation + " failed with result code " + code + ": " + message, code);
    }

    private static Exception Coded(string message, int code)
    {
        InvalidOperationException exception = new InvalidOperationException(message);
        exception.Data["SqliteResultCode"] = code;
        return exception;
    }

    private static byte[] Utf8(string value)
    {
        return Encoding.UTF8.GetBytes(value + "\0");
    }

    private static string ReadUtf8(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
        {
            return string.Empty;
        }

        int length = 0;
        while (Marshal.ReadByte(pointer, length) != 0)
        {
            length++;
        }

        byte[] bytes = new byte[length];
        Marshal.Copy(pointer, bytes, 0, length);
        return Encoding.UTF8.GetString(bytes);
    }
}
'@
    }

    return [TarkovCompanionSqliteReader]::Load((Resolve-Path -LiteralPath $LibraryPath).Path)
}

# Queries one integer from a specific durable state. A file timestamp or size is not proof that
# a transaction committed; a row is.
function Get-SqliteScalar {
    param([string] $DatabasePath, [string] $CommandText, [int] $BusyTimeoutMilliseconds = 2000)

    return [TarkovCompanionSqliteReader]::QueryInt64($DatabasePath, $CommandText, $BusyTimeoutMilliseconds)
}

# PowerShell wraps a .NET method's exception in MethodInvocationException, so the SQLite result
# code is on an inner exception. Returns the code (or null when the failure was not SQLite's,
# such as a library that would not load) and the innermost message.
function Get-SqliteFailure {
    param([System.Exception] $Exception)

    $Current = $Exception
    $Message = $Exception.Message
    while ($null -ne $Current) {
        $Message = $Current.Message
        if ($Current.Data.Contains("SqliteResultCode")) {
            return [pscustomobject]@{ code = [int]$Current.Data["SqliteResultCode"]; message = $Message }
        }
        $Current = $Current.InnerException
    }

    return [pscustomobject]@{ code = $null; message = $Message }
}

# Only states an initializing database genuinely passes through: a lock held by the writer
# (BUSY, LOCKED, PROTOCOL), a file still being created (CANTOPEN), and a table whose migration
# has not run yet. Anything else - a library that will not load, a corrupt file, a query that
# is simply wrong - is a defect, and waiting ninety seconds would only hide which one.
function Test-TransientSqliteFailure {
    param([object] $Failure)

    if ($null -eq $Failure.code) { return $false }
    if (@(5, 6, 14, 15) -contains $Failure.code) { return $true }
    return $Failure.code -eq 1 -and $Failure.message -match 'no such table'
}

# Polls an integer query until it reaches a minimum. Transient initialization states are retried
# and remembered; every other failure is raised at once, and a timeout names the last thing seen.
function Wait-SqliteCondition {
    param(
        [string] $DatabasePath,
        [string] $CommandText,
        [int64] $Minimum,
        [string] $Description,
        [int] $TimeoutSeconds = 90
    )

    $Deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $LastObservation = "the database file did not exist"
    do {
        if (Test-Path -LiteralPath $DatabasePath -PathType Leaf) {
            try {
                $Value = Get-SqliteScalar -DatabasePath $DatabasePath -CommandText $CommandText
                if ($Value -ge $Minimum) {
                    return $Value
                }
                $LastObservation = "the query returned $Value"
            }
            catch {
                $Failure = Get-SqliteFailure -Exception $_.Exception
                if (-not (Test-TransientSqliteFailure -Failure $Failure)) {
                    throw ("SQLite failed while waiting for {0}: {1}" -f $Description, $Failure.message)
                }
                $LastObservation = "SQLite was not ready ({0})" -f $Failure.message
            }
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $Deadline)

    throw ("Timed out after {0} second(s) waiting for {1}; last observation: {2}." -f $TimeoutSeconds, $Description, $LastObservation)
}

function Stop-StartedProcess {
    param([System.Diagnostics.Process] $Process)

    if ($null -ne $Process -and -not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        if (-not $Process.WaitForExit(5000)) {
            throw "Timed out stopping process $($Process.Id)."
        }
    }
}

function Invoke-PackagedApp {
    param(
        [string] $Path,
        [string[]] $Arguments,
        [string] $WorkRoot,
        [string] $Name
    )

    # TarkovCompanion.exe is a WinExe. PowerShell does not wait for GUI-subsystem
    # processes invoked with the call operator, so the exit code and any report
    # file would be read before the process had produced them.
    $Process = Start-Process -FilePath $Path -ArgumentList $Arguments -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput (Join-Path $WorkRoot "$Name.out.txt") `
        -RedirectStandardError (Join-Path $WorkRoot "$Name.err.txt")
    return $Process.ExitCode
}

function Send-DiagnosticCommand {
    param(
        [string] $ChannelRoot,
        [string] $Id,
        [string] $Command,
        [string] $Token,
        [AllowNull()]
        [string] $Scenario
    )

    $CommandDirectory = Join-Path $ChannelRoot "commands"
    $ResponseDirectory = Join-Path $ChannelRoot "responses"
    $CommandPath = Join-Path $CommandDirectory ($Id + ".command.json")
    $TemporaryPath = $CommandPath + ".tmp"
    $ResponsePath = Join-Path $ResponseDirectory ($Id + ".response.json")
    $Payload = [ordered]@{
        id = $Id
        command = $Command
        token = $Token
        scenario = $Scenario
    }

    # Windows PowerShell's -Encoding utf8 writes a BOM. The packaged channel deliberately
    # deserializes untrusted command files as JSON bytes; emit the same BOM-free UTF-8 form its
    # integration fixture writes, or a rejected file gets a different diagnostic response name
    # and the smoke can only report a misleading response timeout.
    $Utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText(
        $TemporaryPath,
        ($Payload | ConvertTo-Json -Compress),
        $Utf8WithoutBom)
    Move-Item -LiteralPath $TemporaryPath -Destination $CommandPath
    Wait-Path -Path $ResponsePath
    return Get-Content -LiteralPath $ResponsePath -Raw | ConvertFrom-Json
}

$Success = $false
try {
    $ResolvedAppPath = (Resolve-Path -LiteralPath $AppPath).Path
    $ResolvedSimulatorPath = (Resolve-Path -LiteralPath $SimulatorPath).Path
    New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null

    # First, so a reader that cannot load fails as itself rather than as a readiness timeout
    # after the application has been started and left waiting.
    $SqliteVersion = Initialize-SqliteReader -LibraryPath (Join-Path (Split-Path -Parent $ResolvedAppPath) "e_sqlite3.dll")
    Add-Assertion -Name "sqlite-reader" -Passed (-not [string]::IsNullOrWhiteSpace($SqliteVersion)) -Detail "Read-only reader over the packaged SQLite $SqliteVersion."

    $SelfTestPath = Join-Path $WorkRoot "self-test.json"
    $SelfTestExitCode = Invoke-PackagedApp -Path $ResolvedAppPath `
        -Arguments @("--self-test", "--output", $SelfTestPath) `
        -WorkRoot $WorkRoot `
        -Name "self-test"
    Add-Assertion -Name "self-test-exit" -Passed ($SelfTestExitCode -eq 0) -Detail "Exit code: $SelfTestExitCode"
    $SelfTest = Get-Content -LiteralPath $SelfTestPath -Raw | ConvertFrom-Json
    Add-Assertion -Name "self-test-report" -Passed ([bool]$SelfTest.success) -Detail "Headless report is successful."
    Add-Assertion -Name "self-test-offline" -Passed (-not [bool]$SelfTest.environment.networkContacted) -Detail "Self-test made no network contact."

    $ChannelRoot = Join-Path $WorkRoot "diagnostic-channel"
    $LogRoot = Join-Path $WorkRoot "logs"
    $ScreenshotRoot = Join-Path $WorkRoot "screenshots"
    $DiagnosticToken = [Guid]::NewGuid().ToString("N") + [Guid]::NewGuid().ToString("N")
    $env:TARKOV_COMPANION_DIAGNOSTIC_TOKEN = $DiagnosticToken

    # Demo mode composes the production UI and persistence path with its deterministic fixture
    # adapter.  A normal developer launch legitimately has no visible EFT window to capture and
    # therefore reports scan-unavailable; a recognition-required fixture must not quietly pass
    # in that state.
    #
    # A second of slack below the launch instant, so the raid this launch opens is recognised
    # without depending on sub-second clock agreement. A raid left open by an earlier run is
    # minutes older than that and cannot satisfy readiness in its place.
    $RaidOpenedAfterUtc = [DateTimeOffset]::UtcNow.AddSeconds(-1).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
    $AppProcess = Start-Process -FilePath $ResolvedAppPath -ArgumentList @(
        "--demo",
        "--page", "Scanner",
        "--developer-mode",
        "--diagnostic-channel", ('"{0}"' -f $ChannelRoot)
    ) -PassThru
    $null = $AppProcess.Handle
    $StartedProcesses.Add($AppProcess)
    Wait-Path -Path (Join-Path $ChannelRoot "commands")
    Add-Assertion -Name "developer-app-process" -Passed (-not $AppProcess.HasExited) -Detail "PID $($AppProcess.Id) is running."
    $DemoDatabase = Join-Path $DemoDataRoot "Database\tarkov-companion.db"
    $MigrationCount = Wait-SqliteCondition -DatabasePath $DemoDatabase `
        -CommandText "SELECT COUNT(*) FROM schema_migrations;" `
        -Minimum 1 `
        -Description "a migrated demo database"
    Add-Assertion -Name "demo-persistence-ready" -Passed ($MigrationCount -ge 1) -Detail "The demo composition recorded $MigrationCount schema migration(s)."

    # A migrated schema is not yet a place a scan can be recorded. RaidActivityCoordinator
    # writes a scan event only against the current raid, and the demo raid is opened later in
    # startup than the migrations. Scans sent in that gap are answered "scan-completed" and
    # recorded nowhere, which made the row delta depend on how fast the runner was.
    #
    # The raid row is the boundary because of the order the application does things in: the
    # raid id is set in memory first and the row is inserted after, so once the row can be read
    # the id a scan is recorded against already exists.
    $RaidCount = Wait-SqliteCondition -DatabasePath $DemoDatabase `
        -CommandText ("SELECT COUNT(*) FROM raids WHERE start_utc IS NOT NULL AND julianday(start_utc) >= julianday('{0}');" -f $RaidOpenedAfterUtc) `
        -Minimum 1 `
        -Description "the demo raid opened by this launch"
    Add-Assertion -Name "demo-raid-open" -Passed ($RaidCount -ge 1) -Detail "The demo launch opened a raid that scans are recorded against."
    $ScanEventsBefore = Get-SqliteScalar -DatabasePath $DemoDatabase -CommandText $ScanEventQuery

    for ($Index = 0; $Index -lt $Scenarios.Count; $Index++) {
        $Scenario = $Scenarios[$Index]
        $StatePath = Join-Path $WorkRoot ($Scenario + ".state.json")
        $SimulatorProcess = Start-Process -FilePath $ResolvedSimulatorPath -ArgumentList @(
            "--developer-mode",
            "--scenario", $Scenario,
            "--log-root", ('"{0}"' -f $LogRoot),
            "--screenshot-root", ('"{0}"' -f $ScreenshotRoot),
            "--state-output", ('"{0}"' -f $StatePath)
        ) -PassThru
        $null = $SimulatorProcess.Handle
        $StartedProcesses.Add($SimulatorProcess)

        Wait-Path -Path $StatePath
        Add-Assertion -Name ("scenario-process-" + $Scenario) -Passed (-not $SimulatorProcess.HasExited) -Detail "PID $($SimulatorProcess.Id) is running."
        $State = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
        Add-Assertion -Name ("scenario-state-" + $Scenario) -Passed ($State.scenario -ceq $Scenario) -Detail "State preserves the canonical scenario identifier."
        Add-Assertion -Name ("scenario-log-" + $Scenario) -Passed (Test-Path -LiteralPath $State.logPath) -Detail "Fake log output exists."
        Add-Assertion -Name ("scenario-screenshot-" + $Scenario) -Passed (Test-Path -LiteralPath $State.screenshotPath) -Detail "Screenshot-filename marker exists."
        Add-Assertion -Name ("scenario-safety-" + $Scenario) -Passed (-not $State.isGame -and -not $State.sendsInput) -Detail "Simulator declares no game identity or input surface."

        $ScenarioResponse = Send-DiagnosticCommand -ChannelRoot $ChannelRoot -Id ("scenario-" + $Index) -Command "scenario" -Token $DiagnosticToken -Scenario $Scenario
        Add-Assertion -Name ("diagnostic-scenario-" + $Scenario) -Passed ([bool]$ScenarioResponse.accepted) -Detail $ScenarioResponse.event
        $ScanResponse = Send-DiagnosticCommand -ChannelRoot $ChannelRoot -Id ("scan-" + $Index) -Command "scan" -Token $DiagnosticToken -Scenario $null
        Add-Assertion -Name ("diagnostic-scan-accepted-" + $Scenario) -Passed ([bool]$ScanResponse.accepted) -Detail $ScanResponse.event
        Add-Assertion -Name ("diagnostic-scan-available-" + $Scenario) -Passed ([bool]$ScanResponse.scan.isAvailable) -Detail "A required recognition fixture may not pass as unavailable."
        Add-Assertion -Name ("diagnostic-scan-completed-" + $Scenario) -Passed ($ScanResponse.event -ceq "scan-completed" -and [bool]$ScanResponse.scan.succeeded) -Detail "Expected a completed deterministic scan, got '$($ScanResponse.event)'."
        Add-Assertion -Name ("diagnostic-scan-item-" + $Scenario) -Passed (-not [string]::IsNullOrWhiteSpace([string]$ScanResponse.scan.itemName)) -Detail "The scan response contains a rendered-result item identity."
        Add-Assertion -Name ("diagnostic-scan-source-" + $Scenario) -Passed ($ScanResponse.scan.source -ceq "demo-fixture") -Detail "Expected the deterministic demo fixture source."

        # The response is written when the scan is queued for history, not when the row is.
        # RaidHistoryOutbox writes behind a queue, so reading straight after the last response
        # raced the writer. Each scan's row is waited for before the next scan is sent, so a
        # row that never lands is named by the scan that should have written it.
        $ExpectedSoFar = $ScanEventsBefore + $Index + 1
        $CommittedSoFar = Wait-SqliteCondition -DatabasePath $DemoDatabase `
            -CommandText $ScanEventQuery `
            -Minimum $ExpectedSoFar `
            -Description ("the durable scan row for {0}" -f $Scenario) `
            -TimeoutSeconds 30
        Add-Assertion -Name ("scan-row-committed-" + $Scenario) -Passed ($CommittedSoFar -eq $ExpectedSoFar) -Detail "Scan rows reached $CommittedSoFar; expected exactly $ExpectedSoFar."

        Stop-StartedProcess -Process $SimulatorProcess
    }

    $ScanEventsAfter = Get-SqliteScalar -DatabasePath $DemoDatabase -CommandText $ScanEventQuery
    $ExpectedScanEvents = [int64]$Scenarios.Count
    $ObservedScanEvents = $ScanEventsAfter - $ScanEventsBefore
    Add-Assertion -Name "scan-persisted" -Passed ($ObservedScanEvents -eq $ExpectedScanEvents) -Detail "Completed fixture scans committed $ObservedScanEvents scan row(s); expected $ExpectedScanEvents."

    Stop-StartedProcess -Process $AppProcess

    $env:TARKOV_COMPANION_OFFLINE = "1"
    $OfflineProcess = Start-Process -FilePath $ResolvedAppPath -ArgumentList @("--demo") -PassThru
    $null = $OfflineProcess.Handle
    $StartedProcesses.Add($OfflineProcess)
    Start-Sleep -Seconds 2
    Add-Assertion -Name "offline-relaunch" -Passed (-not $OfflineProcess.HasExited) -Detail "Demo process remained healthy while offline."
    Stop-StartedProcess -Process $OfflineProcess

    $Success = $true
}
catch {
    $Errors.Add($_.Exception.Message)
}
finally {
    foreach ($StartedProcess in $StartedProcesses) {
        Stop-StartedProcess -Process $StartedProcess
    }

    $env:TARKOV_COMPANION_DIAGNOSTIC_TOKEN = $PreviousDiagnosticToken
    $env:TARKOV_COMPANION_OFFLINE = $PreviousOffline

    $Report = [ordered]@{
        schemaVersion = 1
        generatedUtc = [DateTimeOffset]::UtcNow.ToString("O")
        success = $Success
        machine = [ordered]@{
            operatingSystem = [System.Environment]::OSVersion.VersionString
            architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
            powerShell = $PSVersionTable.PSVersion.ToString()
            interactiveDesktop = [System.Environment]::UserInteractive
        }
        prerequisites = @(
            "Windows 10 or newer",
            "interactive desktop session",
            "published TarkovCompanion.exe and TarkovCompanion.EftSimulator.exe",
            "write access to the selected work and report directories",
            "no Escape from Tarkov installation required"
        )
        scenarios = $Scenarios
        persistence = [ordered]@{
            reader = "packaged e_sqlite3, read-only"
            sqliteVersion = $SqliteVersion
            readiness = "migrated schema, then a raid opened by this launch"
            durableTable = "raid_events"
            durableEventType = "scan"
            scanEventCountBefore = $ScanEventsBefore
            scanEventCountAfter = $ScanEventsAfter
            expectedScanEventDelta = $Scenarios.Count
        }
        # Keep the artifact schema stable for zero/one/many results. Windows PowerShell
        # otherwise unwraps a single pipeline object, which makes consumers infer shape.
        assertions = $Assertions.ToArray()
        errors = $Errors.ToArray()
        # The artifact must be portable: a hosted-runner temp path is neither evidence nor
        # safe to publish. The caller already knows its selected report location.
        workRoot = "redacted"
    }

    $ResolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
    $OutputDirectory = Split-Path -Parent $ResolvedOutputPath
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $Report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ResolvedOutputPath -Encoding utf8
}

if (-not $Success) {
    exit 1
}

exit 0
