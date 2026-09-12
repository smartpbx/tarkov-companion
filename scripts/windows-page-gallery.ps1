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
    option, and photographed. A page that cannot present a window is a failure
    and is reported as one; the screenshots are evidence for a person to read.

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
        "Raid", "Squad", "Scanner", "Items", "Ammo", "Keys",
        "Flea", "Quests", "Hideout", "Events", "Loadout", "History", "Settings"),

    [int] $WindowTimeoutSeconds = 90,

    [int] $SettleSeconds = 4
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

$ResolvedAppPath = (Resolve-Path -LiteralPath $AppPath).Path
New-Item -ItemType Directory -Path $ScreenshotDirectory -Force | Out-Null
$Results = [System.Collections.Generic.List[object]]::new()

foreach ($Page in $Pages) {
    $Screenshot = Join-Path $ScreenshotDirectory ("{0}.png" -f $Page.ToLowerInvariant())
    $Process = $null
    try {
        $Process = Start-Process -FilePath $ResolvedAppPath -ArgumentList @("--page", $Page) -PassThru
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
                detail = "Exited with code $($Process.ExitCode) before showing a window."
                screenshot = $null
            })
            continue
        }

        if ($Process.MainWindowHandle -eq [IntPtr]::Zero) {
            $Results.Add([pscustomobject]@{
                page = $Page
                presented = $false
                detail = "No window within $WindowTimeoutSeconds second(s)."
                screenshot = $null
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
                detail = "Exited with code $($Process.ExitCode) shortly after showing its window."
                screenshot = $null
            })
            continue
        }

        Save-ScreenImage -Path $Screenshot
        $Results.Add([pscustomobject]@{
            page = $Page
            presented = $true
            detail = "Window shown after $([Math]::Round($Stopwatch.Elapsed.TotalSeconds, 2)) second(s)."
            screenshot = (Split-Path -Leaf $Screenshot)
        })
    }
    catch {
        $Results.Add([pscustomobject]@{
            page = $Page
            presented = $false
            detail = $_.Exception.Message
            screenshot = $null
        })
    }
    finally {
        if ($null -ne $Process) {
            try {
                if (-not $Process.HasExited) {
                    $null = $Process.CloseMainWindow()
                    if (-not $Process.WaitForExit(20000)) { $Process.Kill($true) }
                }
            }
            catch {
                Write-Host "Could not close the process for '$Page': $($_.Exception.Message)"
            }
            $Process.Dispose()
        }
    }
}

$Failed = @($Results | Where-Object { -not $_.presented })
$Report = [pscustomobject]@{
    generatedUtc = [DateTime]::UtcNow.ToString("o")
    appPath = $ResolvedAppPath
    pages = $Results
    failedCount = $Failed.Count
}

$Directory = Split-Path -Parent ([System.IO.Path]::GetFullPath($OutputPath))
New-Item -ItemType Directory -Path $Directory -Force | Out-Null
$Report | ConvertTo-Json -Depth 6 | Set-Content -Path $OutputPath -Encoding utf8

foreach ($Result in $Results) {
    $Mark = if ($Result.presented) { "ok  " } else { "FAIL" }
    Write-Host "$Mark $($Result.page): $($Result.detail)"
}

if ($Failed.Count -gt 0) {
    throw "$($Failed.Count) destination(s) did not present a window: $(($Failed | ForEach-Object { $_.page }) -join ', ')"
}

Write-Host "All $($Results.Count) destinations presented a window."
