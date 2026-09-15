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
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Initialize-GalleryBounds {
    if (-not ("TarkovCompanionGalleryBounds" -as [type])) {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class TarkovCompanionGalleryBounds {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr handle, out RECT rect);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool MoveWindow(IntPtr handle, int x, int y, int width, int height, bool repaint);
}
"@
    }
}

function Save-ScreenImage {
    param([string] $Path, [IntPtr] $WindowHandle)

    Initialize-GalleryBounds
    $Bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen

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

function Set-WindowSize {
    param([IntPtr] $WindowHandle, [int] $Width, [int] $Height)

    if ($Width -le 0 -or $Height -le 0) { return }
    Initialize-GalleryBounds
    if (-not [TarkovCompanionGalleryBounds]::MoveWindow($WindowHandle, 12, 12, $Width, $Height, $true)) {
        throw "Could not resize the companion window for its narrow-layout capture."
    }
}

function Find-AutomationElement {
    param(
        [IntPtr] $WindowHandle,
        [string] $AutomationId = "",
        [string] $Name = "",
        [AllowNull()] [object] $ControlType = $null
    )

    $Root = [System.Windows.Automation.AutomationElement]::FromHandle($WindowHandle)
    if ($null -eq $Root) { return $null }
    if ([string]::IsNullOrWhiteSpace($AutomationId) -and
        [string]::IsNullOrWhiteSpace($Name) -and
        $null -eq $ControlType) {
        throw "An automation lookup needs an id, a name, or a control type."
    }
    $Conditions = [System.Collections.Generic.List[System.Windows.Automation.Condition]]::new()
    # Avalonia can retain peers for collapsed controls in the raw automation tree. Interactions
    # and layout assertions concern what the package actually presents, so exclude those peers.
    $Conditions.Add([System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::IsOffscreenProperty,
        $false))
    if (-not [string]::IsNullOrWhiteSpace($AutomationId)) {
        $Conditions.Add([System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            $AutomationId))
    }
    if (-not [string]::IsNullOrWhiteSpace($Name)) {
        $Conditions.Add([System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Name))
    }
    if ($null -ne $ControlType) {
        $Conditions.Add([System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            $ControlType))
    }
    $Condition = if ($Conditions.Count -eq 1) {
        $Conditions[0]
    }
    else {
        [System.Windows.Automation.AndCondition]::new($Conditions.ToArray())
    }
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $Condition)
}

function Wait-AutomationElement {
    param(
        [IntPtr] $WindowHandle,
        [string] $AutomationId = "",
        [string] $Name = "",
        [AllowNull()] [object] $ControlType = $null,
        [int] $TimeoutSeconds = 15
    )

    $Deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $Element = Find-AutomationElement -WindowHandle $WindowHandle -AutomationId $AutomationId -Name $Name -ControlType $ControlType
            if ($null -ne $Element) { return $Element }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $Deadline)
    return $null
}

function Invoke-AutomationElement {
    param([System.Windows.Automation.AutomationElement] $Element, [string] $Description)

    $Pattern = $null
    if (-not $Element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref] $Pattern)) {
        throw "$Description does not expose the UI Automation Invoke pattern."
    }
    ([System.Windows.Automation.InvokePattern] $Pattern).Invoke()
}

