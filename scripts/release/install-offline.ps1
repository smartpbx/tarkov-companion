<#
.SYNOPSIS
Verifies a signed offline Tarkov Companion bundle and runs its installer.

.DESCRIPTION
The recovery and headless install path when the private feed is unavailable. The manifest and
the installer are both verified with cosign against a trust root provisioned separately from the
media, and against the one identity allowed to publish: the reviewed publish workflow on main.
An installer that is older than the installed build is refused unless -AllowDowngrade says the
downgrade is intended, because a stale USB stick is the easiest rollback attack there is.

Nothing is executed until every check has passed. -WhatIf performs every check and stops.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string] $BundleDirectory,

    [Parameter(Mandatory = $true)]
    [string] $TrustedRoot,

    [string] $CosignPath = "cosign",

    # Where the per-user installation lives; overridable so the checks can be exercised anywhere.
    [string] $InstallRoot = $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA "TarkovCompanionDesktop" } else { "" }),

    [switch] $Headless,

    [switch] $AllowDowngrade
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Identity = "https://github.com/smartpbx/tarkov-companion/.github/workflows/publish.yml@refs/heads/main"
$Issuer = "https://token.actions.githubusercontent.com"
$VersionPattern = '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$'

function Assert-File([string] $Path, [string] $Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "The offline bundle is missing $Label."
    }
}

function Invoke-Verification([string] $Path) {
    Assert-File "$Path.sigstore.json" "the signature bundle for $(Split-Path -Leaf $Path)"
    # cosign reports success on stderr. Windows PowerShell 5.1 turns redirected native stderr into
    # a terminating error under "Stop", so the exit code is the only verdict read here.
    $Previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $Output = & $CosignPath verify-blob `
            --bundle "$Path.sigstore.json" `
            --trusted-root $TrustedRoot `
            --certificate-identity $Identity `
            --certificate-oidc-issuer $Issuer `
            $Path 2>&1 | Out-String
        $ExitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $Previous
    }
    if ($ExitCode -ne 0) {
        throw "The signature on $(Split-Path -Leaf $Path) does not verify for the release publisher: $Output"
    }
}

# Orders two release versions; negative when $Left is older. Prerelease labels sort before their
# release and compare numerically where both identifiers are numbers.
function Compare-ReleaseVersion([string] $Left, [string] $Right) {
    $l = [regex]::Match($Left, $VersionPattern)
    $r = [regex]::Match($Right, $VersionPattern)
    if (-not $l.Success -or -not $r.Success) { throw "Cannot compare versions '$Left' and '$Right'." }
    foreach ($Index in 1..3) {
        $Difference = [long]$l.Groups[$Index].Value - [long]$r.Groups[$Index].Value
        if ($Difference -ne 0) { return [math]::Sign($Difference) }
    }
    $LeftPre = $l.Groups[4].Value.TrimStart("-")
    $RightPre = $r.Groups[4].Value.TrimStart("-")
    if ($LeftPre -ceq $RightPre) { return 0 }
    if (-not $LeftPre) { return 1 }
    if (-not $RightPre) { return -1 }
    $LeftParts = $LeftPre.Split(".")
    $RightParts = $RightPre.Split(".")
    for ($Index = 0; $Index -lt [math]::Min($LeftParts.Count, $RightParts.Count); $Index++) {
        $a = $LeftParts[$Index]
        $b = $RightParts[$Index]
        if ($a -ceq $b) { continue }
        $aNumber = $a -match '^[0-9]+$'
        $bNumber = $b -match '^[0-9]+$'
        if ($aNumber -and $bNumber) { return [math]::Sign([long]$a - [long]$b) }
        if ($aNumber) { return -1 }
        if ($bNumber) { return 1 }
        return [math]::Sign([string]::CompareOrdinal($a, $b))
    }
    return [math]::Sign($LeftParts.Count - $RightParts.Count)
}

$Root = (Resolve-Path -LiteralPath $BundleDirectory).Path
Assert-File $TrustedRoot "the trust root"
$ManifestPath = Join-Path $Root "release-manifest.json"
Assert-File $ManifestPath "release-manifest.json"
Invoke-Verification $ManifestPath

$Manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ($Manifest.schemaVersion -ne 1 -or [string]$Manifest.version -notmatch $VersionPattern -or [string]$Manifest.commit -cnotmatch '^[0-9a-f]{40}$') {
    throw "The signed manifest does not describe a supported release."
}
$Installers = @($Manifest.artifacts | Where-Object { $_.component -eq "desktop" -and $_.role -eq "installer" })
if ($Installers.Count -ne 1) {
    throw "The signed manifest must name exactly one desktop installer."
}
$Installer = $Installers[0]
$InstallerName = [string]$Installer.name
if ($InstallerName -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._+-]*$' -or $InstallerName.Contains("..")) {
    throw "The signed manifest names an unsafe installer file."
}
$InstallerPath = Join-Path $Root $InstallerName
Assert-File $InstallerPath $InstallerName
$Actual = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($Actual -cne [string]$Installer.sha256 -or (Get-Item -LiteralPath $InstallerPath).Length -ne [long]$Installer.size) {
    throw "The installer does not match the signed manifest."
}
Invoke-Verification $InstallerPath

$InstalledInfo = if ($InstallRoot) { Join-Path $InstallRoot "current/BUILD_INFO.txt" } else { "" }
if ($InstalledInfo -and (Test-Path -LiteralPath $InstalledInfo -PathType Leaf)) {
    $InstalledVersion = (Get-Content -LiteralPath $InstalledInfo | Where-Object { $_ -match '^version=' } | Select-Object -First 1) -replace '^version=', ''
    if ($InstalledVersion -match $VersionPattern -and (Compare-ReleaseVersion ([string]$Manifest.version) $InstalledVersion) -lt 0 -and -not $AllowDowngrade) {
        throw "Refusing to install $($Manifest.version) over the newer installed $InstalledVersion without -AllowDowngrade."
    }
}

Write-Host "Verified signed Tarkov Companion $($Manifest.version) ($($Manifest.commit)) from the offline bundle."
if (-not $PSCmdlet.ShouldProcess($InstallerPath, "Install signed Tarkov Companion $($Manifest.version)")) {
    return
}

$Arguments = if ($Headless) { @("--silent") } else { @() }
$Process = Start-Process -FilePath $InstallerPath -ArgumentList $Arguments -Wait -PassThru
if ($Process.ExitCode -ne 0) {
    throw "The verified installer exited with code $($Process.ExitCode)."
}
$Installed = Join-Path $InstallRoot "current/TarkovCompanion.exe"
if (-not (Test-Path -LiteralPath $Installed -PathType Leaf)) {
    throw "The installer reported success but the application is not installed."
}
Write-Host "Installed signed Tarkov Companion $($Manifest.version)."
