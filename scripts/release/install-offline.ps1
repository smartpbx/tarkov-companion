<#
.SYNOPSIS
Verifies a signed offline Tarkov Companion bundle and runs its installer.

.DESCRIPTION
The recovery and headless install path when the private feed is unavailable.

Everything it uses is first copied off the media into a new directory only the current user can
read, and verified and run from there: a network share or a hostile stick can change a file
between the moment it is checked and the moment it is executed, and a private copy cannot.

It requires, by default, the same things the relay updater requires online:
- a pinned cosign build, by content;
- a standardized Sigstore bundle for every file;
- the manifest, the installer, and a signed ring decision, each signed by the reviewed publish
  workflow on main in this repository;
- a decision for -Ring in -FeedRepository that selects exactly this manifest, is not paused, and
  is at or above -MinimumGeneration.

An installer older than the installed build needs the decision's signed rollback, or
-AllowDowngrade. So does an installation whose version cannot be read, because "unknown" is not
"older". -BreakGlass accepts a publisher-signed build without any ring decision. It says so
loudly: it is the operator's authority replacing the ring's, not the ring's policy applied.

Nothing is executed until every check has passed. -WhatIf performs every check and stops.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string] $BundleDirectory,

    [Parameter(Mandatory = $true)]
    [string] $TrustedRoot,

    [string] $CosignPath = "cosign",

    # Names a cosign build other than the pinned ones, by digest. It does not switch the check off.
    [string] $CosignSha256 = "",

    [ValidateSet("", "canary", "beta", "stable")]
    [string] $Ring = "",

    [string] $FeedRepository = "",

    [long] $MinimumGeneration = 0,

    # Where the per-user installation lives; overridable so the checks can be exercised anywhere.
    [string] $InstallRoot = $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA "TarkovCompanionDesktop" } else { "" }),

    [switch] $Headless,

    [switch] $AllowDowngrade,

    [switch] $BreakGlass
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Identity = "https://github.com/smartpbx/tarkov-companion/.github/workflows/publish.yml@refs/heads/main"
$Issuer = "https://token.actions.githubusercontent.com"
$SignerRepository = "smartpbx/tarkov-companion"
$SignerRef = "refs/heads/main"
$BundleMediaType = "application/vnd.dev.sigstore.bundle.v0.3+json"
# SemVer 2.0 exactly as the publisher's release_policy.py accepts it.
$Identifier = '(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
$VersionPattern = "^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-$Identifier(\.$Identifier)*)?$"
# cosign v3.1.3, the first v3 release with GHSA-fx35-mq7g-6g98 fixed, from cosign's own signed
# checksums; the same digests as scripts/release/cosign.sha256.
$CosignPins = @(
    "9fe59be0eca1271873ce019061335eb1ac419b7059202e797828467ddabe33be", # cosign-windows-amd64.exe
    "4629c757b7618056f8ddd7e2625ae9fdd94c0372a65049520bc7d9df9efc7f71", # cosign-linux-amd64
    "c5d324e091826b0d7a78eb16fef316450b4eb9aaec045611c08ba06f5e73220a"  # cosign-linux-arm64
)

