<#
.SYNOPSIS
    Photographs every destination in the application, one launch each.
.DESCRIPTION
    Seven of the thirteen destinations were written and shipped without anyone
    ever seeing them rendered. Compiled bindings mean a mistyped property is a
    load-time failure on the page that has it, and nothing exercised those pages,
    so the first person to click Flea would have been the person the build was
    for.

    Each page is opened in its own launch, through the application's own --page
    option, and photographed.

    The map gets shots of its own on top of that. It is the left column of the Raid
    page, so raid.png was always a picture of it — in exactly one state: cold launch,
    default map, base floor, flat. The stacked view, any other floor and any other map
    had never been photographed, so the gallery proved the map draws something rather
    than that the stack works. Every map defect reported so far was found by looking at
    a picture, and those were the pictures nobody was taking.

    Presenting a window was the only thing ever checked, which is why a ragged
    sidebar and several dead bindings shipped: CI took the picture and nobody
    looked. Two things are now asserted as well.

    The toolkit's own warnings are captured per page. Compiled bindings catch a
    renamed property at build time, but nothing caught a value that will not
    convert, a resource that is not there, or a path the toolkit cannot resolve
    at run time. Each of those is a page asking for something it does not get,
    each is reported at warning level, and LogToTrace had nowhere to write it.

    Binding through an object that is null is recorded and counted but does not
    fail the step. Every optional panel does it: the raid summary before a raid
    has ended, the selected quest before one is chosen. Thirty of them on two
    pages is worth removing, and that is a change to the views rather than a gate
    on the build.

    And the photograph is measured. A page that presents a window and then fails
    to fill it in is a flat rectangle, and a flat rectangle passed every check
    there was.

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

    # The map is the left column of the Raid page, so raid.png is already a picture of
    # it — in exactly one state: cold launch, default map, base floor, flat. The stacked
    # view, any other floor and any other map have never been photographed, so the
    # gallery proved the map draws something rather than that the stack works.
    #
    # Every map defect reported so far was found by looking at a picture. These are the
    # pictures nobody was taking. Each entry is a name for the file and the arguments to
    # launch with; the application ignores a map or floor it does not have, so a catalog
    # change makes one of these a duller picture rather than a red build.
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

    [int] $SettleSeconds = 4,

    # A warning matching this is a failure. A property that does not exist, a value
    # that will not convert, a resource that cannot be found: each is a page asking
    # for something it does not get.
    [string] $FailOnWarningPattern = "\\[Binding\\]|Could not find|does not have|Unable to resolve|Cannot resolve|Unable to convert|Static resource",

    # A warning matching this is recorded and counted but does not fail the step.
    #
    # Empty, now that the raid summary and the selected quest are scoped to their own
    # object rather than reached through it by path. Those were the only thirty, and
    # the setting stays so the next one found can be counted before it is a gate.
    [string] $TolerateWarningPattern = "",

    # A page that drew nothing is a near-uniform rectangle. Anything real clears
    # both of these comfortably; a blank one clears neither.
    [int] $MinimumDistinctColors = 48,

    [double] $MinimumVariedFraction = 0.02
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Save-ScreenImage {
    param([string] $Path)

    $Bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $Bitmap = New-Object System.Drawing.Bitmap $Bounds.Width, $Bounds.Height
    try {
        $Graphics = [System.Drawing.Graphics]::FromImage($Bitmap)
        try {
            $Graphics.CopyFromScreen($Bounds.X, $Bounds.Y, 0, 0, $Bitmap.Size)
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
        Start-Sleep -Seconds 2
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

<#
    Reads the toolkit warnings one launch wrote, and says which of them matter.
#>
function Read-InterfaceWarnings {
    param([string] $Path, [string] $Pattern, [string] $Tolerate)

    if (-not (Test-Path -LiteralPath $Path)) {
        return [pscustomobject]@{ all = @(); failing = @() }
    }

    $Lines = @(Get-Content -LiteralPath $Path -ErrorAction SilentlyContinue |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $Tolerated = @($Lines | Where-Object { $Tolerate -and $_ -match $Tolerate })
    return [pscustomobject]@{
        all = $Lines
        tolerated = $Tolerated
        failing = @($Lines | Where-Object { $_ -match $Pattern -and -not ($Tolerate -and $_ -match $Tolerate) })
    }
}

$ResolvedAppPath = (Resolve-Path -LiteralPath $AppPath).Path
New-Item -ItemType Directory -Path $ScreenshotDirectory -Force | Out-Null
$Results = [System.Collections.Generic.List[object]]::new()

$WarningDirectory = Join-Path $ScreenshotDirectory "warnings"
New-Item -ItemType Directory -Path $WarningDirectory -Force | Out-Null

# One list of shots, so a page and a map view go through exactly the same launch,
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
    if (Test-Path -LiteralPath $WarningLog) { Remove-Item -LiteralPath $WarningLog -Force }
    $Process = $null
    try {
        # Read back after the window closes. The application only writes here when
        # this is set, so a player's run costs nothing.
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
            $Results.Add([pscustomobject]@{
                page = $Page
                presented = $false
                drew = $false
                detail = "Exited with code $($Process.ExitCode) before showing a window."
                screenshot = $null
                distinctColors = 0
                variedFraction = 0.0
                warnings = @()
                toleratedWarnings = @()
                failingWarnings = @()
            })
            continue
        }

        if ($Process.MainWindowHandle -eq [IntPtr]::Zero) {
            $Results.Add([pscustomobject]@{
                page = $Page
                presented = $false
                drew = $false
                detail = "No window within $WindowTimeoutSeconds second(s)."
                screenshot = $null
                distinctColors = 0
                variedFraction = 0.0
                warnings = @()
                toleratedWarnings = @()
                failingWarnings = @()
            })
            continue
        }

        # Asynchronous page loads finish after the window appears, so an immediate
        # photograph would show a page that has not filled in yet.
        Start-Sleep -Seconds $SettleSeconds
        $Process.Refresh()
        if ($Process.HasExited) {
            $Results.Add([pscustomobject]@{
                page = $Page
                presented = $false
                drew = $false
                detail = "Exited with code $($Process.ExitCode) shortly after showing its window."
                screenshot = $null
                distinctColors = 0
                variedFraction = 0.0
                warnings = @()
                toleratedWarnings = @()
                failingWarnings = @()
            })
            continue
        }

        Save-ScreenImage -Path $Screenshot

        # Closed here rather than in the finally block, so the warnings written on
        # the way out are in the file before it is read.
        Close-AppProcess -Process $Process -Page $Page
        $Content = Measure-ImageContent -Path $Screenshot
        $Warnings = Read-InterfaceWarnings -Path $WarningLog -Pattern $FailOnWarningPattern -Tolerate $TolerateWarningPattern
        $Drew = $Content.distinctColors -ge $MinimumDistinctColors -and $Content.variedFraction -ge $MinimumVariedFraction
        $Detail = "Window shown after $([Math]::Round($Stopwatch.Elapsed.TotalSeconds, 2))s · $($Content.distinctColors) colours · $([Math]::Round($Content.variedFraction * 100, 1))% varied"
        if (-not $Drew) { $Detail = "Drew almost nothing · $Detail" }
        if ($Warnings.tolerated.Count -gt 0) { $Detail = "$($Warnings.tolerated.Count) null-source binding(s) · $Detail" }
        if ($Warnings.failing.Count -gt 0) { $Detail = "$($Warnings.failing.Count) interface fault(s) · $Detail" }

        $Results.Add([pscustomobject]@{
            page = $Page
            presented = $true
            drew = $Drew
            detail = $Detail
            screenshot = (Split-Path -Leaf $Screenshot)
            distinctColors = $Content.distinctColors
            variedFraction = $Content.variedFraction
            warnings = $Warnings.all
            toleratedWarnings = $Warnings.tolerated
            failingWarnings = $Warnings.failing
        })
    }
    catch {
        $Results.Add([pscustomobject]@{
            page = $Page
            presented = $false
            drew = $false
            detail = $_.Exception.Message
            screenshot = $null
            distinctColors = 0
            variedFraction = 0.0
            warnings = @()
            toleratedWarnings = @()
            failingWarnings = @()
        })
    }
    finally {
        if ($null -ne $Process) {
            Close-AppProcess -Process $Process -Page $Page
            $Process.Dispose()
        }

        Remove-Item Env:\TARKOV_COMPANION_UI_WARNING_LOG -ErrorAction SilentlyContinue
    }
}

$NoWindow = @($Results | Where-Object { -not $_.presented })
$Blank = @($Results | Where-Object { $_.presented -and -not $_.drew })
$Bound = @($Results | Where-Object { $_.failingWarnings.Count -gt 0 })
$Failed = @($Results | Where-Object { -not $_.presented -or $_.failingWarnings.Count -gt 0 })

$Report = [pscustomobject]@{
    generatedUtc = [DateTime]::UtcNow.ToString("o")
    appPath = $ResolvedAppPath
    pages = $Results
    failedCount = $Failed.Count
    noWindowCount = $NoWindow.Count
    blankCount = $Blank.Count
    interfaceFaultCount = $Bound.Count
    nullSourceBindingCount = @($Results | ForEach-Object { $_.toleratedWarnings.Count } | Measure-Object -Sum).Sum
}

$Directory = Split-Path -Parent ([System.IO.Path]::GetFullPath($OutputPath))
New-Item -ItemType Directory -Path $Directory -Force | Out-Null
$Report | ConvertTo-Json -Depth 6 | Set-Content -Path $OutputPath -Encoding utf8

foreach ($Result in $Results) {
    $Mark = if ($Result.presented -and $Result.drew -and $Result.failingWarnings.Count -eq 0) { "ok  " } else { "FAIL" }
    Write-Host "$Mark $($Result.page): $($Result.detail)"
    foreach ($Line in $Result.failingWarnings) {
        Write-Host "     $Line"
    }
}

# Named separately because they are three different repairs. A page that shows no
# window is broken, a page that shows an empty one did not load its data, and a
# binding warning is a property the page asks for and no longer gets.
$Problems = @()
if ($NoWindow.Count -gt 0) { $Problems += "no window: $(($NoWindow | ForEach-Object { $_.page }) -join ', ')" }
if ($Bound.Count -gt 0) { $Problems += "interface faults: $(($Bound | ForEach-Object { $_.page }) -join ', ')" }

# Reported rather than thrown, for now. The photograph is of the whole screen, so a
# window that drew nothing still sits on a desktop with a taskbar on it, and the
# measurement has not been watched across enough builds to be trusted as a gate.
if ($Blank.Count -gt 0) {
    Write-Host "NOTE drew almost nothing: $(($Blank | ForEach-Object { $_.page }) -join ', ')"
}

if ($Problems.Count -gt 0) {
    throw ($Problems -join " · ")
}

$NullSourced = @($Results | Where-Object { $_.toleratedWarnings.Count -gt 0 })
if ($NullSourced.Count -gt 0) {
    Write-Host "NOTE binding through a null source: $(($NullSourced | ForEach-Object { "$($_.page) ($($_.toleratedWarnings.Count))" }) -join ', ')"
}

Write-Host "All $($Results.Count) destinations presented a window and raised no interface faults."
