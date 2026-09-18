<#
.SYNOPSIS
    Photographs every destination in the application, one launch each.
.DESCRIPTION
    Captures responsive visual variation for each destination and fails any launch
    whose interface toolkit reported a fault. V2 launches additionally assert every retained
    route, selected destination, key focus-restoration paths, named dialogs, and live titles
    through UI Automation. This is still not full usability/accessibility validation or proof
    that data and map tiles are ready; that remains an open #279 criterion and depends on the
    application readiness signal owned by #281.

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
    # V2 rough package 30 (acceptance sweep): the 12px offset is breathing room for a window
    # smaller than the desktop. A window the size of the desktop — which the 1920x1080 captures
    # are, on a 1920x1080 runner — has to start in its corner, or its right and bottom edges fall
    # off the screen and the photograph is of a cropped layout.
    $Desktop = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $Left = if (($Width + 24) -le $Desktop.Width) { 12 } else { $Desktop.Left }
    $Top = if (($Height + 24) -le $Desktop.Height) { 12 } else { $Desktop.Top }
    if (-not [TarkovCompanionGalleryBounds]::MoveWindow($WindowHandle, $Left, $Top, $Width, $Height, $true)) {
        throw "Could not resize the companion window for its narrow-layout capture."
    }
}

function Find-AutomationElement {
    param(
        [IntPtr] $WindowHandle,
        [string] $AutomationId = "",
        [string] $Name = "",
        [AllowNull()] [object] $ControlType = $null,
        [bool] $IncludeOffscreen = $false
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
    if (-not $IncludeOffscreen) {
        $Conditions.Add([System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::IsOffscreenProperty,
            $false))
    }
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
        [bool] $IncludeOffscreen = $false,
        [int] $TimeoutSeconds = 15
    )

    $Deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $Element = Find-AutomationElement `
                -WindowHandle $WindowHandle `
                -AutomationId $AutomationId `
                -Name $Name `
                -ControlType $ControlType `
                -IncludeOffscreen $IncludeOffscreen
            if ($null -ne $Element) { return $Element }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $Deadline)
    return $null
}

function Wait-AutomationOutsideViewportElement {
    param(
        [IntPtr] $WindowHandle,
        [string] $AutomationId,
        [int] $TimeoutSeconds = 15
    )

    # Avalonia retains peers for content laid out below a scroll viewport, but its Windows UIA
    # provider does not always set IsOffscreen for those clipped peers. Require either the native
    # offscreen flag or a bounding rectangle with no intersection with the packaged window.
    $Deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $Root = [System.Windows.Automation.AutomationElement]::FromHandle($WindowHandle)
            $Element = Find-AutomationElement `
                -WindowHandle $WindowHandle `
                -AutomationId $AutomationId `
                -IncludeOffscreen $true
            if ($null -ne $Root -and $null -ne $Element) {
                $Viewport = $Root.Current.BoundingRectangle
                $Bounds = $Element.Current.BoundingRectangle
                $HorizontalOverlap = $Bounds.Right -gt $Viewport.Left -and $Bounds.Left -lt $Viewport.Right
                $VerticalOverlap = $Bounds.Bottom -gt $Viewport.Top -and $Bounds.Top -lt $Viewport.Bottom
                $IntersectsViewport = $Bounds.Width -gt 0 -and $Bounds.Height -gt 0 -and $HorizontalOverlap -and $VerticalOverlap
                if ($Element.Current.IsOffscreen -or -not $IntersectsViewport) { return $Element }
            }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $Deadline)
    return $null
}

function Get-InteractionProperty {
    param([AllowNull()] [object] $Object, [string] $Name, [AllowNull()] [object] $Default = $null)

    if ($null -eq $Object) { return $Default }
    $Property = $Object.PSObject.Properties[$Name]
    if ($null -eq $Property) { return $Default }
    return $Property.Value
}

function Get-AutomationControlType {
    param([string] $Name)

    switch ($Name) {
        "Button" { return [System.Windows.Automation.ControlType]::Button }
        "Edit" { return [System.Windows.Automation.ControlType]::Edit }
        "Window" { return [System.Windows.Automation.ControlType]::Window }
    }
    return $null
}

function Wait-AutomationFocus {
    param([string] $AutomationId, [int] $TimeoutSeconds = 15)

    $Deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $Focused = [System.Windows.Automation.AutomationElement]::FocusedElement
            if ($null -ne $Focused -and $Focused.Current.AutomationId -ceq $AutomationId) {
                return $Focused
            }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $Deadline)
    return $null
}

function Wait-AutomationWindowName {
    param([IntPtr] $WindowHandle, [string] $Name, [int] $TimeoutSeconds = 15)

    $Deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $Root = [System.Windows.Automation.AutomationElement]::FromHandle($WindowHandle)
            if ($null -ne $Root -and $Root.Current.Name -ceq $Name) { return $Root }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $Deadline)
    return $null
}

function Wait-AutomationItemStatus {
    param([IntPtr] $WindowHandle, [string] $AutomationId, [string] $Status, [int] $TimeoutSeconds = 15)

    $Deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $Element = Find-AutomationElement -WindowHandle $WindowHandle -AutomationId $AutomationId
            if ($null -ne $Element -and $Element.Current.ItemStatus -ceq $Status) { return $Element }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $Deadline)
    return $null
}