function Test-Windows {
    return [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
}

function Get-Property($Object, [string] $Name) {
    if ($Object -is [System.Management.Automation.PSCustomObject] -and @($Object.PSObject.Properties.Name) -ccontains $Name) {
        return $Object.PSObject.Properties[$Name].Value
    }
    return $null
}

function Test-OnlyProperties($Object, [string[]] $Allowed) {
    if ($Object -isnot [System.Management.Automation.PSCustomObject]) { return $false }
    return @($Object.PSObject.Properties.Name | Where-Object { $Allowed -cnotcontains $_ }).Count -eq 0
}

function New-PrivateDirectory([string] $Path) {
    if (Test-Windows) {
        $null = New-Item -ItemType Directory -Path $Path -WhatIf:$false
        $Acl = New-Object System.Security.AccessControl.DirectorySecurity
        $Acl.SetAccessRuleProtection($true, $false)
        $User = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
        $Rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            $User, "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
        $Acl.AddAccessRule($Rule)
        Set-Acl -LiteralPath $Path -AclObject $Acl -WhatIf:$false
    } else {
        $null = [System.IO.Directory]::CreateDirectory($Path, [System.IO.UnixFileMode]"UserRead, UserWrite, UserExecute")
    }
}

function Get-Sha256([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# Copies one named file off the media into the private directory, refusing anything but a plain file.
function Copy-FromMedia([string] $Name, [string] $Label) {
    if ($Name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._+-]*$' -or $Name.Contains("..")) {
        throw "The offline bundle names an unsafe file: $Name."
    }
    $Source = Join-Path $Root $Name
    $Item = Get-Item -LiteralPath $Source -Force -ErrorAction SilentlyContinue
    if ($null -eq $Item -or $Item.PSIsContainer -or ($Item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
        throw "The offline bundle is missing $Label, or it is not a plain file."
    }
    $Target = Join-Path $Staging $Name
    Copy-Item -LiteralPath $Source -Destination $Target -WhatIf:$false
    return $Target
}

# Refuses any bundle but a standardized v0.3 message-signature bundle over exactly these bytes.
function Assert-StandardBundle([string] $Path) {
    $Label = Split-Path -Leaf $Path
    $Raw = Get-Content -LiteralPath "$Path.sigstore.json" -Raw
    $Refusal = "The signature bundle for $Label is not a standardized v0.3 Sigstore bundle."
    if ($null -eq $Raw -or -not $Raw.TrimStart().StartsWith("{")) { throw $Refusal }
    try { $Bundle = $Raw | ConvertFrom-Json } catch { throw $Refusal }
    $Material = Get-Property $Bundle "verificationMaterial"
    $Signature = Get-Property $Bundle "messageSignature"
    $Digest = Get-Property $Signature "messageDigest"
    $Entries = @(Get-Property $Material "tlogEntries")
    $Standard = (Test-OnlyProperties $Bundle @("mediaType", "verificationMaterial", "messageSignature")) -and
        ((Get-Property $Bundle "mediaType") -ceq $BundleMediaType) -and
        (Test-OnlyProperties $Material @("certificate", "tlogEntries", "timestampVerificationData")) -and
        ([string](Get-Property (Get-Property $Material "certificate") "rawBytes")).Length -gt 0 -and
        ($null -ne (Get-Property $Material "tlogEntries")) -and $Entries.Count -eq 1 -and
        (Test-OnlyProperties $Signature @("messageDigest", "signature")) -and
        ([string](Get-Property $Signature "signature")).Length -gt 0 -and
        ((Get-Property $Digest "algorithm") -ceq "SHA2_256") -and
        ([string](Get-Property $Digest "digest") -cmatch '^[A-Za-z0-9+/]{43}=$')
    if (-not $Standard) { throw $Refusal }
    $Hex = Get-Sha256 $Path
    $Bytes = [byte[]]::new(32)
    for ($Index = 0; $Index -lt 32; $Index++) { $Bytes[$Index] = [Convert]::ToByte($Hex.Substring(2 * $Index, 2), 16) }
    if ([Convert]::ToBase64String($Bytes) -cne [string](Get-Property $Digest "digest")) {
        throw "The signature bundle for $Label signs different bytes."
    }
}

function Invoke-Verification([string] $Path) {
    $Label = Split-Path -Leaf $Path
    if (-not (Test-Path -LiteralPath "$Path.sigstore.json" -PathType Leaf)) {
        throw "The offline bundle is missing the signature bundle for $Label."
    }
    Assert-StandardBundle $Path
    # cosign reports success on stderr. Windows PowerShell 5.1 turns redirected native stderr into
    # a terminating error under "Stop", so the exit code is the only verdict read here.
    $Previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $Output = & $StagedCosign verify-blob `
            --bundle "$Path.sigstore.json" `
            --trusted-root $StagedTrustRoot `
            --certificate-identity $Identity `
            --certificate-oidc-issuer $Issuer `
            --certificate-github-workflow-repository $SignerRepository `
            --certificate-github-workflow-ref $SignerRef `
            $Path 2>&1 | Out-String
        $ExitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $Previous
    }
    if ($ExitCode -ne 0) {
        throw "The signature on $Label does not verify for the release publisher: $Output"
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
        $aNumber = $a -cmatch '^[0-9]+$'
        $bNumber = $b -cmatch '^[0-9]+$'
        if ($aNumber -and $bNumber) { return [math]::Sign([decimal]$a - [decimal]$b) }
        if ($aNumber) { return -1 }
        if ($bNumber) { return 1 }
        return [math]::Sign([string]::CompareOrdinal($a, $b))
    }
    return [math]::Sign($LeftParts.Count - $RightParts.Count)
}

if (-not $InstallRoot) {
    throw "The installation directory is unknown, so the installed version cannot be checked; pass -InstallRoot."
}
if (-not $BreakGlass -and (-not $Ring -or -not $FeedRepository)) {
    throw "Pass -Ring and -FeedRepository so the ring decision on the media can be checked, or -BreakGlass to install without one."
}
if ($FeedRepository -and $FeedRepository -cnotmatch '^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$') {
    throw "-FeedRepository must be owner/name."
}

$Root = (Resolve-Path -LiteralPath $BundleDirectory).Path
if (-not (Test-Path -LiteralPath $TrustedRoot -PathType Leaf)) {
    throw "The trust root $TrustedRoot is missing."
}
$Staging = Join-Path ([System.IO.Path]::GetTempPath()) ("tarkov-offline-" + [guid]::NewGuid().ToString("N"))
New-PrivateDirectory $Staging
try {
    # The verifier is copied and hashed too, so the cosign that is checked is the one that runs.
    $CosignSource = (Get-Command -Name $CosignPath -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $StagedCosign = Join-Path $Staging ("cosign" + [System.IO.Path]::GetExtension($CosignSource))
    Copy-Item -LiteralPath $CosignSource -Destination $StagedCosign -WhatIf:$false
    $CosignDigest = Get-Sha256 $StagedCosign
    if ($CosignSha256) {
        if ($CosignSha256 -cnotmatch '^[0-9a-f]{64}$') { throw "-CosignSha256 is not a sha256." }
        if ($CosignDigest -cne $CosignSha256) { throw "$CosignSource is not the cosign -CosignSha256 names." }
    } elseif ($CosignPins -cnotcontains $CosignDigest) {
        throw "$CosignSource (sha256 $CosignDigest) is not a pinned cosign."
    }
    $StagedTrustRoot = Join-Path $Staging "trusted-root.json"
    Copy-Item -LiteralPath $TrustedRoot -Destination $StagedTrustRoot -WhatIf:$false

    $ManifestPath = Copy-FromMedia "release-manifest.json" "release-manifest.json"
    $null = Copy-FromMedia "release-manifest.json.sigstore.json" "the signature bundle for release-manifest.json"
    Invoke-Verification $ManifestPath
    $ManifestDigest = Get-Sha256 $ManifestPath

    $Manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    if ($Manifest.schemaVersion -ne 1 -or [string]$Manifest.version -cnotmatch $VersionPattern -or [string]$Manifest.commit -cnotmatch '^[0-9a-f]{40}$') {
        throw "The signed manifest does not describe a supported release."
    }
    $Installers = @($Manifest.artifacts | Where-Object { $_.component -eq "desktop" -and $_.role -eq "installer" })
    if ($Installers.Count -ne 1) {
        throw "The signed manifest must name exactly one desktop installer."
    }
    $Installer = $Installers[0]
    $InstallerName = [string]$Installer.name
    $InstallerPath = Copy-FromMedia $InstallerName $InstallerName
    $null = Copy-FromMedia "$InstallerName.sigstore.json" "the signature bundle for $InstallerName"
    if ((Get-Sha256 $InstallerPath) -cne [string]$Installer.sha256 -or (Get-Item -LiteralPath $InstallerPath).Length -ne [long]$Installer.size) {
        throw "The installer does not match the signed manifest."
    }
    Invoke-Verification $InstallerPath

    $RollbackAuthorized = $false
    $Selection = "BREAK-GLASS: no ring decision was checked"
    if ($BreakGlass) {
        Write-Warning "BREAK-GLASS: installing a publisher-signed build without a signed ring decision. Ring, pause, rollback and generation policy were NOT applied; this is the operator's authority, not the ring's."
    } else {
        # Member-name ForEach-Object honours -WhatIf and would skip the read; plain property access does not.
        $Decisions = @(Get-ChildItem -LiteralPath $Root -File |
            Where-Object { $_.Name -cmatch '^release-index-g[0-9]{10}\.json$' } |
            Sort-Object -Property Name)
        if ($Decisions.Count -eq 0) {
            throw "The offline bundle holds no signed ring decision; add the ring's release-index-g*.json or pass -BreakGlass."
        }
        $IndexName = @($Decisions[$Decisions.Count - 1].Name)
        $EnvelopePath = Copy-FromMedia $IndexName[0] "the signed ring decision"
        $Envelope = Get-Content -LiteralPath $EnvelopePath -Raw | ConvertFrom-Json
        if ((Get-Property $Envelope "schemaVersion") -ne 1 -or
            (Get-Property $Envelope "mediaType") -cne "application/vnd.tarkov-companion.signed-release-index.v1+json" -or
            (Get-Property $Envelope "payloadBase64") -isnot [string] -or
            (Get-Property $Envelope "sigstoreBundle") -isnot [System.Management.Automation.PSCustomObject]) {
            throw "The ring envelope is malformed."
        }
        $IndexPath = Join-Path $Staging "release-index.json"
        [System.IO.File]::WriteAllBytes($IndexPath, [Convert]::FromBase64String($Envelope.payloadBase64))
        $Envelope.sigstoreBundle | ConvertTo-Json -Depth 32 -Compress | Set-Content -LiteralPath "$IndexPath.sigstore.json" -NoNewline -WhatIf:$false
        Invoke-Verification $IndexPath

        $Index = Get-Content -LiteralPath $IndexPath -Raw | ConvertFrom-Json
        $Generation = [long]$IndexName[0].Substring(15, 10)
        $Release = Get-Property $Index "release"
        $Authorization = Get-Property $Index "authorization"
        if ((Get-Property $Index "schemaVersion") -ne 1 -or
            (Get-Property $Index "mediaType") -cne "application/vnd.tarkov-companion.release-index.v1+json" -or
            (Get-Property $Index "feedRepository") -cne $FeedRepository -or
            (Get-Property $Index "ring") -cne $Ring -or
            [long](Get-Property $Index "generation") -ne $Generation -or
            [long](Get-Property $Authorization "previousGeneration") -ne ($Generation - 1) -or
            (Get-Property $Index "paused") -isnot [bool]) {
            throw "The signed ring decision is not a $Ring decision for $FeedRepository."
        }
        if ((Get-Property $Release "manifestSha256") -cne $ManifestDigest -or
            (Get-Property $Release "version") -cne [string]$Manifest.version -or
            (Get-Property $Release "commit") -cne [string]$Manifest.commit) {
            throw "The signed $Ring decision selects a different build from the manifest on this media."
        }
        if ($Generation -lt $MinimumGeneration) {
            throw "Refusing $Ring generation $Generation below the required generation $MinimumGeneration."
        }
        $RollbackAuthorized = $null -ne (Get-Property $Index "rollback")
        if ($Index.paused -and -not $RollbackAuthorized) {
            throw "$Ring is paused at generation $Generation; consumers hold where they are."
        }
        $Selection = "selected by $Ring generation $Generation"
    }

    # Unknown is not older. An installation whose version cannot be read is treated as one that
    # might be newer, so going over it is a downgrade until somebody says otherwise.
    $Current = Join-Path $InstallRoot "current"
    if (Test-Path -LiteralPath $Current) {
        $InstalledInfo = Join-Path $Current "BUILD_INFO.txt"
        $InstalledVersion = ""
        if (Test-Path -LiteralPath $InstalledInfo -PathType Leaf) {
            $Line = @(Get-Content -LiteralPath $InstalledInfo | Where-Object { $_ -cmatch '^version=' } | Select-Object -First 1)
            if ($Line.Count -eq 1) { $InstalledVersion = $Line[0].Substring(8) }
        }
        if ($InstalledVersion -cnotmatch $VersionPattern) {
            if (-not $AllowDowngrade) {
                throw "The installed version under $Current cannot be read; refusing to install over it without -AllowDowngrade."
            }
        } elseif ((Compare-ReleaseVersion ([string]$Manifest.version) $InstalledVersion) -lt 0 -and -not $RollbackAuthorized -and -not $AllowDowngrade) {
            throw "Refusing to install $($Manifest.version) over the newer installed $InstalledVersion without a signed rollback or -AllowDowngrade."
        }
    }

    Write-Host "Verified signed Tarkov Companion $($Manifest.version) ($($Manifest.commit)) from a private copy of the offline bundle, $Selection."
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
} finally {
    Remove-Item -LiteralPath $Staging -Recurse -Force -ErrorAction SilentlyContinue -WhatIf:$false
}
