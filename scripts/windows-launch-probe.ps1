<#
.SYNOPSIS
    Behavioral first-run probe for the packaged Windows build.

.DESCRIPTION
    Launches the extracted, self-contained TarkovCompanion.exe exactly as a user
    would - no arguments, no developer mode, no diagnostic channel - and records
    whether the application reaches a live desktop window, stays healthy, writes
    its local data, and shuts down cleanly.

    This probe is deliberately separate from windows-smoke.ps1. The smoke script
    exercises the developer diagnostic surface with the simulator; this probe
    proves the ordinary double-click path that a real user takes.

    The probe never reads or writes Escape from Tarkov memory, never sends input
    to any other process, and never requires Escape from Tarkov to be installed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $AppPath,

    [string] $OutputPath = (Join-Path $PWD "windows-launch-probe.json"),

    [string] $ScreenshotPath = (Join-Path $PWD "windows-launch-probe.png"),

    [int] $WindowTimeoutSeconds = 120,

    [int] $ObserveSeconds = 30,

    [int] $ShutdownTimeoutSeconds = 45,

    [string] $LocalDataRoot = (Join-Path $env:LOCALAPPDATA "TarkovCompanion")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Observations = [System.Collections.Generic.List[object]]::new()
$Errors = [System.Collections.Generic.List[string]]::new()

function Add-Observation {
    param(
        [string] $Name,
        [bool] $Passed,
        [string] $Detail,
        [bool] $Required = $true
    )

    $Observations.Add([ordered]@{
        name = $Name
        passed = $Passed
        required = $Required
        detail = $Detail
    })
}

function Measure-Directory {
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return [ordered]@{ exists = $false; fileCount = 0; totalBytes = 0 }
    }

    $Files = @(Get-ChildItem -LiteralPath $Path -Recurse -File -ErrorAction SilentlyContinue)
    $TotalBytes = 0
    foreach ($File in $Files) {
        $TotalBytes += $File.Length
    }

    return [ordered]@{
        exists = $true
        fileCount = $Files.Count
        totalBytes = $TotalBytes
    }
}

function Expand-DesktopResolution {
    <#
    .SYNOPSIS
        Raises the session's display resolution so the whole window is captured.
    .DESCRIPTION
        A hosted runner presents a 1024x768 desktop and this application asks for
        1500x900, with a 1120x720 minimum it will not go below. The screenshot was
        therefore a crop of the top-left corner of the window, which is the one part
        of the evidence a person actually reads, and it hid the layout below and to
        the right of it.

        Best effort on purpose. A runner image without this cmdlet, or one that
        refuses the mode, still produces a screenshot; it is just the old crop. This
        is evidence quality, not a thing to fail a build over.
    #>
    param(
        [int] $Width = 1920,
        [int] $Height = 1080
    )

    Add-Type -AssemblyName System.Windows.Forms
    $Before = [System.Windows.Forms.SystemInformation]::VirtualScreen
    if ($Before.Width -ge $Width -and $Before.Height -ge $Height) {
        return "$($Before.Width)x$($Before.Height) (already large enough)"
    }

    if (-not (Get-Command Set-DisplayResolution -ErrorAction SilentlyContinue)) {
        return "$($Before.Width)x$($Before.Height) (Set-DisplayResolution is unavailable)"
    }

    try {
        Set-DisplayResolution -Width $Width -Height $Height -Force
        Start-Sleep -Seconds 2
        $After = [System.Windows.Forms.SystemInformation]::VirtualScreen
        return "$($Before.Width)x$($Before.Height) to $($After.Width)x$($After.Height)"
    }
    catch {
        return "$($Before.Width)x$($Before.Height) (unchanged: $($_.Exception.Message))"
    }
}

function Save-PrimaryScreenImage {
    param([string] $Path)

    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing

    $Bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
    if ($Bounds.Width -le 0 -or $Bounds.Height -le 0) {
        throw "The session reports an empty virtual screen ($($Bounds.Width)x$($Bounds.Height))."
    }

    $Bitmap = New-Object System.Drawing.Bitmap $Bounds.Width, $Bounds.Height
    try {
        $Graphics = [System.Drawing.Graphics]::FromImage($Bitmap)
        try {
            $Graphics.CopyFromScreen($Bounds.X, $Bounds.Y, 0, 0, $Bitmap.Size)
        }
        finally {
            $Graphics.Dispose()
        }

        $Directory = Split-Path -Parent ([System.IO.Path]::GetFullPath($Path))
        New-Item -ItemType Directory -Path $Directory -Force | Out-Null
        $Bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $Bitmap.Dispose()
    }

    return "$($Bounds.Width)x$($Bounds.Height)"
}