function Wait-AutomationNamePattern {
    param(
        [IntPtr] $WindowHandle,
        [string] $AutomationId,
        [string] $Pattern,
        [bool] $IncludeOffscreen = $false,
        [int] $TimeoutSeconds = 15
    )

    # The peer already exists before a page or filter command runs. Waiting only for its ID
    # returned the old Name immediately and raced the command's binding update.
    $Deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $Element = Find-AutomationElement `
                -WindowHandle $WindowHandle `
                -AutomationId $AutomationId `
                -IncludeOffscreen $IncludeOffscreen
            if ($null -ne $Element -and $Element.Current.Name -match $Pattern) { return $Element }
        }
        catch [System.Windows.Automation.ElementNotAvailableException] {
        }
        Start-Sleep -Milliseconds 100
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

function Toggle-AutomationElement {
    param([System.Windows.Automation.AutomationElement] $Element, [string] $Description)

    $Pattern = $null
    if (-not $Element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref] $Pattern)) {
        throw "$Description does not expose the UI Automation Toggle pattern."
    }
    ([System.Windows.Automation.TogglePattern] $Pattern).Toggle()
}

function Set-AutomationValue {
    param(
        [System.Windows.Automation.AutomationElement] $Element,
        [string] $Value,
        [string] $Description
    )

    $Pattern = $null
    if (-not $Element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref] $Pattern)) {
        throw "$Description does not expose the UI Automation Value pattern."
    }
    ([System.Windows.Automation.ValuePattern] $Pattern).SetValue($Value)
}

function Invoke-ShellInteraction {
    param([IntPtr] $WindowHandle, [object] $Interaction)

    $StepsProperty = Get-InteractionProperty -Object $Interaction -Name "steps"
    $Steps = if ($null -eq $StepsProperty) { @($Interaction) } else { @($StepsProperty) }
    $Completed = [System.Collections.Generic.List[string]]::new()
    foreach ($Step in $Steps) {
        $Description = [string](Get-InteractionProperty -Object $Step -Name "description" -Default "packaged-shell step")
        $Action = [string](Get-InteractionProperty -Object $Step -Name "action" -Default "invoke")
        $TargetId = [string](Get-InteractionProperty -Object $Step -Name "targetAutomationId" -Default "")
        $TargetName = [string](Get-InteractionProperty -Object $Step -Name "targetName" -Default "")
        $ControlTypeName = [string](Get-InteractionProperty -Object $Step -Name "targetControlType" -Default "")
        $ControlType = Get-AutomationControlType -Name $ControlTypeName
        $IncludeOffscreen = [bool](Get-InteractionProperty -Object $Step -Name "includeOffscreen" -Default $false)

        if ($Action -eq "resize") {
            Set-WindowSize `
                -WindowHandle $WindowHandle `
                -Width ([int](Get-InteractionProperty -Object $Step -Name "width" -Default 0)) `
                -Height ([int](Get-InteractionProperty -Object $Step -Name "height" -Default 0))
        }
        elseif ($Action -ne "assert") {
            $Target = Wait-AutomationElement `
                -WindowHandle $WindowHandle `
                -AutomationId $TargetId `
                -Name $TargetName `
                -ControlType $ControlType `
                -IncludeOffscreen $IncludeOffscreen
            if ($null -eq $Target) { throw "Interaction target '$Description' was not in the packaged app's automation tree." }
            switch ($Action) {
                "invoke" { Invoke-AutomationElement -Element $Target -Description $Description }
                "toggle" { Toggle-AutomationElement -Element $Target -Description $Description }
                "focus" { $Target.SetFocus() }
                "set-value" {
                    Set-AutomationValue `
                        -Element $Target `
                        -Value ([string](Get-InteractionProperty -Object $Step -Name "value" -Default "")) `
                        -Description $Description
                }
                default { throw "Unknown packaged-shell interaction action '$Action'." }
            }
        }

        foreach ($ExpectedId in @(Get-InteractionProperty -Object $Step -Name "expectedAutomationIds" -Default @())) {
            if ($null -eq (Wait-AutomationElement -WindowHandle $WindowHandle -AutomationId $ExpectedId)) {
                throw "'$Description' did not expose expected element '$ExpectedId'."
            }
        }
        foreach ($ExpectedId in @(Get-InteractionProperty -Object $Step -Name "expectedOutsideViewportAutomationIds" -Default @())) {
            if ($null -eq (Wait-AutomationOutsideViewportElement `
                -WindowHandle $WindowHandle `
                -AutomationId $ExpectedId)) {
                throw "'$Description' expected '$ExpectedId' outside the packaged window viewport within 15 seconds."
            }
        }
        foreach ($BoundsAssertion in @(Get-InteractionProperty -Object $Step -Name "expectedBounds" -Default @())) {
            # V2 rough package 30 (acceptance sweep): named as well as identified. The V1 pages
            # the V2 shell still hosts give their controls no automation id, and their controls
            # are exactly the ones this measures.
            $BoundsId = [string](Get-InteractionProperty -Object $BoundsAssertion -Name "automationId" -Default "")
            $BoundsName = [string](Get-InteractionProperty -Object $BoundsAssertion -Name "name" -Default "")
            $BoundsLabel = if ($BoundsId) { $BoundsId } else { $BoundsName }
            $BoundsElement = Wait-AutomationElement `
                -WindowHandle $WindowHandle `
                -AutomationId $BoundsId `
                -Name $BoundsName `
                -ControlType (Get-AutomationControlType -Name ([string](Get-InteractionProperty -Object $BoundsAssertion -Name "controlType" -Default "")))
            if ($null -eq $BoundsElement) {
                throw "'$Description' did not expose bounded element '$BoundsLabel'."
            }
            $Bounds = $BoundsElement.Current.BoundingRectangle
            $MinimumWidth = [double](Get-InteractionProperty -Object $BoundsAssertion -Name "minimumWidth" -Default 0)
            $MinimumHeight = [double](Get-InteractionProperty -Object $BoundsAssertion -Name "minimumHeight" -Default 0)
            if ($Bounds.Width -lt $MinimumWidth -or $Bounds.Height -lt $MinimumHeight) {
                throw "'$Description' measured '$BoundsLabel' at $($Bounds.Width)x$($Bounds.Height), below ${MinimumWidth}x${MinimumHeight}."
            }

            # V2 rough package 30 (acceptance sweep): a control the player is expected to press
            # must actually be on the window. Every V1 page hosted inside the V2 shell drew
            # without the page inset V1 gives it, so Ammo/Keys "Reload", Flea "Look up value",
            # Loadout "Empty the kit" and Events "Create" were sliced by the window frame at
            # 1920 and 3840 wide. UI Automation still found them, which is why the gallery
            # passed; their bounding rectangles say what a photograph shows.
            if ([bool](Get-InteractionProperty -Object $BoundsAssertion -Name "insideWindow" -Default $false)) {
                Initialize-GalleryBounds
                $WindowRect = New-Object TarkovCompanionGalleryBounds+RECT
                if (-not [TarkovCompanionGalleryBounds]::GetWindowRect($WindowHandle, [ref] $WindowRect)) {
                    throw "'$Description' could not read the packaged window bounds."
                }

                # The frame's own border, not the page: a control inside it is still visible.
                $Slack = 12
                if ($Bounds.Left -lt ($WindowRect.Left - $Slack) -or
                    $Bounds.Top -lt ($WindowRect.Top - $Slack) -or
                    $Bounds.Right -gt ($WindowRect.Right + $Slack) -or
                    $Bounds.Bottom -gt ($WindowRect.Bottom + $Slack)) {
                    throw ("'$Description' left '$BoundsLabel' outside the window: " +
                        "control [$($Bounds.Left),$($Bounds.Top),$($Bounds.Right),$($Bounds.Bottom)] " +
                        "against window [$($WindowRect.Left),$($WindowRect.Top),$($WindowRect.Right),$($WindowRect.Bottom)].")
                }
            }
        }
        foreach ($ExpectedName in @(Get-InteractionProperty -Object $Step -Name "expectedNames" -Default @())) {
            if ($null -eq (Wait-AutomationElement -WindowHandle $WindowHandle -Name $ExpectedName)) {
                throw "'$Description' did not expose expected element '$ExpectedName'."
            }
        }
        foreach ($ForbiddenId in @(Get-InteractionProperty -Object $Step -Name "forbiddenAutomationIds" -Default @())) {
            if ($null -ne (Find-AutomationElement -WindowHandle $WindowHandle -AutomationId $ForbiddenId)) {
                throw "'$Description' exposed layout element '$ForbiddenId' that should be absent."
            }
        }

        $ExpectedHeading = [string](Get-InteractionProperty -Object $Step -Name "expectedHeading" -Default "")
        if (-not [string]::IsNullOrWhiteSpace($ExpectedHeading) -and
            $null -eq (Wait-AutomationElement -WindowHandle $WindowHandle -AutomationId "v2-shell-page-heading" -Name $ExpectedHeading)) {
            throw "'$Description' did not expose page heading '$ExpectedHeading'."
        }

        $ExpectedFocus = [string](Get-InteractionProperty -Object $Step -Name "expectedFocusAutomationId" -Default "")
        if (-not [string]::IsNullOrWhiteSpace($ExpectedFocus) -and
            $null -eq (Wait-AutomationFocus -AutomationId $ExpectedFocus)) {
            throw "'$Description' did not leave keyboard focus on '$ExpectedFocus'."
        }

        $ExpectedWindowName = [string](Get-InteractionProperty -Object $Step -Name "expectedWindowName" -Default "")
        if (-not [string]::IsNullOrWhiteSpace($ExpectedWindowName) -and
            $null -eq (Wait-AutomationWindowName -WindowHandle $WindowHandle -Name $ExpectedWindowName)) {
            throw "'$Description' did not update the window title to '$ExpectedWindowName'."
        }

        $ExpectedDialogName = [string](Get-InteractionProperty -Object $Step -Name "expectedDialogName" -Default "")
        if (-not [string]::IsNullOrWhiteSpace($ExpectedDialogName) -and
            $null -eq (Wait-AutomationElement `
                -WindowHandle $WindowHandle `
                -AutomationId "v2-shell-dialog" `
                -Name $ExpectedDialogName `
                -ControlType ([System.Windows.Automation.ControlType]::Window))) {
            throw "'$Description' did not expose a named UI Automation dialog peer."
        }

        foreach ($CurrentId in @(Get-InteractionProperty -Object $Step -Name "expectedCurrentAutomationIds" -Default @())) {
            if ($null -eq (Wait-AutomationItemStatus -WindowHandle $WindowHandle -AutomationId $CurrentId -Status "Current page")) {
                throw "'$Description' did not expose '$CurrentId' as the current page."
            }
        }

        foreach ($NameAssertion in @(Get-InteractionProperty -Object $Step -Name "expectedNamePatterns" -Default @())) {
            if ($null -eq (Wait-AutomationNamePattern `
                -WindowHandle $WindowHandle `
                -AutomationId $NameAssertion.automationId `
                -Pattern $NameAssertion.pattern `
                -IncludeOffscreen ([bool](Get-InteractionProperty -Object $NameAssertion -Name "includeOffscreen" -Default $false)))) {
                throw "'$Description' did not expose '$($NameAssertion.automationId)' with name matching '$($NameAssertion.pattern)' within 15 seconds."
            }
        }

        $Completed.Add($Description)
    }

    return "Completed $($Completed.Count) packaged-shell step(s): $($Completed -join '; ')."
}

