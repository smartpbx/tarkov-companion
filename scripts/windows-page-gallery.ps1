<#
.SYNOPSIS
    Photographs every destination in the application, one launch each.
.DESCRIPTION
    Captures responsive visual variation for each destination. This is deliberately
    a presentation smoke check, not a semantic assertion that the expected page,
    its accessibility tree, or its data and map tiles are ready. Those stronger
    checks remain at the #279 integration seam owned by #281.

    Each page is opened in its own launch, through the application's own --page
    option, and photographed.

    The map gets shots of its own on top of that. It is the left column of the Raid
    page, so raid.png was always a picture of it — in exactly one state: cold launch,
    default map, base floor, flat. The stacked view, any other floor and any other map
    had never been photographed. These additional pictures only show that a launch
    with those arguments produced responsive visual variation; they do not establish
    floor-stack semantics or map-tile/data readiness.

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

    [double] $MinimumVariedFraction = 0.02
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
            if (-not $Process.WaitForExit(20000)) { $Process.Kill($true) }
            $null = $Process.WaitForExit(5000)
        }
    }
    catch {
        Write-Host "Could not close the process for '$Page': $($_.Exception.Message)"
    }
}

$ResolvedAppPath = (Resolve-Path -LiteralPath $AppPath).Path
New-Item -ItemType Directory -Path $ScreenshotDirectory -Force | Out-Null
$Results = [System.Collections.Generic.List[object]]::new()

# One list of shots, so a page and a map view go through exactly the same launch and
# presentation measurement. A map view is a page opened with more said about it.
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
    $Process = $null
    try {
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
            $Results.Add([pscustomobject]@{
                page = $Page
                presented = $false
                visuallyVaried = $false
                detail = "Exited with code $($Process.ExitCode) before showing a window."
                screenshot = $null
                distinctColors = 0
                variedFraction = 0.0
            })
            continue
        }

        if ($Process.MainWindowHandle -eq [IntPtr]::Zero) {
            $Results.Add([pscustomobject]@{
                page = $Page
                presented = $false
                visuallyVaried = $false
                detail = "No window within $WindowTimeoutSeconds second(s)."
                screenshot = $null
                distinctColors = 0
                variedFraction = 0.0
            })
            continue
        }

        # Window creation is not page readiness. Two consecutive responsive samples only make
        # the visual capture less racy; expected-page semantics, accessibility and data/tile
        # readiness remain deferred to the #279/#281 integration seam.
        $ReadinessDeadline = [DateTime]::UtcNow.AddSeconds($ReadinessTimeoutSeconds)
        $ResponsiveSamples = 0
        while ([DateTime]::UtcNow -lt $ReadinessDeadline -and $ResponsiveSamples -lt 2) {
            $Process.Refresh()
            if ($Process.HasExited) { break }
            if ($Process.Responding) { $ResponsiveSamples++ } else { $ResponsiveSamples = 0 }
            if ($ResponsiveSamples -lt 2) { Start-Sleep -Milliseconds 250 }
        }
        $Process.Refresh()
        if ($Process.HasExited) {
            $Results.Add([pscustomobject]@{
                page = $Page
                presented = $false
                visuallyVaried = $false
                detail = "Exited with code $($Process.ExitCode) shortly after showing its window."
                screenshot = $null
                distinctColors = 0
                variedFraction = 0.0
            })
            continue
        }

        if ($ResponsiveSamples -lt 2) {
            throw "The $Page page did not become responsive within $ReadinessTimeoutSeconds second(s)."
        }

        Save-ScreenImage -Path $Screenshot -WindowHandle $Process.MainWindowHandle

        Close-AppProcess -Process $Process -Page $Page
        $Content = Measure-ImageContent -Path $Screenshot
        $Drew = $Content.distinctColors -ge $MinimumDistinctColors -and $Content.variedFraction -ge $MinimumVariedFraction
        $Detail = "Responsive window after $([Math]::Round($Stopwatch.Elapsed.TotalSeconds, 2))s · $($Content.distinctColors) colours · $([Math]::Round($Content.variedFraction * 100, 1))% visually varied"
        if (-not $Drew) { $Detail = "Insufficient visual variation · $Detail" }

        $Results.Add([pscustomobject]@{
            page = $Page
            presented = $true
            visuallyVaried = $Drew
            detail = $Detail
            screenshot = (Split-Path -Leaf $Screenshot)
            distinctColors = $Content.distinctColors
            variedFraction = $Content.variedFraction
        })
    }
    catch {
        $Results.Add([pscustomobject]@{
            page = $Page
            presented = $false
            visuallyVaried = $false
            detail = $_.Exception.Message
            screenshot = $null
            distinctColors = 0
            variedFraction = 0.0
        })
    }
    finally {
        if ($null -ne $Process) {
            Close-AppProcess -Process $Process -Page $Page
            $Process.Dispose()
        }

    }
}

$NoWindow = @($Results | Where-Object { -not $_.presented })
$Blank = @($Results | Where-Object { $_.presented -and -not $_.visuallyVaried })
$Failed = @($Results | Where-Object { -not $_.presented -or -not $_.visuallyVaried })

$Report = [pscustomobject]@{
    generatedUtc = [DateTime]::UtcNow.ToString("o")
    appPath = "package/TarkovCompanion.exe"
    pages = $Results
    failedCount = $Failed.Count
    noWindowCount = $NoWindow.Count
    blankCount = $Blank.Count
    scope = "Responsive visual variation only; semantic expected-page, accessibility, and data/tile readiness are deferred to #279 integration owned by #281."
}

$Directory = Split-Path -Parent ([System.IO.Path]::GetFullPath($OutputPath))
New-Item -ItemType Directory -Path $Directory -Force | Out-Null
$Report | ConvertTo-Json -Depth 6 | Set-Content -Path $OutputPath -Encoding utf8

foreach ($Result in $Results) {
    $Mark = if ($Result.presented -and $Result.visuallyVaried) { "ok  " } else { "FAIL" }
    Write-Host "$Mark $($Result.page)"
}

# This only distinguishes no presentation from insufficient visible variation. It must not be
# read as proof that the requested page, its accessibility tree, or its map/data tiles are ready.
$Problems = @()
if ($NoWindow.Count -gt 0) { $Problems += "no window: $(($NoWindow | ForEach-Object { $_.page }) -join ', ')" }
if ($Blank.Count -gt 0) { $Problems += "insufficient visual variation: $(($Blank | ForEach-Object { $_.page }) -join ', ')" }

if ($Problems.Count -gt 0) {
    throw ($Problems -join " · ")
}

Write-Host "All $($Results.Count) destinations showed responsive visual variation; semantic readiness remains deferred to #281."
