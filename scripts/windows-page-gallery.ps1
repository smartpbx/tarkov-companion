<#
.SYNOPSIS
    Photographs every destination in the application, one launch each.
.DESCRIPTION
    Captures responsive visual variation for each destination and fails any launch
    whose interface toolkit reported a fault. It is not a semantic assertion that the
    expected page, its accessibility tree, or its data and map tiles are ready: that
    remains an open #279 criterion, and it depends on the application readiness
    signal owned by #281.

    Each page is opened in its own launch, through the application's own --page
    option, and photographed.

    The map gets shots of its own on top of that. It is the left column of the Raid
    page, so raid.png was always a picture of it — in exactly one state: cold launch,
    default map, base floor, flat. The stacked view, any other floor and any other map
    had never been photographed. These additional pictures only show that a launch
    with those arguments produced responsive visual variation; they do not establish
    floor-stack semantics or map-tile/data readiness.

    The toolkit's own warnings are captured per launch through
    TARKOV_COMPANION_UI_WARNING_LOG, and a binding the toolkit could not resolve, a
    value it could not convert, or a resource it could not find fails that launch.
    Compiled bindings catch a renamed property at build time; these are the ones they
    do not, and each is a page asking for something it does not get.

    That gate was here before and was not real. Its pattern was written "\\[Binding\\]"
    in a double-quoted PowerShell string, where a backslash is not an escape, so the
    regular expression asked for a literal backslash and never matched "[Binding]" at
    all. It was then removed rather than repaired. A capture that was never armed also
    passed, because no file read as no warnings; a launch whose log is missing or empty
    now fails, since the application writes its own startup lines to the same listener.

    Escape from Tarkov is neither required nor touched. Nothing here reads game
    memory or sends input to another process.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $AppPath,

    [string] $OutputPath = (Join-Path $PWD "windows-page-gallery.json"),

    [string] $ScreenshotDirectory = (Join-Path $PWD "pages"),

    [string[]] $Pages = @(
        "Raid", "Squad", "Group", "Scanner", "Items", "Ammo", "Keys",
        "Flea", "Quests", "Hideout", "Events", "Loadout", "History", "Settings"),

    # These are visual captures of launches with map arguments. The application can fall
    # back when an argument has no data, so they intentionally prove neither selected-map
    # semantics nor floor-stack or tile readiness.
    [object[]] $MapViews = @(
        @{ name = "map-stacked"; args = @("--page", "Raid", "--map", "customs", "--stack") },
        # The quotes are inside the string on purpose. Start-Process joins ArgumentList with
        # spaces and quotes nothing, so a bare "3rd Floor" reached the application as two
        # arguments and it opened on a floor named "3rd" -- which the map said out loud, in the
        # status line, which is how this was found.
        @{ name = "map-floor";   args = @("--page", "Raid", "--map", "customs", "--floor", '"3rd Floor"') },
        @{ name = "map-streets"; args = @("--page", "Raid", "--map", "streets-of-tarkov") }
    ),

    [int] $WindowTimeoutSeconds = 90,

    [int] $ReadinessTimeoutSeconds = 30,

    # A near-uniform window has insufficient visual variation. These thresholds do not
    # establish page content or semantic readiness.
    [int] $MinimumDistinctColors = 48,

    [double] $MinimumVariedFraction = 0.02,

    # A toolkit line matching this fails the launch that wrote it. The areas are the ones in
    # which a warning means the page asked for something and did not get it; the phrases catch
    # the same failure reported from any other area. Single-quoted on purpose: see above.
    [string] $FailOnWarningPattern = '^\[(Binding|Property|Visual|Layout|Control)\]|Could not find|does not have|Unable to resolve|Cannot resolve|Unable to convert|Static resource',

    # Recorded per launch in the report. The count is always exact; only the copies are bounded.
    [int] $MaximumRecordedFaultsPerLaunch = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Save-ScreenImage {
    param([string] $Path, [IntPtr] $WindowHandle)

    $Bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
    if (-not ("TarkovCompanionGalleryBounds" -as [type])) {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class TarkovCompanionGalleryBounds {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr handle, out RECT rect);
}
"@
    }

    $Rect = New-Object TarkovCompanionGalleryBounds+RECT
    if (-not [TarkovCompanionGalleryBounds]::GetWindowRect($WindowHandle, [ref] $Rect)) {
        throw "Could not read the companion window bounds."
    }

    $Left = [Math]::Max($Rect.Left, $Bounds.Left)
    $Top = [Math]::Max($Rect.Top, $Bounds.Top)
    $Right = [Math]::Min($Rect.Right, $Bounds.Right)
    $Bottom = [Math]::Min($Rect.Bottom, $Bounds.Bottom)
    $WindowBounds = New-Object System.Drawing.Rectangle $Left, $Top, ($Right - $Left), ($Bottom - $Top)
    if ($WindowBounds.Width -le 0 -or $WindowBounds.Height -le 0) {
        throw "The companion window is outside the visible desktop."
    }

    $Bitmap = New-Object System.Drawing.Bitmap $WindowBounds.Width, $WindowBounds.Height
    try {
        $Graphics = [System.Drawing.Graphics]::FromImage($Bitmap)
        try {
            $Graphics.CopyFromScreen($WindowBounds.X, $WindowBounds.Y, 0, 0, $Bitmap.Size)
        }
        finally {
            $Graphics.Dispose()
        }

        $Bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $Bitmap.Dispose()
    }
}