# Matches the launch probe: the window asks for more room than a hosted runner's
# default desktop has, and a cropped photograph of a layout is not evidence.
<#
    Whether the desktop can already show a window this size.

    V2 rough package 30 (acceptance sweep): a photograph of a window cropped by a smaller desktop
    is not evidence of that window's layout, so a shot that cannot be given its room is skipped
    with its reason recorded rather than photographed and judged.

    It reads the desktop; it does not change it. An earlier version asked for the shot's size plus
    a margin before every sized launch, which drove a display-mode change between launches and
    left the V2 route pass unable to get a window at all while the scenarios around it were fine.
    The desktop is set once, below, exactly as it was before this pass existed.
#>
function Test-DesktopFits {
    param([int] $Width, [int] $Height)

    $Current = [System.Windows.Forms.SystemInformation]::VirtualScreen
    return ($Current.Width -ge $Width -and $Current.Height -ge $Height)
}

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
    V2 rough package 30 (acceptance sweep): measures dead area, which "visually varied" does not.

    A page can be full of colour and still be mostly nothing: the Intel workspace kept a 460px
    context panel drawn empty whenever no item was selected, a quarter of a 1920-wide window; on
    a 3840x1080 ultrawide the map workspaces drew a plan across barely half their map card and
    left the rest as slate. Both passed every existing check.

    Two numbers, both taken from sampled rows down the body of the window and reported as medians
    so one banner or one toolbar row cannot move them:

      edgeDeadFraction  - how far a single flat colour runs inward from the right edge.
      flatBandFraction  - the widest run of one flat colour anywhere in the row.

    Neither is a judgement about design. They are bounds: a page that was fixed must not quietly
    go back to leaving that much of the window empty.
#>
function Measure-DeadSpace {
    param([string] $Path, [int] $LeftInset = 176)

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

        # Past the navigation rail, and clear of the top bar and the bottom edge: those are
        # chrome, and chrome is meant to be a flat colour.
        $Left = [Math]::Min($LeftInset, [Math]::Max(0, $Bitmap.Width - 8))
        $Top = [int]($Bitmap.Height * 0.2)
        $Bottom = [int]($Bitmap.Height * 0.85)
        $Step = 4
        $Body = [Math]::Max(1, $Bitmap.Width - $Left)
        $EdgeRuns = [System.Collections.Generic.List[double]]::new()
        $BandRuns = [System.Collections.Generic.List[double]]::new()
        # The pixel read is written out rather than put behind a helper: this walks tens of
        # thousands of samples per capture, and a scriptblock call for each one costs more than
        # the whole measurement.
        for ($Y = $Top; $Y -lt $Bottom; $Y += 24) {
            $Row = $Y * $Stride
            $EdgeX = $Bitmap.Width - 1
            $Offset = $Row + ($EdgeX * 4)
            $EdgeColour = ($Bytes[$Offset + 2] -shl 16) -bor ($Bytes[$Offset + 1] -shl 8) -bor $Bytes[$Offset]
            $EdgeRun = 0
            for ($X = $EdgeX; $X -ge $Left; $X -= $Step) {
                $Offset = $Row + ($X * 4)
                $Colour = ($Bytes[$Offset + 2] -shl 16) -bor ($Bytes[$Offset + 1] -shl 8) -bor $Bytes[$Offset]
                if ($Colour -ne $EdgeColour) { break }
                $EdgeRun += $Step
            }
            $EdgeRuns.Add([Math]::Min($EdgeRun, $Body) / $Body)

            $Widest = 0
            $Run = 0
            $Previous = -1
            for ($X = $Left; $X -lt $Bitmap.Width; $X += $Step) {
                $Offset = $Row + ($X * 4)
                $Colour = ($Bytes[$Offset + 2] -shl 16) -bor ($Bytes[$Offset + 1] -shl 8) -bor $Bytes[$Offset]
                if ($Colour -eq $Previous) { $Run += $Step } else { $Run = $Step; $Previous = $Colour }
                if ($Run -gt $Widest) { $Widest = $Run }
            }
            $BandRuns.Add([Math]::Min($Widest, $Body) / $Body)
        }

        if ($EdgeRuns.Count -eq 0) {
            return [pscustomobject]@{ edgeDeadFraction = 0.0; flatBandFraction = 0.0 }
        }

        $Median = {
            param($Values)
            $Sorted = @($Values | Sort-Object)
            return [Math]::Round($Sorted[[int]($Sorted.Count / 2)], 4)
        }
        return [pscustomobject]@{
            edgeDeadFraction = & $Median $EdgeRuns
            flatBandFraction = & $Median $BandRuns
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
        $Forced = $false
        if (-not $Process.HasExited) {
            $null = $Process.CloseMainWindow()
            # Kill() without the process-tree overload: that one is .NET Core only, so under
            # Windows PowerShell it threw, was reported below, and left the launch running into
            # the next one.
            if (-not $Process.WaitForExit(20000)) {
                $Forced = $true
                $Process.Kill()
            }
            $null = $Process.WaitForExit(5000)
        }
        return -not $Forced -and $Process.HasExited
    }
    catch {
        Write-Host "Could not close the process for '$Page': $($_.Exception.Message)"
        return $false
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
        # V2 rough package 30 (acceptance sweep). -1 says "not measured for this shot".
        edgeDeadFraction = -1.0
        flatBandFraction = -1.0
        deadSpaceWithinBounds = $true
        deadSpaceDetail = "Not measured."
        skipped = $false
        warningCaptureArmed = $false
        warningLineCount = 0
        interfaceFaultCount = 0
        interfaceFaults = [string[]]@()
        gracefulShutdown = $false
    }
}

<#
    A focus target the preview store will accept: the text, or nothing at all.

    V2 rough package 30 (acceptance sweep): "" is not "no focus target" to the store. It rejects
    an empty one, and a rejected state is set aside whole, so a seed carrying an address and an
    empty focus silently lost the address as well — which is the only reason the seed exists.
#>
function Resolve-FocusTarget {
    param([AllowNull()] [object] $Value)

    $Text = [string]$Value
    if ([string]::IsNullOrEmpty($Text)) { return $null }
    return $Text
}