function Invoke-ShellInteraction {
    param([IntPtr] $WindowHandle, [object] $Interaction)

    $ControlType = if ($Interaction.targetControlType -eq "Button") {
        [System.Windows.Automation.ControlType]::Button
    }
    else {
        $null
    }
    $Target = Wait-AutomationElement `
        -WindowHandle $WindowHandle `
        -AutomationId $Interaction.targetAutomationId `
        -Name $Interaction.targetName `
        -ControlType $ControlType
    if ($null -eq $Target) { throw "Interaction target '$($Interaction.description)' was not in the packaged app's automation tree." }
    Invoke-AutomationElement -Element $Target -Description $Interaction.description

    foreach ($ExpectedId in @($Interaction.expectedAutomationIds)) {
        if ($null -eq (Wait-AutomationElement -WindowHandle $WindowHandle -AutomationId $ExpectedId)) {
            throw "'$($Interaction.description)' did not expose expected element '$ExpectedId'."
        }
    }
    foreach ($ExpectedName in @($Interaction.expectedNames)) {
        if ($null -eq (Wait-AutomationElement -WindowHandle $WindowHandle -Name $ExpectedName)) {
            throw "'$($Interaction.description)' did not expose expected element '$ExpectedName'."
        }
    }
    foreach ($ForbiddenId in @($Interaction.forbiddenAutomationIds)) {
        if ($null -ne (Find-AutomationElement -WindowHandle $WindowHandle -AutomationId $ForbiddenId)) {
            throw "'$($Interaction.description)' exposed layout element '$ForbiddenId' that should be absent."
        }
    }

    return "Invoked $($Interaction.description) and observed the expected packaged-shell state."
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
    param([string] $Page, [string] $ShellMode, [bool] $InteractionRequired)

    return [pscustomobject]@{
        page = $Page
        shellMode = $ShellMode
        presented = $false
        visuallyVaried = $false
        interactionRequired = $InteractionRequired
        interactionSmoke = -not $InteractionRequired
        interactionDetail = if ($InteractionRequired) { "The packaged-shell interaction did not complete." } else { "Visual capture only." }
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
    $Shots.Add([pscustomobject]@{
        name = $Name; args = @("--ui-shell", "legacy", "--page", $Name); shellMode = "legacy"
        width = 0; height = 0; interaction = $null
    })
}
foreach ($View in $MapViews) {
    $Shots.Add([pscustomobject]@{
        name = $View.name; args = @("--ui-shell", "legacy") + $View.args; shellMode = "legacy"
        width = 0; height = 0; interaction = $null
    })
}

# One packaged launch per shell crosses a real UI Automation boundary before its screenshot.
# The two narrow shots also prove that Variant A's rail really becomes the labelled row rather
# than merely claiming a breakpoint in a view model.
$Shots.Add([pscustomobject]@{
    name = "shell-legacy"; args = @("--ui-shell", "legacy", "--page", "Raid"); shellMode = "legacy"
    width = 0; height = 0
    interaction = [pscustomobject]@{
        description = "legacy Settings destination"; targetAutomationId = ""; targetName = "Settings"; targetControlType = "Button"
        expectedAutomationIds = @(); expectedNames = @("Interface size"); forbiddenAutomationIds = @("v2-shell-page-heading")
    }
})
$Shots.Add([pscustomobject]@{
    name = "shell-v2-a"; args = @("--ui-shell", "v2-a", "--page", "setup"); shellMode = "v2-a"
    width = 0; height = 0
    interaction = [pscustomobject]@{
        description = "Variant A Intel destination"; targetAutomationId = "v2-shell-destination-items"; targetName = ""; targetControlType = "Button"
        expectedAutomationIds = @("v2-shell-workspace-search", "v2-shell-navigation-rail"); expectedNames = @()
        forbiddenAutomationIds = @("v2-shell-header-search", "v2-shell-navigation-row")
    }
})
$Shots.Add([pscustomobject]@{
    name = "shell-v2-b"; args = @("--ui-shell", "v2-b", "--page", "home"); shellMode = "v2-b"
    width = 0; height = 0
    interaction = [pscustomobject]@{
        description = "Variant B Raid destination"; targetAutomationId = "v2-shell-destination-raid"; targetName = ""; targetControlType = "Button"
        expectedAutomationIds = @("v2-shell-header-search", "v2-shell-navigation-row"); expectedNames = @()
        forbiddenAutomationIds = @("v2-shell-workspace-search", "v2-shell-navigation-rail")
    }
})
$Shots.Add([pscustomobject]@{
    name = "shell-v2-a-narrow"; args = @("--ui-shell", "v2-a", "--page", "setup"); shellMode = "v2-a"
    width = 560; height = 820
    interaction = [pscustomobject]@{
        description = "narrow Variant A Intel destination"; targetAutomationId = "v2-shell-destination-items"; targetName = ""; targetControlType = "Button"
        expectedAutomationIds = @("v2-shell-workspace-search", "v2-shell-navigation-row"); expectedNames = @()
        forbiddenAutomationIds = @("v2-shell-header-search", "v2-shell-navigation-rail")
    }
})
$Shots.Add([pscustomobject]@{
    name = "shell-v2-b-narrow"; args = @("--ui-shell", "v2-b", "--page", "home"); shellMode = "v2-b"
    width = 560; height = 820
    interaction = [pscustomobject]@{
        description = "narrow Variant B Raid destination"; targetAutomationId = "v2-shell-destination-raid"; targetName = ""; targetControlType = "Button"
        expectedAutomationIds = @("v2-shell-header-search", "v2-shell-navigation-row"); expectedNames = @()
        forbiddenAutomationIds = @("v2-shell-workspace-search", "v2-shell-navigation-rail")
    }
})

foreach ($Shot in $Shots) {
    $Page = $Shot.name
    $Screenshot = Join-Path $ScreenshotDirectory ("{0}.png" -f $Page.ToLowerInvariant())
    $WarningLog = Join-Path $WarningDirectory ("{0}.log" -f $Page.ToLowerInvariant())
    $Result = New-ShotResult -Page $Page -ShellMode $Shot.shellMode -InteractionRequired ($null -ne $Shot.interaction)
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

        if ($Shot.width -gt 0 -and $Shot.height -gt 0) {
            Set-WindowSize -WindowHandle $Process.MainWindowHandle -Width $Shot.width -Height $Shot.height
            Start-Sleep -Milliseconds 500
        }

        if ($null -ne $Shot.interaction) {
            $Result.interactionDetail = Invoke-ShellInteraction -WindowHandle $Process.MainWindowHandle -Interaction $Shot.interaction
            $Result.interactionSmoke = $true
            Start-Sleep -Milliseconds 300
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
$InteractionFailed = @($Results | Where-Object { $_.interactionRequired -and -not $_.interactionSmoke })
$Failed = @($Results | Where-Object {
    -not $_.presented -or -not $_.visuallyVaried -or $_.interfaceFaultCount -gt 0 -or
        -not $_.warningCaptureArmed -or ($_.interactionRequired -and -not $_.interactionSmoke)
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
    interactionFailureCount = $InteractionFailed.Count
    scope = "Responsive visual variation, packaged-shell UI Automation interactions, and toolkit interface faults; semantic expected-page, accessibility, and data/tile readiness are not proven here and remain an open #279 criterion that depends on the application readiness signal owned by #281."
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
if ($InteractionFailed.Count -gt 0) { $Problems += "packaged-shell interaction failed: $(($InteractionFailed | ForEach-Object { $_.page }) -join ', ')" }

if ($Problems.Count -gt 0) {
    throw ($Problems -join "; ")
}

Write-Host "All $($Results.Count) launches showed responsive visual variation; every required packaged-shell interaction passed with an armed warning capture and no interface faults. Semantic page, accessibility and data/tile readiness are not proven here."
