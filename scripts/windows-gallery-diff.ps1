<#
.SYNOPSIS
    Compares this run's V2 A gallery captures with the approved set, pixel by pixel. Advisory.
.DESCRIPTION
    [#279] The gallery proves a window drew something varied and that named controls sit inside
    their bounds. It could not see a page that rendered "successfully" but differently: #827 moved
    the Raid map card 32 px shorter, which the gallery caught only because one fraction floor
    happened to sit right there (0.741 against 0.75). A diff against an approved capture shows that
    change on every shot, where it is, and what it looked like before.

    For each capture it counts the pixels that differ from the approved one by more than
    -Tolerance on any channel AND have no match within one pixel in the other image (both ways),
    so anti-aliasing and a one-pixel text shift do not count, while a line that appeared or went
    does. Regions listed in the <shot>.capture.json the gallery writes beside each PNG (clocks,
    "x s ago", the update banner, the taskbar strip) are masked, in both this run's and the
    approved positions.

    It never fails: the result goes to the job summary, a JSON report, and, for each shot over
    -ThresholdPercent, a three-panel PNG (approved | this run | changed pixels in red on a dimmed
    copy, masks in blue) in -DiffDirectory.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $CurrentDirectory,
    [Parameter(Mandatory = $true)] [string] $BaselineDirectory,
    [Parameter(Mandatory = $true)] [string] $DiffDirectory,
    [string] $ReportPath = (Join-Path $DiffDirectory "gallery-diff.json"),
    [string] $Include = "v2-a-*.png",
    # The Events page prints the local configuration directory; it is not published, so not diffed.
    [string] $Exclude = "v2-a-plan-events-*",
    [int] $Tolerance = 24,
    [double] $ThresholdPercent = 0.5,
    [int] $MaskPadding = 4,
    [string] $BaselineLabel = "the approved set"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public sealed class GalleryDiffResult {
    public int Width, Height, BaselineWidth, BaselineHeight;
    public long Compared, Changed, Masked;
    public int MinX = int.MaxValue, MinY = int.MaxValue, MaxX = -1, MaxY = -1;
    public byte[] State; // 0 same, 1 changed, 2 masked; current image coordinates
}

public static class GalleryPixelDiff {
    static int[] Load(string path, out int width, out int height) {
        using (var source = new Bitmap(path)) {
            width = source.Width; height = source.Height;
            var pixels = new int[width * height];
            var data = source.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try {
                for (int y = 0; y < height; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * width, width);
            } finally { source.UnlockBits(data); }
            return pixels;
        }
    }

    static bool Near(int a, int b, int tolerance) {
        return Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) <= tolerance
            && Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) <= tolerance
            && Math.Abs((a & 255) - (b & 255)) <= tolerance;
    }

    // True when some pixel of 'other' within one pixel of (x, y) matches 'value'.
    static bool HasMatch(int value, int[] other, int w, int h, int x, int y, int tolerance) {
        for (int dy = -1; dy <= 1; dy++) {
            int yy = y + dy; if (yy < 0 || yy >= h) continue;
            for (int dx = -1; dx <= 1; dx++) {
                int xx = x + dx; if (xx < 0 || xx >= w) continue;
                if (Near(value, other[yy * w + xx], tolerance)) return true;
            }
        }
        return false;
    }

    // masks: x, y, width, height quadruples in current image coordinates.
    public static GalleryDiffResult Compare(string baselinePath, string currentPath, int[] masks, int tolerance) {
        int bw, bh, cw, ch;
        var b = Load(baselinePath, out bw, out bh);
        var c = Load(currentPath, out cw, out ch);
        var r = new GalleryDiffResult { Width = cw, Height = ch, BaselineWidth = bw, BaselineHeight = bh, State = new byte[cw * ch] };
        for (int m = 0; m + 3 < masks.Length; m += 4) {
            int x0 = Math.Max(0, masks[m]), y0 = Math.Max(0, masks[m + 1]);
            int x1 = Math.Min(cw, masks[m] + masks[m + 2]), y1 = Math.Min(ch, masks[m + 1] + masks[m + 3]);
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) r.State[y * cw + x] = 2;
        }
        bool same = bw == cw && bh == ch;
        for (int y = 0; y < ch; y++) {
            for (int x = 0; x < cw; x++) {
                int i = y * cw + x;
                if (r.State[i] == 2) { r.Masked++; continue; }
                r.Compared++;
                bool changed;
                if (x >= bw || y >= bh) changed = true;
                else {
                    int cv = c[i], bv = b[y * bw + x];
                    if (Near(cv, bv, tolerance)) changed = false;
                    else if (!same) changed = !HasMatch(cv, b, bw, bh, x, y, tolerance);
                    else changed = !HasMatch(cv, b, bw, bh, x, y, tolerance) || !HasMatch(bv, c, cw, ch, x, y, tolerance);
                }
                if (!changed) continue;
                r.State[i] = 1; r.Changed++;
                if (x < r.MinX) r.MinX = x; if (y < r.MinY) r.MinY = y;
                if (x > r.MaxX) r.MaxX = x; if (y > r.MaxY) r.MaxY = y;
            }
        }
        return r;
    }

    public static void Render(string baselinePath, string currentPath, GalleryDiffResult r, string outputPath) {
        int w = r.Width, h = r.Height;
        int[] c; int cw, ch;
        c = Load(currentPath, out cw, out ch);
        var heat = new int[w * h];
        for (int i = 0; i < heat.Length; i++) {
            int p = c[i];
            int grey = (((p >> 16) & 255) * 3 + ((p >> 8) & 255) * 6 + (p & 255)) / 10 * 35 / 100;
            if (r.State[i] == 1) heat[i] = unchecked((int)0xFFFF2020);
            else if (r.State[i] == 2) heat[i] = unchecked((int)0xFF000000) | (grey << 16) | (grey << 8) | Math.Min(255, grey + 110);
            else heat[i] = unchecked((int)0xFF000000) | (grey << 16) | (grey << 8) | grey;
        }
        using (var heatmap = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        using (var baseline = new Bitmap(baselinePath))
        using (var current = new Bitmap(currentPath)) {
            var data = heatmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try {
                for (int y = 0; y < h; y++) Marshal.Copy(heat, y * w, IntPtr.Add(data.Scan0, y * data.Stride), w);
            } finally { heatmap.UnlockBits(data); }
            int panel = Math.Max(w, baseline.Width), height = Math.Max(h, baseline.Height);
            using (var sheet = new Bitmap(panel * 3 + 16, height, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(sheet))
            using (var font = new Font("Segoe UI", 14f, FontStyle.Bold))
            using (var box = new Pen(Color.Yellow, 2f)) {
                g.Clear(Color.Black);
                g.DrawImageUnscaled(baseline, 0, 0);
                g.DrawImageUnscaled(current, panel + 8, 0);
                g.DrawImageUnscaled(heatmap, panel * 2 + 16, 0);
                if (r.MaxX >= 0) g.DrawRectangle(box, panel * 2 + 16 + r.MinX - 2, r.MinY - 2, r.MaxX - r.MinX + 4, r.MaxY - r.MinY + 4);
                string[] labels = { "approved", "this run", "changed" };
                for (int k = 0; k < 3; k++) {
                    var at = new PointF(k * (panel + 8) + 8, height - 32);
                    g.FillRectangle(Brushes.Black, at.X - 4, at.Y - 2, 120, 28);
                    g.DrawString(labels[k], font, Brushes.Yellow, at);
                }
                g.Flush();
                sheet.Save(outputPath, ImageFormat.Png);
            }
        }
    }
}
"@

function Read-Masks {
    param([string] $Png)
    $Sidecar = [System.IO.Path]::ChangeExtension($Png, ".capture.json")
    if (-not (Test-Path -LiteralPath $Sidecar)) { return @() }
    try {
        $Capture = Get-Content -LiteralPath $Sidecar -Raw | ConvertFrom-Json
        return @($Capture.masks)
    }
    catch { return @() }
}

$Rows = [System.Collections.Generic.List[object]]::new()
$Note = $null
try {
    New-Item -ItemType Directory -Path $DiffDirectory -Force | Out-Null
    $HasBaselines = (Test-Path -LiteralPath $BaselineDirectory) -and
        @(Get-ChildItem -LiteralPath $BaselineDirectory -Filter $Include -File).Count -gt 0
    if (-not $HasBaselines) {
        $Note = "No approved baselines were found, so nothing was compared. Approve a set: run this workflow with approve-baselines set to true."
    }
    else {
        $Current = @(Get-ChildItem -LiteralPath $CurrentDirectory -Filter $Include -File | Where-Object { $_.Name -notlike "$Exclude" } | Sort-Object Name)
        $Approved = @(Get-ChildItem -LiteralPath $BaselineDirectory -Filter $Include -File | Where-Object { $_.Name -notlike "$Exclude" } | Sort-Object Name)
        $CurrentNames = @{}
        foreach ($Shot in $Current) { $CurrentNames[$Shot.Name] = $true }
        foreach ($Shot in $Approved) {
            if (-not $CurrentNames.ContainsKey($Shot.Name)) {
                $Rows.Add([pscustomobject]@{ shot = $Shot.BaseName; changedPercent = $null; changedPixels = 0; region = ""; result = "not captured this run" })
            }
        }
        foreach ($Shot in $Current) {
            $Before = Join-Path $BaselineDirectory $Shot.Name
            if (-not (Test-Path -LiteralPath $Before)) {
                $Rows.Add([pscustomobject]@{ shot = $Shot.BaseName; changedPercent = $null; changedPixels = 0; region = ""; result = "new (no approved capture)" })
                continue
            }
            try {
                # Masked where the volatile text is now and where it was when approved.
                $Rects = [System.Collections.Generic.List[int]]::new()
                foreach ($Mask in @(Read-Masks $Shot.FullName) + @(Read-Masks $Before)) {
                    $Rects.Add([int]$Mask.x - $MaskPadding); $Rects.Add([int]$Mask.y - $MaskPadding)
                    $Rects.Add([int]$Mask.width + 2 * $MaskPadding); $Rects.Add([int]$Mask.height + 2 * $MaskPadding)
                }
                $Diff = [GalleryPixelDiff]::Compare($Before, $Shot.FullName, $Rects.ToArray(), $Tolerance)
                $Percent = if ($Diff.Compared -gt 0) { 100.0 * $Diff.Changed / $Diff.Compared } else { 0.0 }
                $Over = $Percent -gt $ThresholdPercent
                $Result = if ($Over) { "over" } else { "under" }
                if ($Diff.Width -ne $Diff.BaselineWidth -or $Diff.Height -ne $Diff.BaselineHeight) {
                    $Result += " (size $($Diff.BaselineWidth)x$($Diff.BaselineHeight) to $($Diff.Width)x$($Diff.Height))"
                }
                $Region = if ($Diff.MaxX -ge 0) { "x $($Diff.MinX)-$($Diff.MaxX), y $($Diff.MinY)-$($Diff.MaxY)" } else { "" }
                if ($Over) {
                    [GalleryPixelDiff]::Render($Before, $Shot.FullName, $Diff, (Join-Path $DiffDirectory ("{0}.diff.png" -f $Shot.BaseName)))
                }
                $Rows.Add([pscustomobject]@{
                    shot = $Shot.BaseName; changedPercent = [Math]::Round($Percent, 3); changedPixels = $Diff.Changed
                    maskedPixels = $Diff.Masked; region = $Region; result = $Result })
            }
            catch {
                $Rows.Add([pscustomobject]@{ shot = $Shot.BaseName; changedPercent = $null; changedPixels = 0; region = ""; result = "not compared: $($_.Exception.Message)" })
            }
        }
    }
}
catch {
    $Note = "The diff stopped: $($_.Exception.Message)"
}

$Compared = @($Rows | Where-Object { $null -ne $_.changedPercent })
$OverRows = @($Compared | Where-Object { $_.result -like "over*" })
$Report = [ordered]@{
    baseline = $BaselineLabel
    tolerance = $Tolerance
    thresholdPercent = $ThresholdPercent
    comparedCount = $Compared.Count
    overCount = $OverRows.Count
    note = $Note
    shots = $Rows.ToArray()
}
try { $Report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ReportPath -Encoding UTF8 } catch { Write-Host "Could not write ${ReportPath}: $($_.Exception.Message)" }

$Lines = [System.Collections.Generic.List[string]]::new()
$Lines.Add("### Gallery pixel diff (advisory)")
$Lines.Add("")
if ($Note) { $Lines.Add($Note) }
else {
    $Lines.Add("Against $BaselineLabel. $($Compared.Count) shots compared, $($OverRows.Count) over $ThresholdPercent% changed (per-channel tolerance $Tolerance, one-pixel shift allowed, clocks/banner/taskbar masked). Shots over the line have a three-panel PNG in the ``gallery-diffs`` artifact. This never fails the run.")
    $Lines.Add("")
    $Lines.Add("| Shot | Changed % | Changed px | Region | Result |")
    $Lines.Add("| --- | ---: | ---: | --- | --- |")
    $Sorted = @($Rows | Sort-Object -Property @{ Expression = { if ($null -eq $_.changedPercent) { 1e9 } else { [double]$_.changedPercent } }; Descending = $true }, shot)
    foreach ($Row in $Sorted) {
        $Shown = if ($null -eq $Row.changedPercent) { "-" } else { "{0:0.000}" -f $Row.changedPercent }
        $Lines.Add("| $($Row.shot) | $Shown | $($Row.changedPixels) | $($Row.region) | $($Row.result) |")
    }
}
$Text = $Lines -join [Environment]::NewLine
Write-Host $Text
if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
    try { $Text | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8 } catch { Write-Host "Could not write the job summary: $($_.Exception.Message)" }
}
exit 0