function Set-V2PreviewState {
    param([object] $Seed)

    $Mode = [string](Get-InteractionProperty -Object $Seed -Name "variant")
    $Directory = Join-Path $env:LOCALAPPDATA "TarkovCompanion\Config\v2-shell-preview"
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $State = [ordered]@{
        schema = 1
        variant = $Mode
        address = [string](Get-InteractionProperty -Object $Seed -Name "address")
        selectedEntity = $null
        # Null, not "": V2ShellPreviewStore accepts a missing focus target and rejects an empty
        # one, and a rejected state is set aside whole — which would silently discard the address
        # this seed exists to set. V2 rough package 30 seeds an address and no focus.
        focusTarget = Resolve-FocusTarget (Get-InteractionProperty -Object $Seed -Name "focusTarget")
        recents = @()
        pins = @()
        window = $null
        captureShortcutEnabled = $true
    }
    $Json = $State | ConvertTo-Json -Depth 4
    [System.IO.File]::WriteAllText(
        (Join-Path $Directory "$Mode.json"),
        $Json,
        [System.Text.UTF8Encoding]::new($false))
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

function New-NavigationStep {
    param([string] $Description, [string] $AutomationId, [string] $Heading)

    return [pscustomobject]@{
        action = "invoke"
        description = $Description
        targetAutomationId = $AutomationId
        targetControlType = "Button"
        expectedHeading = $Heading
    }
}

function Get-V2WindowName {
    param([string] $Heading)

    # Construct the separator so Windows PowerShell 5.1's legacy source-code decoding cannot
    # misdecode a UTF-8 literal and make an otherwise-correct exact title assertion fail.
    $Separator = [char]0x00B7
    return "Tarkov Companion $Separator $Heading $Separator provisional - #265 not yet run"
}

# The two V2 route traversals activate every retained V1 page through visible section controls
# (or the global palette for B's Search workspace). Focus, dialog, title, deep-link, stale-state,
# reset/close, and wide-to-narrow cases get their own explicit assertions below.
$Shots.Add([pscustomobject]@{
    name = "shell-legacy"; args = @("--ui-shell", "legacy", "--page", "Raid"); shellMode = "legacy"
    width = 0; height = 0
    interaction = [pscustomobject]@{
        description = "legacy Settings destination"; targetAutomationId = ""; targetName = "Settings"; targetControlType = "Button"
        expectedAutomationIds = @(); expectedNames = @("Interface size"); forbiddenAutomationIds = @("v2-shell-page-heading")
    }
})
$Shots.Add([pscustomobject]@{
    # V2 rough package 15: this scenario still exercises Back, which now only draws under
    # --developer-mode (Clayton flagged it as provisional #265 scaffold, not shipped chrome).
    name = "shell-v2-a"; args = @("--ui-shell", "v2-a", "--page", "setup", "--developer-mode"); shellMode = "v2-a"
    width = 0; height = 0
    interaction = [pscustomobject]@{
        steps = @(
            [pscustomobject]@{
                action = "assert"; description = "Variant A required-readiness denominator"
                expectedAutomationIds = @("v2-shell-navigation-rail")
                forbiddenAutomationIds = @("v2-shell-header-search", "v2-shell-navigation-row")
                expectedNamePatterns = @(
                    [pscustomobject]@{ automationId = "v2-shell-readiness-summary"; pattern = '^\d+ of 5 checks ready' },
                    [pscustomobject]@{ automationId = "v2-shell-readiness-game-log"; pattern = '^Open Game log folder\.' }
                )
            },
            [pscustomobject]@{
                action = "invoke"; description = "contextual Profile readiness action"
                targetAutomationId = "v2-shell-readiness-profile"; targetControlType = "Button"
                includeOffscreen = $true
                expectedHeading = "Plan"; expectedFocusAutomationId = "v2-shell-readiness-target-profile"
            },
            [pscustomobject]@{
                action = "invoke"; description = "Back restores the Profile readiness button"
                targetAutomationId = "v2-shell-back"; targetControlType = "Button"
                expectedHeading = "Setup & Admin"; expectedFocusAutomationId = "v2-shell-readiness-profile"
            },
            (New-NavigationStep "Variant A Raid destination" "v2-shell-destination-raid" "Raid"),
            (New-NavigationStep "Variant A Loot decision section" "v2-shell-section-raid.loot" "Loot decision"),
            (New-NavigationStep "Variant A Intel destination" "v2-shell-destination-items" "Intel"),
            (New-NavigationStep "Variant A Ammo section" "v2-shell-section-items.ammo" "Ammo"),
            (New-NavigationStep "Variant A Keys section" "v2-shell-section-items.keys" "Keys"),
            (New-NavigationStep "Variant A Flea section" "v2-shell-section-items.flea" "Flea"),
            (New-NavigationStep "Variant A Stash scan section" "v2-shell-section-stash" "Stash scan"),
            (New-NavigationStep "Variant A Plan destination" "v2-shell-destination-plan" "Plan"),
            (New-NavigationStep "Variant A Hideout section" "v2-shell-section-plan.hideout" "Hideout"),
            (New-NavigationStep "Variant A Loadout section" "v2-shell-section-plan.loadout" "Loadout"),
            (New-NavigationStep "Variant A Events section" "v2-shell-section-plan.events" "Events"),
            (New-NavigationStep "Variant A Team destination" "v2-shell-destination-team" "Team"),
            (New-NavigationStep "Variant A Group section" "v2-shell-section-team.group" "Group"),
            (New-NavigationStep "Variant A Tablet section" "v2-shell-section-team.tablet" "Tablet preview"),
            (New-NavigationStep "Variant A Debrief destination" "v2-shell-destination-debrief" "Debrief"),
            (New-NavigationStep "Variant A Setup destination" "v2-shell-destination-setup" "Setup & Admin")
        )
    }
})
$Shots.Add([pscustomobject]@{
    # V2 rough package 15: this scenario still exercises the Commands palette button, which now
    # only draws under --developer-mode.
    name = "shell-v2-b"; args = @("--ui-shell", "v2-b", "--page", "home", "--developer-mode"); shellMode = "v2-b"
    width = 0; height = 0
    interaction = [pscustomobject]@{
        steps = @(
            [pscustomobject]@{
                action = "assert"; description = "Variant B initial title and layout"
                expectedWindowName = (Get-V2WindowName -Heading "Home")
                # V2 rough package 15: every variant renders the rail at Standard+ width now (the
                # concept renders always show a left rail); Variant B still switches to the row at
                # Compact/Narrow widths, covered by shell-v2-b-narrow below.
                expectedAutomationIds = @("v2-shell-header-search", "v2-shell-navigation-rail")
                forbiddenAutomationIds = @("v2-shell-workspace-search", "v2-shell-navigation-row")
            },
            [pscustomobject]@{
                action = "invoke"; description = "named Capture dialog peer"
                targetAutomationId = "v2-shell-capture"; targetControlType = "Button"
                expectedDialogName = "Capture"; expectedFocusAutomationId = "v2-shell-capture-dialog-title"
            },
            [pscustomobject]@{
                action = "invoke"; description = "Capture close restores its invoker"
                targetAutomationId = "v2-shell-capture-dialog-close"; targetControlType = "Button"
                expectedFocusAutomationId = "v2-shell-capture"
            },
            [pscustomobject]@{
                action = "invoke"; description = "Variant B Raid destination and live title"
                targetAutomationId = "v2-shell-destination-raid"; targetControlType = "Button"
                expectedHeading = "Raid"; expectedWindowName = (Get-V2WindowName -Heading "Raid")
            },
            (New-NavigationStep "Variant B Loot decision section" "v2-shell-section-raid.loot" "Loot decision"),
            [pscustomobject]@{
                action = "invoke"; description = "open Variant B global command palette"
                targetAutomationId = "v2-shell-palette"; targetControlType = "Button"
                expectedDialogName = "Commands"; expectedFocusAutomationId = "v2-shell-palette-dialog-title"
            },
            [pscustomobject]@{
                action = "set-value"; description = "filter palette to retained Items route"
                targetAutomationId = "v2-shell-palette-query"; targetControlType = "Edit"; value = "Items"
                expectedAutomationIds = @("v2-shell-command-go.items")
            },
            (New-NavigationStep "Variant B Search workspace command" "v2-shell-command-go.items" "Search"),
            (New-NavigationStep "Variant B Ammo section" "v2-shell-section-items.ammo" "Ammo"),
            (New-NavigationStep "Variant B Keys section" "v2-shell-section-items.keys" "Keys"),
            (New-NavigationStep "Variant B Flea section" "v2-shell-section-items.flea" "Flea"),
            (New-NavigationStep "Variant B Prepare destination" "v2-shell-destination-plan" "Prepare"),
            (New-NavigationStep "Variant B Hideout section" "v2-shell-section-plan.hideout" "Hideout"),
            (New-NavigationStep "Variant B Loadout section" "v2-shell-section-plan.loadout" "Loadout"),
            (New-NavigationStep "Variant B Events section" "v2-shell-section-plan.events" "Events"),
            (New-NavigationStep "Variant B Stash scan section" "v2-shell-section-stash" "Stash scan"),
            (New-NavigationStep "Variant B Team destination" "v2-shell-destination-team" "Team"),
            (New-NavigationStep "Variant B Group section" "v2-shell-section-team.group" "Group"),
            (New-NavigationStep "Variant B Tablet section" "v2-shell-section-team.tablet" "Tablet preview"),
            (New-NavigationStep "Variant B History destination" "v2-shell-destination-debrief" "History"),
            (New-NavigationStep "Variant B Setup destination" "v2-shell-destination-setup" "Setup")
        )
    }
})
$Shots.Add([pscustomobject]@{
    name = "shell-v2-a-narrow"; args = @("--ui-shell", "v2-a", "--page", "setup"); shellMode = "v2-a"
    width = 0; height = 0
    interaction = [pscustomobject]@{
        steps = @(
            [pscustomobject]@{
                action = "focus"; description = "focus Variant A rail destination before resize"
                targetAutomationId = "v2-shell-destination-items"; targetControlType = "Button"
                expectedFocusAutomationId = "v2-shell-destination-items"
                expectedAutomationIds = @("v2-shell-navigation-rail")
            },
            [pscustomobject]@{
                action = "resize"; description = "Variant A rail-to-row focus restoration"
                width = 560; height = 820
                expectedFocusAutomationId = "v2-shell-destination-items"
                expectedAutomationIds = @("v2-shell-navigation-row")
                forbiddenAutomationIds = @("v2-shell-navigation-rail")
            }
        )
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
$Shots.Add([pscustomobject]@{
    name = "shell-v2-a-stale-focus"; args = @("--ui-shell", "v2-a"); shellMode = "v2-a"
    width = 0; height = 0
    seedPreview = [pscustomobject]@{
        variant = "v2-a"; address = "#/setup"; focusTarget = "v2-shell-readiness-removed"
    }
    interaction = [pscustomobject]@{
        steps = @([pscustomobject]@{
            action = "assert"; description = "stale persisted focus falls back to the page heading"
            expectedHeading = "Setup & Admin"; expectedFocusAutomationId = "v2-shell-page-heading"
        })
    }
})
$Shots.Add([pscustomobject]@{
    name = "shell-v2-a-tablet-link"; args = @("--ui-shell", "v2-a", "--page", "tablet"); shellMode = "v2-a"
    width = 0; height = 0
    interaction = [pscustomobject]@{
        steps = @([pscustomobject]@{
            action = "assert"; description = "Variant A documented tablet deep link"
            expectedHeading = "Tablet preview"; expectedFocusAutomationId = "v2-shell-page-heading"
            expectedCurrentAutomationIds = @("v2-shell-destination-team")
        })
    }
})
$Shots.Add([pscustomobject]@{
    name = "shell-v2-b-tablet-link"; args = @("--ui-shell", "v2-b", "--page", "tablet"); shellMode = "v2-b"
    width = 0; height = 0
    interaction = [pscustomobject]@{
        steps = @([pscustomobject]@{
            action = "assert"; description = "Variant B documented tablet deep link"
            expectedHeading = "Tablet preview"; expectedFocusAutomationId = "v2-shell-page-heading"
            expectedCurrentAutomationIds = @("v2-shell-destination-team")
        })
    }
})
$Shots.Add([pscustomobject]@{
    # V2 rough package 15: this scenario still exercises the Commands palette button, which now
    # only draws under --developer-mode.
    name = "shell-v2-reset-close"; args = @("--ui-shell", "v2-a", "--page", "setup", "--developer-mode"); shellMode = "v2-a"
    width = 0; height = 0; captureBeforeInteraction = $true; closeImmediately = $true
    interaction = [pscustomobject]@{
        steps = @(
            [pscustomobject]@{
                action = "invoke"; description = "open commands for reset-close race"
                targetAutomationId = "v2-shell-palette"; targetControlType = "Button"
                expectedDialogName = "Commands"
            },
            [pscustomobject]@{
                action = "set-value"; description = "filter commands to preview reset"
                targetAutomationId = "v2-shell-palette-query"; targetControlType = "Edit"; value = "Reset"
                expectedAutomationIds = @("v2-shell-command-reset-preview")
            },
            [pscustomobject]@{
                action = "invoke"; description = "start preview reset immediately before close"
                targetAutomationId = "v2-shell-command-reset-preview"; targetControlType = "Button"
            }
        )
    }
})

# ---------------------------------------------------------------------------------------------
# V2 rough package 30 — the acceptance sweep.
#
# Every address in V2RouteRegistry.Default for Variant A, photographed at the two widths Clayton
# actually runs: an ordinary 1920x1080 window and his 3840x1080 ultrawide. The gallery already
# proved these pages exist; this proves what they look like, and the photographs are published
# as a CI artifact so a later run can be looked at without a Windows box.
#
# The launches reach each route through the persisted preview address rather than a flag, which
# is the same path a deep link takes, and they run before the workflow seeds any game data — so
# these are the data-empty captures. The data-present pair is rendered on Linux with
# tools/V2RenderPreview, which cannot photograph the packaged shell but does have a database.
#
# Bounds, not opinions. Three of them, one per defect this package repaired:
#   * insideWindow  - the V1 pages the shell hosted drew without V1's own page inset, so
#                     "Reload", "Look up value", "Empty the kit" and "Create" were cut off by the
#                     window frame at both widths. All five have since become native V2
#                     workspaces (packages 28 and 25), which carry their own inset and give their
#                     controls automation ids, so the measurement follows the fault onto those
#                     controls instead of naming a V1 button that no longer exists.
#   * forbidden     - the Intel context panel was a fixed 460px column drawn empty until an item
#                     was selected: a quarter of a 1920-wide window, every landing.
#   * dead space    - a ceiling on how much of the body may be one flat colour, so the Intel
#                     stripe cannot quietly come back. Only the routes where the number means
#                     something carry a bound: with no game data yet, a hosted V1 page honestly
#                     has an empty body, and bounding that would fail for being truthful. The
#                     rest are measured and reported, so the next person choosing a bound has
#                     numbers rather than an impression.
$V2AcceptanceWidths = @(
    [pscustomobject]@{ suffix = "1920"; width = 1920; height = 1080 },
    [pscustomobject]@{ suffix = "3840"; width = 3840; height = 1080 }
)

$V2AcceptanceRoutes = @(
    [pscustomobject]@{ key = "raid"; address = "#/raid"; heading = "Raid"
        expected = @("v2-shell-navigation-rail", "v2-map-plan") },
    [pscustomobject]@{ key = "raid-loot"; address = "#/raid/loot"; heading = "Loot decision"
        expected = @("v2-shell-navigation-rail") },
    # The two bounded ones. Both were measured on this branch at under 2% of the body, against
    # roughly a quarter of it before the context column learned to collapse.
    [pscustomobject]@{ key = "intel"; address = "#/intel"; heading = "Intel"
        expected = @("v2-shell-navigation-rail", "v2-intel-results")
        forbidden = @("v2-intel-context"); edge = 0.12 },
    # Package 28 made these three native V2 workspaces, so they no longer host a V1 page and no
    # longer have a V1 button to measure. Their own controls are named and identified, so they
    # are asserted by id and measured with the same rule instead.
    [pscustomobject]@{ key = "intel-ammo"; address = "#/intel/ammo"; heading = "Ammo"
        expected = @("v2-shell-navigation-rail", "v2-ammo-search")
        bounds = @([pscustomobject]@{ automationId = "v2-ammo-reload"; insideWindow = $true }) },
    [pscustomobject]@{ key = "intel-keys"; address = "#/intel/keys"; heading = "Keys"
        expected = @("v2-shell-navigation-rail", "v2-keys-search")
        bounds = @([pscustomobject]@{ automationId = "v2-keys-reload"; insideWindow = $true }) },
    [pscustomobject]@{ key = "intel-flea"; address = "#/intel/flea"; heading = "Flea"
        expected = @("v2-shell-navigation-rail", "v2-flea-search")
        bounds = @([pscustomobject]@{ automationId = "v2-flea-search-go"; insideWindow = $true }) },
    # A real tarkov.dev item id (Graphics card). With no data synced yet this is the honest
    # "nothing known about this item" state, which is exactly what a first run shows — and the
    # state in which the context column used to draw itself empty anyway.
    [pscustomobject]@{ key = "intel-item"; address = "#/intel/item/57347ca924597744596b4e71"; heading = "Item details"
        expected = @("v2-shell-navigation-rail"); forbidden = @("v2-intel-context"); edge = 0.12 },
    [pscustomobject]@{ key = "intel-stash"; address = "#/intel/stash"; heading = "Stash scan"
        expected = @("v2-shell-navigation-rail") },
    [pscustomobject]@{ key = "plan"; address = "#/plan"; heading = "Plan"
        expected = @("v2-shell-navigation-rail") },
    [pscustomobject]@{ key = "plan-hideout"; address = "#/plan/hideout"; heading = "Hideout"
        expected = @("v2-shell-navigation-rail", "v2-hideout-status") },
    [pscustomobject]@{ key = "plan-keep"; address = "#/plan/keep"; heading = "Keep list"
        expected = @("v2-shell-navigation-rail") },
    [pscustomobject]@{ key = "plan-loadout"; address = "#/plan/loadout"; heading = "Loadout"
        expected = @("v2-shell-navigation-rail", "v2-loadout-search")
        bounds = @([pscustomobject]@{ automationId = "v2-loadout-clear"; insideWindow = $true }) },
    [pscustomobject]@{ key = "plan-events"; address = "#/plan/events"; heading = "Events"
        expected = @("v2-shell-navigation-rail", "v2-events-new-name")
        bounds = @([pscustomobject]@{ automationId = "v2-events-create"; insideWindow = $true }) },
    [pscustomobject]@{ key = "team"; address = "#/team"; heading = "Team"
        expected = @("v2-shell-navigation-rail") },
    [pscustomobject]@{ key = "team-group"; address = "#/team/group"; heading = "Group"
        expected = @("v2-shell-navigation-rail") },
    [pscustomobject]@{ key = "tablet"; address = "#/tablet"; heading = "Tablet preview"
        expected = @("v2-shell-navigation-rail") },
    [pscustomobject]@{ key = "debrief"; address = "#/debrief"; heading = "Debrief"
        expected = @("v2-shell-navigation-rail") },
    [pscustomobject]@{ key = "setup"; address = "#/setup"; heading = "Setup & Admin"
        expected = @("v2-shell-navigation-rail") }
)

foreach ($Route in $V2AcceptanceRoutes) {
    foreach ($Size in $V2AcceptanceWidths) {
        $Step = [ordered]@{
            action = "assert"
            description = "Variant A $($Route.address) at $($Size.width)x$($Size.height)"
            expectedAutomationIds = @($Route.expected)
            expectedHeading = $Route.heading
        }
        if ($null -ne (Get-InteractionProperty -Object $Route -Name "bounds")) {
            $Step["expectedBounds"] = @($Route.bounds)
        }
        if ($null -ne (Get-InteractionProperty -Object $Route -Name "forbidden")) {
            $Step["forbiddenAutomationIds"] = @($Route.forbidden)
        }

        $Shot = [ordered]@{
            name = "v2-a-$($Route.key)-$($Size.suffix)"
            args = @("--ui-shell", "v2-a")
            shellMode = "v2-a"
            width = $Size.width
            height = $Size.height
            seedPreview = [pscustomobject]@{ variant = "v2-a"; address = $Route.address }
            # Photograph the route, then assert against it; the assertions here change nothing.
            captureBeforeInteraction = $true
            interaction = [pscustomobject]@{ steps = @([pscustomobject]$Step) }
            measureDeadSpace = $true
        }
        # The ultrawide bound is the 1920 one with headroom: the same page has more window to
        # fill at 3840 and nothing new to fill it with, which is the deferred layout problem the
        # PR's route table describes rather than a regression to catch tonight.
        $Headroom = if ($Size.width -ge 3840) { 0.15 } else { 0.0 }
        $EdgeBound = [double](Get-InteractionProperty -Object $Route -Name "edge" -Default (-1))
        if ($EdgeBound -ge 0) {
            $Shot["maximumEdgeDeadFraction"] = [Math]::Min(1.0, $EdgeBound + $Headroom)
        }
        $BandBound = [double](Get-InteractionProperty -Object $Route -Name "band" -Default (-1))
        if ($BandBound -ge 0) {
            $Shot["maximumFlatBandFraction"] = [Math]::Min(1.0, $BandBound + $Headroom)
        }

        $Shots.Add([pscustomobject]$Shot)
    }
}
$Shots.Add([pscustomobject]@{
    name = "map-renderer-wide"; args = @("--map-renderer-gallery"); shellMode = "v2-map"
    width = 1100; height = 850
    interaction = [pscustomobject]@{
        steps = @(
            [pscustomobject]@{
                action = "assert"; description = "wide map renderer semantics and touch targets"
                expectedAutomationIds = @(
                    "v2-map-renderer", "v2-map-plan", "v2-map-search", "v2-map-page-next",
                    "v2-map-page-status", "v2-map-mode-floorstack2d", "v2-map-background-status",
                    "v2-map-zoom-in", "v2-map-object-cluster-3-2-1176be92",
                    "v2-map-loot-preset", "v2-map-loot-heading", "v2-map-loot-legend", "v2-map-loot-state")
                expectedBounds = @(
                    [pscustomobject]@{ automationId = "v2-map-zoom-in"; minimumWidth = 44; minimumHeight = 44 },
                    [pscustomobject]@{ automationId = "v2-map-loot-preset"; minimumWidth = 44; minimumHeight = 44 },
                    [pscustomobject]@{ automationId = "v2-map-object-cluster-3-2-1176be92"; minimumWidth = 44; minimumHeight = 44 })
                expectedNamePatterns = @(
                    [pscustomobject]@{ automationId = "v2-map-loot-state"; pattern = '^Some spawn knowledge is incomplete or list-only\.' })
            },
            [pscustomobject]@{
                action = "invoke"; description = "open every record in the dense map cluster"
                targetAutomationId = "v2-map-object-cluster-3-2-1176be92"; targetControlType = "Button"
                expectedAutomationIds = @("v2-map-clear-cluster", "v2-map-page-next")
                expectedNamePatterns = @(
                    [pscustomobject]@{ automationId = "v2-map-page-status"; pattern = '^Page 1 of 7 .* 305 matching details$' })
            },
            [pscustomobject]@{
                action = "invoke"; description = "page through the dense map cluster"
                targetAutomationId = "v2-map-page-next"; targetControlType = "Button"
                expectedNamePatterns = @(
                    [pscustomobject]@{ automationId = "v2-map-page-status"; pattern = '^Page 2 of 7 .* 305 matching details$' })
            },
            [pscustomobject]@{
                action = "set-value"; description = "search a record beyond the first map page"
                targetAutomationId = "v2-map-search"; targetControlType = "Edit"; value = "Potential loot 305"
                expectedAutomationIds = @("v2-map-list-loot-spawn-a9b34ab73ebd45eea8742a68aaf2d8e96899a59396562e2859948149e42e542f-0386ea0d")
                expectedNamePatterns = @(
                    [pscustomobject]@{ automationId = "v2-map-page-status"; pattern = '^Page 1 of 1 .* 1 matching details$' })
            },
            [pscustomobject]@{
                action = "invoke"; description = "apply the high-value loot-only preset"
                targetAutomationId = "v2-map-loot-preset"; targetControlType = "Button"
                expectedNamePatterns = @(
                    [pscustomobject]@{ automationId = "v2-map-layer-estimates-d4fc996e"; pattern = '^Show Historical estimates$'; includeOffscreen = $true },
                    [pscustomobject]@{ automationId = "v2-map-layer-hazards-88da2def"; pattern = '^Hide Hazards$'; includeOffscreen = $true })
            },
            [pscustomobject]@{
                action = "toggle"; description = "filter the typed loot layer to exceptional spawns"
                targetAutomationId = "v2-map-loot-filter-tier-exceptional-71739f81"; targetControlType = "Button"
                includeOffscreen = $true
                expectedNamePatterns = @(
                    [pscustomobject]@{ automationId = "v2-map-loot-page-status"; pattern = '^Loot page 1 of 1 .* 2 matching spawns$'; includeOffscreen = $true })
            },
            [pscustomobject]@{
                action = "focus"; description = "bring the list-only loot row into keyboard view"
                targetAutomationId = "v2-map-loot-row-map-only-cache-9594a087"; targetControlType = "Button"
                includeOffscreen = $true
                expectedFocusAutomationId = "v2-map-loot-row-map-only-cache-9594a087"
            },
            [pscustomobject]@{
                action = "invoke"; description = "open typed list-only loot details"
                targetAutomationId = "v2-map-loot-row-map-only-cache-9594a087"; targetControlType = "Button"
                expectedNamePatterns = @(
                    # Text peers may expose either the explicit composite AutomationProperties.Name
                    # or their visible heading as the UIA Name. The stable id proves this is the
                    # selected-detail peer; accept both truthful names instead of requiring the
                    # toolkit-specific punctuation after the heading.
                    [pscustomobject]@{ automationId = "v2-map-loot-selection-live"; pattern = '^Map-only medical cache(?:\.|$)'; includeOffscreen = $true })
            },
            [pscustomobject]@{
                action = "toggle"; description = "filter typed loot rows to the medical category"
                targetAutomationId = "v2-map-loot-filter-category-medical-fca38b50"; targetControlType = "Button"
                includeOffscreen = $true
            },
            [pscustomobject]@{
                action = "toggle"; description = "show the explicit empty profile-utility filter state"
                targetAutomationId = "v2-map-loot-filter-basis-profileutility-3e6145a0"; targetControlType = "Button"
                includeOffscreen = $true
                expectedNamePatterns = @(
                    [pscustomobject]@{ automationId = "v2-map-loot-state"; pattern = '^No potential spawns match these filters\.'; includeOffscreen = $true })
            }
        )
    }
})
$Shots.Add([pscustomobject]@{
    name = "map-renderer-loot-offline"
    args = @("--map-renderer-gallery", "--map-renderer-loot-offline")
    shellMode = "v2-map"; width = 900; height = 700
    interaction = [pscustomobject]@{
        steps = @([pscustomobject]@{
            action = "assert"; description = "offline loot layer remains explicit and independently toggleable"
            expectedAutomationIds = @(
                "v2-map-renderer", "v2-map-plan", "v2-map-loot-preset", "v2-map-loot-heading",
                "v2-map-loot-legend", "v2-map-loot-state")
            expectedNamePatterns = @(
                [pscustomobject]@{ automationId = "v2-map-loot-state"; pattern = '^Loot-spawn data is unavailable\.' })
        }, [pscustomobject]@{
            action = "toggle"; description = "toggle the unavailable loot layer independently"
            targetAutomationId = "v2-map-layer-high-value-loot-spawns-cb6f64d2"; targetControlType = "Button"
            includeOffscreen = $true
            expectedNamePatterns = @(
                [pscustomobject]@{ automationId = "v2-map-layer-high-value-loot-spawns-cb6f64d2"; pattern = '^Show High-value loot$'; includeOffscreen = $true })
        })
    }
})
$Shots.Add([pscustomobject]@{
    name = "map-renderer-320dip-large-text"
    args = @("--map-renderer-gallery", "--map-renderer-large-text")
    shellMode = "v2-map"; width = 640; height = 1000
    interaction = [pscustomobject]@{
        steps = @([pscustomobject]@{
            action = "assert"; description = "320 DIP map renderer at twice interface scale"
            expectedAutomationIds = @("v2-map-renderer", "v2-map-plan", "v2-map-zoom-in", "v2-map-loot-preset")
            expectedOutsideViewportAutomationIds = @("v2-map-search", "v2-map-page-next", "v2-map-page-status")
            expectedBounds = @(
                [pscustomobject]@{ automationId = "v2-map-zoom-in"; minimumWidth = 44; minimumHeight = 44 })
        })
    }
})

foreach ($Shot in $Shots) {
    $Page = $Shot.name
    $Screenshot = Join-Path $ScreenshotDirectory ("{0}.png" -f $Page.ToLowerInvariant())
    $WarningLog = Join-Path $WarningDirectory ("{0}.log" -f $Page.ToLowerInvariant())
    $Interaction = Get-InteractionProperty -Object $Shot -Name "interaction"
    $Result = New-ShotResult -Page $Page -ShellMode $Shot.shellMode -InteractionRequired ($null -ne $Interaction)
    $Process = $null
    try {
        if (Test-Path -LiteralPath $WarningLog) { Remove-Item -LiteralPath $WarningLog -Force }
        # V2 rough package 30 (acceptance sweep): a window this size needs a desktop that size.
        if ($Shot.width -gt 0 -and $Shot.height -gt 0 -and
            -not (Test-DesktopFits -Width $Shot.width -Height $Shot.height)) {
            $Desktop = [System.Windows.Forms.SystemInformation]::VirtualScreen
            # Nothing ran, so nothing is claimed: the gate's six conditions are satisfied to keep
            # the run green, interactionRequired drops to false so the report does not say an
            # assertion passed, and skipped plus detail say what actually happened.
            $Result.skipped = $true
            $Result.presented = $true
            $Result.visuallyVaried = $true
            $Result.warningCaptureArmed = $true
            $Result.gracefulShutdown = $true
            $Result.interactionRequired = $false
            $Result.interactionSmoke = $true
            $Result.interactionDetail = "Skipped: the desktop is only $($Desktop.Width)x$($Desktop.Height)."
            $Result.detail = "Skipped: a $($Shot.width)x$($Shot.height) window needs a desktop at least that large; this one is $($Desktop.Width)x$($Desktop.Height)."
            $Result.deadSpaceDetail = "Not measured (skipped)."
            continue
        }

        $SeedPreview = Get-InteractionProperty -Object $Shot -Name "seedPreview"
        if ($null -ne $SeedPreview) { Set-V2PreviewState -Seed $SeedPreview }
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
        # the visual capture less racy. The declared V2 UIA steps prove only their named route,
        # focus, dialog and title assertions; full usability/accessibility and data/tile readiness
        # remain open #279 criteria that depend on the readiness signal owned by #281.
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

        $CaptureBeforeInteraction = [bool](Get-InteractionProperty -Object $Shot -Name "captureBeforeInteraction" -Default $false)
        if ($CaptureBeforeInteraction) {
            Save-ScreenImage -Path $Screenshot -WindowHandle $Process.MainWindowHandle
        }

        if ($null -ne $Interaction) {
            $Result.interactionDetail = Invoke-ShellInteraction -WindowHandle $Process.MainWindowHandle -Interaction $Interaction
            $Result.interactionSmoke = $true
            if (-not [bool](Get-InteractionProperty -Object $Shot -Name "closeImmediately" -Default $false)) {
                Start-Sleep -Milliseconds 300
            }
        }

        if (-not $CaptureBeforeInteraction) {
            Save-ScreenImage -Path $Screenshot -WindowHandle $Process.MainWindowHandle
        }

        $Result.gracefulShutdown = Close-AppProcess -Process $Process -Page $Page
        if (-not $Result.gracefulShutdown) {
            throw "The packaged app required forced termination after '$Page'."
        }
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

        # V2 rough package 30 (acceptance sweep): a shot that declares a bound is measured
        # against it. Shots without one are measured anyway and only reported, so the next
        # person can see where the dead area actually is before choosing a bound for it.
        $MaximumEdge = [double](Get-InteractionProperty -Object $Shot -Name "maximumEdgeDeadFraction" -Default (-1))
        $MaximumBand = [double](Get-InteractionProperty -Object $Shot -Name "maximumFlatBandFraction" -Default (-1))
        if ($MaximumEdge -ge 0 -or $MaximumBand -ge 0 -or [bool](Get-InteractionProperty -Object $Shot -Name "measureDeadSpace" -Default $false)) {
            $Dead = Measure-DeadSpace -Path $Screenshot
            $Result.edgeDeadFraction = $Dead.edgeDeadFraction
            $Result.flatBandFraction = $Dead.flatBandFraction
            $Breaches = @()
            if ($MaximumEdge -ge 0 -and $Dead.edgeDeadFraction -gt $MaximumEdge) {
                $Breaches += "a flat $([Math]::Round($Dead.edgeDeadFraction * 100, 1))% of the body runs in from the right edge (bound $([Math]::Round($MaximumEdge * 100, 1))%)"
            }
            if ($MaximumBand -ge 0 -and $Dead.flatBandFraction -gt $MaximumBand) {
                $Breaches += "the widest flat band is $([Math]::Round($Dead.flatBandFraction * 100, 1))% of the body (bound $([Math]::Round($MaximumBand * 100, 1))%)"
            }
            $Result.deadSpaceWithinBounds = $Breaches.Count -eq 0
            $Result.deadSpaceDetail = if ($Breaches.Count -eq 0) {
                "Edge $([Math]::Round($Dead.edgeDeadFraction * 100, 1))% · widest flat band $([Math]::Round($Dead.flatBandFraction * 100, 1))%"
            }
            else {
                "Dead area: " + ($Breaches -join "; ") + "."
            }
        }
    }
    catch {
        $Result.detail = $_.Exception.Message
    }
    finally {
        Remove-Item Env:\TARKOV_COMPANION_UI_WARNING_LOG -ErrorAction SilentlyContinue
        if ($null -ne $Process) {
            if (-not $Process.HasExited) {
                $Result.gracefulShutdown = Close-AppProcess -Process $Process -Page $Page
            }
            $Process.Dispose()
        }

        # Every outcome, including a launch that never showed a window: what the toolkit said
        # on the way down is often the explanation. Read after the close, so it is all there.
        # A skipped shot never launched, so there is nothing of its own to read.
        try {
            $Lines = if ($Result.skipped) { $null } else { Read-WarningLogLines -Path $WarningLog }
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
$Ungraceful = @($Results | Where-Object { -not $_.gracefulShutdown })
$InteractionFailed = @($Results | Where-Object { $_.interactionRequired -and -not $_.interactionSmoke })
$DeadSpace = @($Results | Where-Object { -not $_.deadSpaceWithinBounds })
$Failed = @($Results | Where-Object {
    -not $_.presented -or -not $_.visuallyVaried -or $_.interfaceFaultCount -gt 0 -or
        -not $_.warningCaptureArmed -or -not $_.gracefulShutdown -or
        ($_.interactionRequired -and -not $_.interactionSmoke) -or -not $_.deadSpaceWithinBounds
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
    ungracefulShutdownCount = $Ungraceful.Count
    interactionFailureCount = $InteractionFailed.Count
    deadSpaceFailureCount = $DeadSpace.Count
    skippedCount = @($Results | Where-Object { $_.skipped }).Count
    scope = "Responsive visual variation, retained-route, focus, dialog, title and current-destination UI Automation assertions, graceful shutdown, and toolkit interface faults; full usability/accessibility and data/tile readiness are not proven and remain open #279 criteria that depend on the application readiness signal owned by #281."
}

$Directory = Split-Path -Parent ([System.IO.Path]::GetFullPath($OutputPath))
New-Item -ItemType Directory -Path $Directory -Force | Out-Null
$Report | ConvertTo-Json -Depth 6 | Set-Content -Path $OutputPath -Encoding utf8

foreach ($Result in $Results) {
    $Mark = if ($Failed -contains $Result) { "FAIL" } elseif ($Result.skipped) { "skip" } else { "ok  " }
    $Dead = if ($Result.edgeDeadFraction -ge 0) { ", $($Result.deadSpaceDetail)" } else { "" }
    Write-Host "$Mark $($Result.page): $($Result.warningLineCount) trace line(s), $($Result.interfaceFaultCount) interface fault(s)$Dead"
    foreach ($Line in @($Result.interfaceFaults | Select-Object -First 5)) {
        Write-Host "     $Line"
    }
}

# Named separately because they are different repairs. No window is a launch that broke, an
# unvaried one did not fill in, an interface fault is a page asking for something it does not
# get, and an unarmed capture is a gate that was not listening. None of them is proof that the
# every usability/accessibility behavior or that map/data tiles are ready.
$Problems = @()
if ($NoWindow.Count -gt 0) { $Problems += "no window: $(($NoWindow | ForEach-Object { $_.page }) -join ', ')" }
if ($Blank.Count -gt 0) { $Problems += "insufficient visual variation: $(($Blank | ForEach-Object { $_.page }) -join ', ')" }
if ($Faulted.Count -gt 0) { $Problems += "interface faults: $(($Faulted | ForEach-Object { $_.page }) -join ', ')" }
if ($Unarmed.Count -gt 0) { $Problems += "warning capture was not armed: $(($Unarmed | ForEach-Object { $_.page }) -join ', ')" }
if ($Ungraceful.Count -gt 0) { $Problems += "packaged app did not shut down gracefully: $(($Ungraceful | ForEach-Object { $_.page }) -join ', ')" }
if ($InteractionFailed.Count -gt 0) { $Problems += "packaged-shell interaction failed: $(($InteractionFailed | ForEach-Object { $_.page }) -join ', ')" }
if ($DeadSpace.Count -gt 0) { $Problems += "too much of the window is empty: $(($DeadSpace | ForEach-Object { "$($_.page) ($($_.deadSpaceDetail))" }) -join '; ')" }

if ($Problems.Count -gt 0) {
    throw ($Problems -join "; ")
}

Write-Host "All $($Results.Count) launches showed responsive visual variation and shut down gracefully; every required packaged-shell route, focus, dialog, title and current-destination assertion passed with an armed warning capture and no interface faults. Full usability/accessibility and data/tile readiness are not proven here."