# Done before the application starts so it lays out for the larger desktop rather
# than being resized under it.
$DesktopGeometry = Expand-DesktopResolution

$ResolvedAppPath = (Resolve-Path -LiteralPath $AppPath).Path
$PackageDirectory = Split-Path -Parent $ResolvedAppPath
$WorkRoot = Join-Path $env:TEMP ("tarkov-companion-probe-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null
$StandardOutputPath = Join-Path $WorkRoot "stdout.txt"
$StandardErrorPath = Join-Path $WorkRoot "stderr.txt"

$DataBefore = Measure-Directory -Path $LocalDataRoot
$Process = $null
$WindowTitle = $null
$WindowSeconds = $null
$WindowClosedAfterSeconds = $null
$ExitCode = $null
$GracefulClose = $false
$ScreenGeometry = $null
$Session = [ordered]@{}
$Success = $false

try {
    $Session = [ordered]@{
        operatingSystem = [System.Environment]::OSVersion.VersionString
        architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        powerShell = $PSVersionTable.PSVersion.ToString()
        userInteractive = [System.Environment]::UserInteractive
        userName = [System.Environment]::UserName
        packageDirectory = $PackageDirectory
    }

    $Process = Start-Process -FilePath $ResolvedAppPath `
        -WorkingDirectory $PackageDirectory `
        -RedirectStandardOutput $StandardOutputPath `
        -RedirectStandardError $StandardErrorPath `
        -PassThru

    # Touch the handle so the object keeps it open. Without this, ExitCode and WaitForExit
    # are unreliable once the process ends, which made a clean two-tenths-of-a-second
    # shutdown look like a process that refused to exit.
    $null = $Process.Handle

    $Stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($Stopwatch.Elapsed.TotalSeconds -lt $WindowTimeoutSeconds) {
        if ($Process.HasExited) {
            break
        }

        $Process.Refresh()
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            break
        }

        Start-Sleep -Milliseconds 250
    }

    $Stopwatch.Stop()
    $Process.Refresh()

    if ($Process.HasExited) {
        $EarlyExitCode = $Process.ExitCode
        $ExitCode = $EarlyExitCode
        Add-Observation -Name "process-alive" -Passed $false -Detail "The application exited early with code $EarlyExitCode."
        throw "The application exited before presenting a window (exit code $EarlyExitCode). Check startup.log in the local data directory."
    }

    Add-Observation -Name "process-alive" -Passed $true -Detail "PID $($Process.Id) is running."

    $WindowSeconds = [Math]::Round($Stopwatch.Elapsed.TotalSeconds, 2)
    $HasWindow = $Process.MainWindowHandle -ne [IntPtr]::Zero
    Add-Observation -Name "main-window" -Passed $HasWindow -Detail "Main window handle after $WindowSeconds second(s)."
    if (-not $HasWindow) {
        throw "No main window appeared within $WindowTimeoutSeconds seconds."
    }

    $WindowTitle = $Process.MainWindowTitle
    Add-Observation -Name "window-title" -Passed (-not [string]::IsNullOrWhiteSpace($WindowTitle)) -Detail "Title: '$WindowTitle'"

    # Observe the window for a while: an Avalonia view model that throws during
    # asynchronous initialisation typically kills the process shortly after the
    # window first appears, so an immediate check is not sufficient evidence.
    $ObserveDeadline = [DateTime]::UtcNow.AddSeconds($ObserveSeconds)
    $Unresponsive = 0
    while ([DateTime]::UtcNow -lt $ObserveDeadline) {
        Start-Sleep -Milliseconds 500
        $Process.Refresh()
        if ($Process.HasExited) {
            Add-Observation -Name "window-stable" -Passed $false -Detail "The application exited during observation with code $($Process.ExitCode)."
            throw "The application exited $ObserveSeconds second(s) after showing its window."
        }

        if (-not $Process.Responding) {
            $Unresponsive++
        }
    }

    Add-Observation -Name "window-stable" -Passed $true -Detail "Stayed alive for $ObserveSeconds second(s) after the window appeared."
    Add-Observation -Name "window-responding" -Passed ($Unresponsive -eq 0) -Detail "Unresponsive samples: $Unresponsive." -Required:$false

    try {
        $ScreenGeometry = Save-PrimaryScreenImage -Path $ScreenshotPath
        Add-Observation -Name "screenshot" -Passed $true -Detail "Captured $ScreenGeometry desktop to $ScreenshotPath. Display: $DesktopGeometry." -Required:$false
    }
    catch {
        Add-Observation -Name "screenshot" -Passed $false -Detail $_.Exception.Message -Required:$false
    }

    $GracefulClose = $Process.CloseMainWindow()

    # Track the window and the process separately. "The window closed but the process stayed"
    # and "the window never closed" are different defects and need different fixes.
    $ShutdownWatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($ShutdownWatch.Elapsed.TotalSeconds -lt $ShutdownTimeoutSeconds) {
        if ($Process.HasExited) {
            break
        }

        $Process.Refresh()
        if ($null -eq $WindowClosedAfterSeconds -and $Process.MainWindowHandle -eq [IntPtr]::Zero) {
            $WindowClosedAfterSeconds = [Math]::Round($ShutdownWatch.Elapsed.TotalSeconds, 2)
        }

        Start-Sleep -Milliseconds 250
    }

    $ShutdownWatch.Stop()
    $ShutdownSeconds = [Math]::Round($ShutdownWatch.Elapsed.TotalSeconds, 2)
    Add-Observation -Name "window-closed" `
        -Passed ($null -ne $WindowClosedAfterSeconds -or $Process.HasExited) `
        -Detail "Main window handle released after $WindowClosedAfterSeconds second(s)." `
        -Required:$false

    if (-not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        $Process.WaitForExit(10000) | Out-Null
        Add-Observation -Name "clean-shutdown" -Passed $false -Detail "The window-close request did not end the process within $ShutdownTimeoutSeconds second(s)."
    }
    else {
        $ExitCode = $Process.ExitCode
        Add-Observation -Name "clean-shutdown" -Passed ($ExitCode -eq 0) -Detail "Closing the window exited with code $ExitCode after $ShutdownSeconds second(s) (graceful request accepted: $GracefulClose)."
    }

    $Success = -not ($Observations | Where-Object { $_.required -and -not $_.passed })
}
catch {
    $Errors.Add($_.Exception.Message)
}
finally {
    if ($null -ne $Process -and -not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        $Process.WaitForExit(10000) | Out-Null
    }

    $DataAfter = Measure-Directory -Path $LocalDataRoot
    $DatabasePath = Join-Path $LocalDataRoot "Database\tarkov-companion.db"
    $DatabaseBytes = 0
    if (Test-Path -LiteralPath $DatabasePath) {
        $DatabaseBytes = (Get-Item -LiteralPath $DatabasePath).Length
    }

    Add-Observation -Name "local-data-written" `
        -Passed ($DataAfter.fileCount -gt $DataBefore.fileCount -or $DataAfter.totalBytes -gt $DataBefore.totalBytes) `
        -Detail "Local data grew from $($DataBefore.fileCount) file(s)/$($DataBefore.totalBytes) byte(s) to $($DataAfter.fileCount) file(s)/$($DataAfter.totalBytes) byte(s)." `
        -Required:$false
    Add-Observation -Name "database-created" -Passed ($DatabaseBytes -gt 0) -Detail "SQLite database is $DatabaseBytes byte(s)." -Required:$false

    $StandardOutput = ""
    if (Test-Path -LiteralPath $StandardOutputPath) {
        $StandardOutput = (Get-Content -LiteralPath $StandardOutputPath -Raw -ErrorAction SilentlyContinue)
    }

    $StandardError = ""
    if (Test-Path -LiteralPath $StandardErrorPath) {
        $StandardError = (Get-Content -LiteralPath $StandardErrorPath -Raw -ErrorAction SilentlyContinue)
    }

    $Report = [ordered]@{
        schemaVersion = 1
        generatedUtc = [DateTimeOffset]::UtcNow.ToString("O")
        success = $Success
        appPath = $ResolvedAppPath
        session = $Session
        window = [ordered]@{
            appearedAfterSeconds = $WindowSeconds
            title = $WindowTitle
            observedSeconds = $ObserveSeconds
            screenGeometry = $ScreenGeometry
            screenshotPath = $ScreenshotPath
        }
        shutdown = [ordered]@{
            gracefulRequestAccepted = $GracefulClose
            windowClosedAfterSeconds = $WindowClosedAfterSeconds
            exitCode = $ExitCode
        }
        localData = [ordered]@{
            root = $LocalDataRoot
            before = $DataBefore
            after = $DataAfter
            databaseBytes = $DatabaseBytes
        }
        standardOutput = $StandardOutput
        standardError = $StandardError
        observations = $Observations
        errors = $Errors
        workRoot = $WorkRoot
    }

    $ResolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
    $OutputDirectory = Split-Path -Parent $ResolvedOutputPath
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $Report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ResolvedOutputPath -Encoding utf8
    Write-Host ($Report | ConvertTo-Json -Depth 8)
}

if (-not $Success) {
    exit 1
}

exit 0