# Matches the launch probe: the window asks for more room than a hosted runner's
# default desktop has, and a cropped photograph of a layout is not evidence.
if (Get-Command Set-DisplayResolution -ErrorAction SilentlyContinue) {
    try {
        Set-DisplayResolution -Width 1920 -Height 1080 -Force
        $Deadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            Start-Sleep -Milliseconds 250
            $Current = [System.Windows.Forms.SystemInformation]::VirtualScreen
        } while (($Current.Width -lt 1920 -or $Current.Height -lt 1080) -and [DateTime]::UtcNow -lt $Deadline)
    }
    catch {
        Write-Host "Display resolution unchanged: $($_.Exception.Message)"
    }
}

<#
    Measures how much of the photograph actually has something on it.

    Sampled on a grid rather than pixel by pixel: a full 1920x1080 read through
    GetPixel takes minutes in PowerShell, and every twelfth pixel answers the
    question just as well. LockBits and one Marshal copy is the whole cost.
#>
function Measure-ImageContent {
    param([string] $Path)

    $Bitmap = [System.Drawing.Bitmap]::FromFile($Path)
    try {
        $Rect = New-Object System.Drawing.Rectangle 0, 0, $Bitmap.Width, $Bitmap.Height
        $Data = $Bitmap.LockBits($Rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $Stride = [Math]::Abs($Data.Stride)
            $Bytes = New-Object byte[] ($Stride * $Bitmap.Height)
            [System.Runtime.InteropServices.Marshal]::Copy($Data.Scan0, $Bytes, 0, $Bytes.Length)
        }
        finally {
            $Bitmap.UnlockBits($Data)
        }

        $Counts = @{}
        $Sampled = 0
        $Step = 12
        for ($Y = 0; $Y -lt $Bitmap.Height; $Y += $Step) {
            $Row = $Y * $Stride
            for ($X = 0; $X -lt $Bitmap.Width; $X += $Step) {
                $Offset = $Row + ($X * 4)
                $Key = ($Bytes[$Offset + 2] -shl 16) -bor ($Bytes[$Offset + 1] -shl 8) -bor $Bytes[$Offset]
                if ($Counts.ContainsKey($Key)) { $Counts[$Key]++ } else { $Counts[$Key] = 1 }
                $Sampled++
            }
        }

        if ($Sampled -eq 0) {
            return [pscustomobject]@{ distinctColors = 0; variedFraction = 0.0 }
        }

        $Dominant = 0
        foreach ($Count in $Counts.Values) { if ($Count -gt $Dominant) { $Dominant = $Count } }
        return [pscustomobject]@{
            distinctColors = $Counts.Count
            variedFraction = [Math]::Round(($Sampled - $Dominant) / $Sampled, 4)
        }
    }
    finally {
        $Bitmap.Dispose()
    }
}

