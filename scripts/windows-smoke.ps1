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
$ScanEventsBefore = $null
$ScanEventsAfter = $null
$SqliteProviderAssemblyPath = $null

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

# Queries a specific durable state through the provider shipped with the package. This avoids
# treating a file timestamp or size as proof that a transaction actually committed.
function Get-SqliteScalar {
    param([string] $DatabasePath, [string] $CommandText)

    if (-not ("Microsoft.Data.Sqlite.SqliteConnection" -as [type])) {
        if ([string]::IsNullOrWhiteSpace($SqliteProviderAssemblyPath) -or -not (Test-Path -LiteralPath $SqliteProviderAssemblyPath)) {
            throw "The packaged SQLite provider is unavailable for the durable-state assertion."
        }
        [System.Reflection.Assembly]::LoadFrom($SqliteProviderAssemblyPath) | Out-Null
    }

    $Connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$DatabasePath;Mode=ReadOnly;Cache=Shared")
    try {
        $Connection.Open()
        $Command = $Connection.CreateCommand()
        try {
            $Command.CommandText = $CommandText
            return [int64]$Command.ExecuteScalar()
        }
        finally {
            $Command.Dispose()
        }
    }
    finally {
        $Connection.Dispose()
    }
}

# The command directory exists before the application's asynchronous initialization has
# completed. Waiting for a migrated SQLite schema is the readiness boundary: it proves the
# composition root initialized a usable database, rather than relying on an arbitrary delay.
function Wait-DatabaseReady {
    param(
        [string] $Root,
        [int] $TimeoutSeconds = 90
    )

    $DatabasePath = Join-Path $Root "Database\tarkov-companion.db"
    $Deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $LastObservedBytes = 0L
    while ([DateTime]::UtcNow -lt $Deadline) {
        if (Test-Path -LiteralPath $DatabasePath) {
            # SQLite creates its file before migrations and demo seeding have committed their
            # first page. A migrated schema row is the durable readiness evidence.
            $DatabaseFile = Get-Item -LiteralPath $DatabasePath -ErrorAction Stop
            $LastObservedBytes = [int64]$DatabaseFile.Length
            if ($LastObservedBytes -gt 0) {
                try {
                    if ((Get-SqliteScalar -DatabasePath $DatabasePath -CommandText "SELECT COUNT(*) FROM schema_migrations;") -gt 0) {
                        return [string]$DatabasePath
                    }
                }
                catch {
                    # The writer can still hold an exclusive initialization transaction.
                }
            }
        }

        Start-Sleep -Milliseconds 100
    }

    throw "Timed out waiting for initialized SQLite database at $DatabasePath (last observed size: $LastObservedBytes byte(s))."
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
    $SqliteProviderAssemblyPath = Join-Path (Split-Path -Parent $ResolvedAppPath) "Microsoft.Data.Sqlite.dll"
    New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null

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
    $DemoDatabase = Wait-DatabaseReady -Root $DemoDataRoot
    Add-Assertion -Name "demo-persistence-ready" -Passed (Test-Path -LiteralPath $DemoDatabase) -Detail "The demo composition initialized a migrated SQLite database."
    $ScanEventsBefore = Get-SqliteScalar -DatabasePath $DemoDatabase -CommandText "SELECT COUNT(*) FROM raid_events WHERE type = 'scan';"

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

        Stop-StartedProcess -Process $SimulatorProcess
    }

    $ScanEventsAfter = Get-SqliteScalar -DatabasePath $DemoDatabase -CommandText "SELECT COUNT(*) FROM raid_events WHERE type = 'scan';"
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
