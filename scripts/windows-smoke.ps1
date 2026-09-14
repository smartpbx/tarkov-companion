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

$Scenarios = @(
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
$PersistenceBeforeScans = $null
$PersistenceAfterScans = $null

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

# The command directory exists before the application's asynchronous initialization has
# completed.  Waiting for a real SQLite file is the readiness boundary: it proves the
# composition root has initialized persistence, rather than relying on an arbitrary delay.
function Wait-DatabaseReady {
    param(
        [string] $Root,
        [int] $TimeoutSeconds = 90
    )

    $DatabasePath = Join-Path $Root "Database\tarkov-companion.db"
    Wait-Path -Path $DatabasePath -TimeoutSeconds $TimeoutSeconds
    if ((Get-Item -LiteralPath $DatabasePath).Length -le 0) {
        throw "The application created an empty database at $DatabasePath."
    }

    return $DatabasePath
}

# A completed diagnostic scan is awaited through RaidActivityCoordinator before its response is
# written.  Record a bounded, path-free fingerprint of its data directory on both sides so the
# smoke report proves that the scan changed persistent state without uploading runner paths.
function Get-DirectoryFingerprint {
    param([string] $Root)

    if (-not (Test-Path -LiteralPath $Root)) {
        return [ordered]@{ exists = $false; fileCount = 0; bytes = 0; newestUtc = $null }
    }

    $Files = @(Get-ChildItem -LiteralPath $Root -Recurse -File -ErrorAction Stop)
    $Bytes = 0L
    $Newest = $null
    foreach ($File in $Files) {
        $Bytes += $File.Length
        if ($null -eq $Newest -or $File.LastWriteTimeUtc -gt $Newest) {
            $Newest = $File.LastWriteTimeUtc
        }
    }

    return [ordered]@{
        exists = $true
        fileCount = $Files.Count
        bytes = $Bytes
        newestUtc = if ($null -eq $Newest) { $null } else { $Newest.ToString("O") }
    }
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

    $Payload | ConvertTo-Json | Set-Content -LiteralPath $TemporaryPath -Encoding utf8
    Move-Item -LiteralPath $TemporaryPath -Destination $CommandPath
    Wait-Path -Path $ResponsePath
    return Get-Content -LiteralPath $ResponsePath -Raw | ConvertFrom-Json
}

$Success = $false
try {
    $ResolvedAppPath = (Resolve-Path -LiteralPath $AppPath).Path
    $ResolvedSimulatorPath = (Resolve-Path -LiteralPath $SimulatorPath).Path
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
    Add-Assertion -Name "demo-persistence-ready" -Passed (Test-Path -LiteralPath $DemoDatabase) -Detail "The demo composition initialized its SQLite database."
    $PersistenceBeforeScans = Get-DirectoryFingerprint -Root $DemoDataRoot

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

    $PersistenceAfterScans = Get-DirectoryFingerprint -Root $DemoDataRoot
    $PersistenceChanged = $PersistenceAfterScans.fileCount -gt $PersistenceBeforeScans.fileCount -or
        $PersistenceAfterScans.bytes -gt $PersistenceBeforeScans.bytes -or
        $PersistenceAfterScans.newestUtc -ne $PersistenceBeforeScans.newestUtc
    Add-Assertion -Name "scan-persisted" -Passed $PersistenceChanged -Detail "Completed fixture scans changed the demo persistence fingerprint."

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
            beforeScans = if ($null -eq $PersistenceBeforeScans) { $null } else { $PersistenceBeforeScans }
            afterScans = if ($null -eq $PersistenceAfterScans) { $null } else { $PersistenceAfterScans }
        }
        assertions = $Assertions
        errors = $Errors
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