<#
    Closes a launch and waits for it, so whatever it writes on the way out has landed.
    Safe to call twice; the second call finds it already gone.
#>
function Close-AppProcess {
    param([System.Diagnostics.Process] $Process, [string] $Page)

    try {
        if (-not $Process.HasExited) {
            $null = $Process.CloseMainWindow()
            # Kill() without the process-tree overload: that one is .NET Core only, so under
            # Windows PowerShell it threw, was reported below, and left the launch running into
            # the next one.
            if (-not $Process.WaitForExit(20000)) { $Process.Kill() }
            $null = $Process.WaitForExit(5000)
        }
    }
    catch {
        Write-Host "Could not close the process for '$Page': $($_.Exception.Message)"
    }
}

<#
    Reads the lines one launch wrote to its warning log, or null when it wrote no file.

    Read with a StreamReader rather than Get-Content. Get-Content decorates every line with
    PSPath and friends, and Windows PowerShell's ConvertTo-Json serialises those, so each
    warning became an object carrying the runner's path; the report grew to a megabyte of it.

    Shared for writing, because a launch that could not be closed still holds the listener's
    handle, and that should be reported as the fault it is rather than as an IOException.
#>
function Read-WarningLogLines {
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    $Stream = [System.IO.FileStream]::new($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    $Reader = [System.IO.StreamReader]::new($Stream)
    try {
        $Lines = [System.Collections.Generic.List[string]]::new()
        while ($null -ne ($Line = $Reader.ReadLine())) {
            if (-not [string]::IsNullOrWhiteSpace($Line)) { $Lines.Add($Line) }
        }
        return ,$Lines.ToArray()
    }
    finally {
        $Reader.Dispose()
    }
}

<#
    Picks the interface faults out of one launch's trace lines.

    The file receives everything written to Trace, and the application's own logger writes
    there too, as "[Information] Category: message", followed by an exception's text when it
    has one. Those are application diagnostics. Judging them by the phrases would fail a page
    because a log message happened to say "could not find", so only the toolkit's own lines,
    "[Area] message", are judged.
#>
function Get-InterfaceFaultLines {
    param([AllowEmptyCollection()] [string[]] $Lines, [string] $Pattern)

    $ApplicationLine = '^\[(Trace|Debug|Information|Warning|Error|Critical|None)\] '
    return ,[string[]]@($Lines | Where-Object {
        $_ -match '^\[[A-Za-z0-9]+\]' -and $_ -notmatch $ApplicationLine -and $_ -match $Pattern
    })
}

function New-ShotResult {
    param([string] $Page)

    return [pscustomobject]@{
        page = $Page
        presented = $false
        visuallyVaried = $false
        detail = "The launch did not complete."
        screenshot = $null
        distinctColors = 0
        variedFraction = 0.0
        warningCaptureArmed = $false
        warningLineCount = 0
        interfaceFaultCount = 0
        interfaceFaults = [string[]]@()
    }
}

$ResolvedAppPath = (Resolve-Path -LiteralPath $AppPath).Path
New-Item -ItemType Directory -Path $ScreenshotDirectory -Force | Out-Null
$Results = [System.Collections.Generic.List[object]]::new()

$WarningDirectory = Join-Path $ScreenshotDirectory "warnings"
New-Item -ItemType Directory -Path $WarningDirectory -Force | Out-Null

# One list of shots, so a page and a map view go through exactly the same launch, presentation
# measurement and warning capture. A map view is a page opened with more said about it.
$Shots = [System.Collections.Generic.List[object]]::new()
foreach ($Name in $Pages) {
    $Shots.Add([pscustomobject]@{ name = $Name; args = @("--page", $Name) })
}
foreach ($View in $MapViews) {
    $Shots.Add([pscustomobject]@{ name = $View.name; args = $View.args })
}

foreach ($Shot in $Shots) {
    $Page = $Shot.name
    $Screenshot = Join-Path $ScreenshotDirectory ("{0}.png" -f $Page.ToLowerInvariant())
    $WarningLog = Join-Path $WarningDirectory ("{0}.log" -f $Page.ToLowerInvariant())
    $Result = New-ShotResult -Page $Page
    $Process = $null
    try {
        if (Test-Path -LiteralPath $WarningLog) { Remove-Item -LiteralPath $WarningLog -Force }
        # Inherited by this launch only, and read back once it has exited. The application
        # writes nothing there unless this is set, so a player's run costs nothing.
        $env:TARKOV_COMPANION_UI_WARNING_LOG = $WarningLog
        $Process = Start-Process -FilePath $ResolvedAppPath -ArgumentList $Shot.args -PassThru
        # Reading Handle here is what makes ExitCode and WaitForExit reliable later.
        $null = $Process.Handle

        $Stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        while ($Stopwatch.Elapsed.TotalSeconds -lt $WindowTimeoutSeconds) {
            if ($Process.HasExited) { break }
            $Process.Refresh()
            if ($Process.MainWindowHandle -ne [IntPtr]::Zero) { break }
            Start-Sleep -Milliseconds 250
        }
        $Stopwatch.Stop()
        $Process.Refresh()

        if ($Process.HasExited) {
            $Result.detail = "Exited with code $($Process.ExitCode) before showing a window."
            continue
        }

        if ($Process.MainWindowHandle -eq [IntPtr]::Zero) {
            $Result.detail = "No window within $WindowTimeoutSeconds second(s)."
            continue
        }

        # Window creation is not page readiness. Two consecutive responsive samples only make
        # the visual capture less racy; expected-page semantics, accessibility and data/tile
        # readiness are not proven here (open #279 criterion; readiness signal owned by #281).
        #
        # The capture must also be armed before anything is photographed or closed. The listener
        # creates its file on the first line it receives, which is the application's own startup
        # line, so a launch closed before that would have been judged fault-free by a listener
        # that had not yet been heard from.
        $ReadinessDeadline = [DateTime]::UtcNow.AddSeconds($ReadinessTimeoutSeconds)
        $ResponsiveSamples = 0
        $Armed = $false
        while ([DateTime]::UtcNow -lt $ReadinessDeadline -and ($ResponsiveSamples -lt 2 -or -not $Armed)) {
            $Process.Refresh()
            if ($Process.HasExited) { break }
            if ($Process.Responding) { $ResponsiveSamples++ } else { $ResponsiveSamples = 0 }
            $Armed = (Test-Path -LiteralPath $WarningLog -PathType Leaf) -and (Get-Item -LiteralPath $WarningLog).Length -gt 0
            if ($ResponsiveSamples -lt 2 -or -not $Armed) { Start-Sleep -Milliseconds 250 }
        }
        $Process.Refresh()
        if ($Process.HasExited) {
            $Result.detail = "Exited with code $($Process.ExitCode) shortly after showing its window."
            continue
        }

        if ($ResponsiveSamples -lt 2) {
            throw "The $Page page did not become responsive within $ReadinessTimeoutSeconds second(s)."
        }

        if (-not $Armed) {
            throw "The warning capture was not armed within $ReadinessTimeoutSeconds second(s): nothing reached TARKOV_COMPANION_UI_WARNING_LOG."
        }

        Save-ScreenImage -Path $Screenshot -WindowHandle $Process.MainWindowHandle

        Close-AppProcess -Process $Process -Page $Page
        $Content = Measure-ImageContent -Path $Screenshot
        $Drew = $Content.distinctColors -ge $MinimumDistinctColors -and $Content.variedFraction -ge $MinimumVariedFraction
        $Detail = "Responsive window after $([Math]::Round($Stopwatch.Elapsed.TotalSeconds, 2))s · $($Content.distinctColors) colours · $([Math]::Round($Content.variedFraction * 100, 1))% visually varied"
        if (-not $Drew) { $Detail = "Insufficient visual variation · $Detail" }

        $Result.presented = $true
        $Result.visuallyVaried = $Drew
        $Result.detail = $Detail
        $Result.screenshot = (Split-Path -Leaf $Screenshot)
        $Result.distinctColors = $Content.distinctColors
        $Result.variedFraction = $Content.variedFraction
    }
    catch {
        $Result.detail = $_.Exception.Message
    }
    finally {
        Remove-Item Env:\TARKOV_COMPANION_UI_WARNING_LOG -ErrorAction SilentlyContinue
        if ($null -ne $Process) {
            Close-AppProcess -Process $Process -Page $Page
            $Process.Dispose()
        }

        # Every outcome, including a launch that never showed a window: what the toolkit said
        # on the way down is often the explanation. Read after the close, so it is all there.
        try {
            $Lines = Read-WarningLogLines -Path $WarningLog
            if ($null -ne $Lines) {
                $Faults = Get-InterfaceFaultLines -Lines $Lines -Pattern $FailOnWarningPattern
                $Result.warningCaptureArmed = $Lines.Count -gt 0
                $Result.warningLineCount = $Lines.Count
                $Result.interfaceFaultCount = $Faults.Count
                $Result.interfaceFaults = [string[]]@($Faults |
                    Select-Object -First $MaximumRecordedFaultsPerLaunch |
                    ForEach-Object { if ($_.Length -gt 400) { $_.Substring(0, 400) } else { $_ } })
            }
        }
        catch {
            $Result.warningCaptureArmed = $false
            $Result.detail = "{0} (warning log unreadable: {1})" -f $Result.detail, $_.Exception.Message
        }

        $Results.Add($Result)
    }
}

$NoWindow = @($Results | Where-Object { -not $_.presented })
$Blank = @($Results | Where-Object { $_.presented -and -not $_.visuallyVaried })
$Faulted = @($Results | Where-Object { $_.interfaceFaultCount -gt 0 })
$Unarmed = @($Results | Where-Object { -not $_.warningCaptureArmed })
$Failed = @($Results | Where-Object {
    -not $_.presented -or -not $_.visuallyVaried -or $_.interfaceFaultCount -gt 0 -or -not $_.warningCaptureArmed
})

$Report = [pscustomobject]@{
    generatedUtc = [DateTime]::UtcNow.ToString("o")
    appPath = "package/TarkovCompanion.exe"
    pages = $Results
    failedCount = $Failed.Count
    noWindowCount = $NoWindow.Count
    blankCount = $Blank.Count
    interfaceFaultCount = $Faulted.Count
    warningCaptureUnarmedCount = $Unarmed.Count
    scope = "Responsive visual variation and toolkit interface faults only; semantic expected-page, accessibility, and data/tile readiness are not proven here and remain an open #279 criterion that depends on the application readiness signal owned by #281."
}

$Directory = Split-Path -Parent ([System.IO.Path]::GetFullPath($OutputPath))
New-Item -ItemType Directory -Path $Directory -Force | Out-Null
$Report | ConvertTo-Json -Depth 6 | Set-Content -Path $OutputPath -Encoding utf8

foreach ($Result in $Results) {
    $Mark = if ($Failed -contains $Result) { "FAIL" } else { "ok  " }
    Write-Host "$Mark $($Result.page): $($Result.warningLineCount) trace line(s), $($Result.interfaceFaultCount) interface fault(s)"
    foreach ($Line in @($Result.interfaceFaults | Select-Object -First 5)) {
        Write-Host "     $Line"
    }
}

# Named separately because they are different repairs. No window is a launch that broke, an
# unvaried one did not fill in, an interface fault is a page asking for something it does not
# get, and an unarmed capture is a gate that was not listening. None of them is proof that the
# requested page, its accessibility tree, or its map/data tiles are ready.
$Problems = @()
if ($NoWindow.Count -gt 0) { $Problems += "no window: $(($NoWindow | ForEach-Object { $_.page }) -join ', ')" }
if ($Blank.Count -gt 0) { $Problems += "insufficient visual variation: $(($Blank | ForEach-Object { $_.page }) -join ', ')" }
if ($Faulted.Count -gt 0) { $Problems += "interface faults: $(($Faulted | ForEach-Object { $_.page }) -join ', ')" }
if ($Unarmed.Count -gt 0) { $Problems += "warning capture was not armed: $(($Unarmed | ForEach-Object { $_.page }) -join ', ')" }

if ($Problems.Count -gt 0) {
    throw ($Problems -join "; ")
}

Write-Host "All $($Results.Count) launches showed responsive visual variation with an armed warning capture and no interface faults; semantic page, accessibility and data/tile readiness are not proven here."
