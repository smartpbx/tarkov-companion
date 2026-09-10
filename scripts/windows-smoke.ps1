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
        [int] $TimeoutSeconds = 15
    )

    $Deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not (Test-Path -LiteralPath $Path)) {
        if ([DateTime]::UtcNow -ge $Deadline) {
            throw "Timed out waiting for $Path"
        }

        Start-Sleep -Milliseconds 100
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

    $AppProcess = Start-Process -FilePath $ResolvedAppPath -ArgumentList @(
        "--developer-mode",
        "--diagnostic-channel", ('"{0}"' -f $ChannelRoot)
    ) -PassThru
    $StartedProcesses.Add($AppProcess)
    Wait-Path -Path (Join-Path $ChannelRoot "commands")
    Add-Assertion -Name "developer-app-process" -Passed (-not $AppProcess.HasExited) -Detail "PID $($AppProcess.Id) is running."

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
        Add-Assertion -Name ("diagnostic-scan-" + $Scenario) -Passed ([bool]$ScanResponse.accepted) -Detail $ScanResponse.event

        Stop-StartedProcess -Process $SimulatorProcess
    }

    Stop-StartedProcess -Process $AppProcess

    $env:TARKOV_COMPANION_OFFLINE = "1"
    $OfflineProcess = Start-Process -FilePath $ResolvedAppPath -ArgumentList @("--demo") -PassThru
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
        assertions = $Assertions
        errors = $Errors
        workRoot = $WorkRoot
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
