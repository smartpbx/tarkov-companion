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

    [ValidateRange(0, 9999999999)]
    [long] $MinimumGeneration = 0,

    # Where the per-user installation lives; overridable so the checks can be exercised anywhere.
    [string] $InstallRoot = $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA "TarkovCompanionDesktop" } else { "" }),

    [switch] $Headless,

    [switch] $AllowDowngrade,

    [switch] $BreakGlass,

    [ValidateRange(1, 600)]
    [int] $VerifierTimeoutSeconds = 120,

    [ValidateRange(1, 7200)]
    [int] $InstallerTimeoutSeconds = 1800
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
$VersionPattern = "^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-$Identifier(\.$Identifier)*)?\z"
$MaximumVersionLength = 128
$MaximumVersionNumber = 2147483647
$MaximumJsonBytes = 16MB
$MaximumSignatureBytes = 2MB
$MaximumArtifactBytes = 512MB
$MaximumCopiedBytes = 1GB
$MaximumArtifacts = 4096
$MaximumDecisionFiles = 8192
$MaximumDirectoryEntries = 16384
$MaximumCommandOutputBytes = 64KB
$ProcessCleanupTimeoutMilliseconds = 10000
$CopiedBytes = [long]0
$CopiedFiles = 0
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

function Test-ExactProperties($Object, [string[]] $Expected) {
    if ($Object -isnot [System.Management.Automation.PSCustomObject]) { return $false }
    $Names = @($Object.PSObject.Properties.Name)
    return $Names.Count -eq $Expected.Count -and
        @($Names | Where-Object { $Expected -cnotcontains $_ }).Count -eq 0 -and
        @($Expected | Where-Object { $Names -cnotcontains $_ }).Count -eq 0
}

function Test-BoundedInteger($Value, [long] $Minimum, [long] $Maximum) {
    if ($Value -is [bool] -or $Value -isnot [ValueType]) { return $false }
    try {
        $Number = [decimal]$Value
        return $Number -eq [math]::Truncate($Number) -and $Number -ge $Minimum -and $Number -le $Maximum
    } catch { return $false }
}

function Test-DecimalIdentifier($Value) {
    return $Value -is [string] -and $Value -cmatch '^[1-9][0-9]{0,19}\z'
}

function Test-ReleaseIdentity($Value) {
    if (-not (Test-ExactProperties $Value @("version", "commit", "buildTag", "manifestName", "manifestSha256")) -or
        (Get-Property $Value "version") -isnot [string] -or
        (Get-Property $Value "commit") -isnot [string] -or [string](Get-Property $Value "commit") -cnotmatch '^[0-9a-f]{40}\z' -or
        (Get-Property $Value "manifestSha256") -isnot [string] -or [string](Get-Property $Value "manifestSha256") -cnotmatch '^[0-9a-f]{64}\z' -or
        (Get-Property $Value "manifestName") -cne "release-manifest.json" -or
        (Get-Property $Value "buildTag") -cne ("v2-build-" + [string](Get-Property $Value "version"))) {
        return $false
    }
    try { Assert-ReleaseVersion ([string](Get-Property $Value "version")); return $true } catch { return $false }
}

function Test-SameRelease($Left, $Right) {
    if (-not (Test-ReleaseIdentity $Left) -or -not (Test-ReleaseIdentity $Right)) { return $false }
    foreach ($Name in @("version", "commit", "buildTag", "manifestName", "manifestSha256")) {
        if ((Get-Property $Left $Name) -cne (Get-Property $Right $Name)) { return $false }
    }
    return $true
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

function Read-BoundedText([string] $Path, [long] $MaximumBytes, [string] $Label) {
    $Item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($Item.PSIsContainer -or ($Item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -or
        $Item.Length -le 0 -or $Item.Length -gt $MaximumBytes) {
        throw "$Label is redirected, empty, or outside its byte limit."
    }
    $Bytes = [System.IO.File]::ReadAllBytes($Item.FullName)
    if ($Bytes.LongLength -ne $Item.Length -or $Bytes.LongLength -gt $MaximumBytes) {
        throw "$Label changed or exceeded its byte limit while being read."
    }
    try {
        $Utf8 = New-Object System.Text.UTF8Encoding($false, $true)
        return $Utf8.GetString($Bytes).TrimStart([char]0xfeff)
    } catch { throw "$Label is not valid UTF-8 text." }
}

function Assert-ReleaseVersion([string] $Value) {
    if ([string]::IsNullOrEmpty($Value) -or $Value.Length -gt $MaximumVersionLength) {
        throw "Unsupported release version '$Value'."
    }
    $Match = [regex]::Match($Value, $VersionPattern)
    if (-not $Match.Success) { throw "Unsupported release version '$Value'." }
    foreach ($Index in 1..3) {
        $Part = $Match.Groups[$Index].Value
        if ($Part.Length -gt 10 -or [long]$Part -gt $MaximumVersionNumber) {
            throw "Release version '$Value' has a numeric identifier outside the supported range."
        }
    }
    $Prerelease = $Match.Groups[4].Value.TrimStart("-")
    foreach ($Part in @($Prerelease.Split(".") | Where-Object { $_ -cmatch '^[0-9]+\z' })) {
        if ($Part.Length -gt 10 -or [long]$Part -gt $MaximumVersionNumber) {
            throw "Release version '$Value' has a numeric identifier outside the supported range."
        }
    }
}

function Read-BoundedJson([string] $Path, [long] $MaximumBytes, [string] $Label) {
    $Item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($Item.PSIsContainer -or ($Item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -or
        $Item.Length -le 0 -or $Item.Length -gt $MaximumBytes) {
        throw "$Label is not a plain non-empty file within its $MaximumBytes-byte limit."
    }
    $Bytes = [System.IO.File]::ReadAllBytes($Item.FullName)
    if ($Bytes.LongLength -ne $Item.Length -or $Bytes.LongLength -gt $MaximumBytes) {
        throw "$Label changed or exceeded its byte limit while being read."
    }
    try {
        $Utf8 = New-Object System.Text.UTF8Encoding($false, $true)
        $Text = $Utf8.GetString($Bytes)
    } catch { throw "$Label is not valid UTF-8 JSON." }
    $Depth = 0
    $Quoted = $false
    $Escaped = $false
    foreach ($Character in $Text.ToCharArray()) {
        if ($Quoted) {
            if ($Escaped) { $Escaped = $false }
            elseif ($Character -ceq '\') { $Escaped = $true }
            elseif ($Character -ceq '"') { $Quoted = $false }
            continue
        }
        if ($Character -ceq '"') { $Quoted = $true }
        elseif ($Character -ceq '{' -or $Character -ceq '[') {
            $Depth++
            if ($Depth -gt 32) { throw "$Label exceeds the JSON nesting limit of 32." }
        } elseif ($Character -ceq '}' -or $Character -ceq ']') {
            $Depth--
            if ($Depth -lt 0) { throw "$Label has unbalanced JSON delimiters." }
        }
    }
    if ($Quoted -or $Depth -ne 0) { throw "$Label has unbalanced JSON strings or delimiters." }
    if (-not $Text.TrimStart([char]0xfeff).StartsWith("{", [StringComparison]::Ordinal)) {
        # Every signed document this installer consumes is an object. Reject arrays before
        # ConvertFrom-Json can enumerate a one-element array into an indistinguishable object.
        throw "$Label must contain a JSON object."
    }
    try {
        # PowerShell otherwise turns ISO-8601 JSON strings into DateTime values and destroys the
        # original spelling before the release policy can require its canonical UTC form.
        if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey("DateKind")) {
            return ConvertFrom-Json -InputObject $Text -DateKind String
        }
        return ConvertFrom-Json -InputObject $Text
    } catch { throw "$Label is not valid bounded JSON." }
}

function Copy-BoundedFile([string] $Source, [string] $Target, [long] $MaximumBytes, [string] $Label) {
    $Item = Get-Item -LiteralPath $Source -Force -ErrorAction SilentlyContinue
    if ($null -eq $Item -or $Item.PSIsContainer -or
        ($Item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -or
        $Item.Length -le 0 -or $Item.Length -gt $MaximumBytes) {
        throw "$Label is missing, redirected, empty, or above its $MaximumBytes-byte limit."
    }
    if ($script:CopiedFiles -ge ($MaximumArtifacts * 2 + 6) -or
        $script:CopiedBytes + $Item.Length -gt $MaximumCopiedBytes) {
        throw "The selected offline bundle exceeds its file-count or total-byte limit."
    }
    $Input = [System.IO.File]::Open($Item.FullName, [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $Output = [System.IO.File]::Open($Target, [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try {
            $Buffer = [byte[]]::new(1MB)
            $Written = [long]0
            while (($Read = $Input.Read($Buffer, 0, $Buffer.Length)) -gt 0) {
                $Written += $Read
                if ($Written -gt $MaximumBytes) { throw "$Label exceeded its byte limit while copied." }
                $Output.Write($Buffer, 0, $Read)
            }
        } finally { $Output.Dispose() }
    } finally { $Input.Dispose() }
    if ($Written -ne $Item.Length) {
        Remove-Item -LiteralPath $Target -Force -ErrorAction SilentlyContinue -WhatIf:$false
        throw "$Label changed while copied."
    }
    $script:CopiedFiles++
    $script:CopiedBytes += $Written
    return $Target
}

# Copies one named file off the media into the private directory, refusing anything but a plain file.
function Copy-FromMedia([string] $Name, [string] $Label, [long] $MaximumBytes) {
    if ($Name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._+-]*\z' -or $Name.Contains("..")) {
        throw "The offline bundle names an unsafe file: $Name."
    }
    $Source = Join-Path $Root $Name
    $Target = Join-Path $Staging $Name
    return Copy-BoundedFile $Source $Target $MaximumBytes $Label
}

function ConvertTo-NativeQuotedArgument([string] $Value) {
    if ($Value.Length -gt 0 -and $Value -cnotmatch '[\s"]') { return $Value }

    # Start-Process accepts one native command line, not an argv array. Apply the Windows/.NET
    # quoting rule explicitly so a trusted-root or staging path containing spaces cannot split
    # into a different verifier argument. The same representation is accepted by pwsh on Unix.
    $Builder = [System.Text.StringBuilder]::new()
    $null = $Builder.Append('"')
    $Backslashes = 0
    foreach ($Character in $Value.ToCharArray()) {
        if ($Character -ceq '\') {
            $Backslashes++
            continue
        }
        if ($Character -ceq '"') {
            $null = $Builder.Append(('\' * ($Backslashes * 2 + 1)))
            $null = $Builder.Append('"')
            $Backslashes = 0
            continue
        }
        if ($Backslashes -gt 0) {
            $null = $Builder.Append(('\' * $Backslashes))
            $Backslashes = 0
        }
        $null = $Builder.Append($Character)
    }
    if ($Backslashes -gt 0) { $null = $Builder.Append(('\' * ($Backslashes * 2))) }
    $null = $Builder.Append('"')
    return $Builder.ToString()
}

function Complete-ChildProcess($Process, [bool] $Terminate) {
    $TreeTerminationProven = -not $Terminate
    try {
        $Process.Refresh()
        if ($Terminate -and -not $Process.HasExited) {
            try {
                # This requests termination of the tree, but .NET exposes no descendant handles
                # to wait here. WaitForExit/HasExited below describe only the root process, so a
                # successful request is not proof that every descendant has stopped using staging.
                $Process.Kill($true)
            }
            catch {
                # Windows PowerShell 5.1 runs on a framework that has no Kill(entireProcessTree).
                # taskkill /T is its tree-aware request. Neither API returns waitable handles for
                # every descendant, so either path remains quarantined after forced termination.
                if (Test-Windows) {
                    try {
                        & taskkill.exe /PID "$($Process.Id)" /T /F *> $null
                    }
                    catch { }
                }
                try { $Process.Kill() } catch { }
            }
        }
        if (-not $Process.HasExited -and
            -not $Process.WaitForExit($ProcessCleanupTimeoutMilliseconds)) {
            return $false
        }
        # The parameterless wait is intentional after the timed wait: it drains the async
        # redirection workers before their files can be read or staging can be removed.
        $Process.WaitForExit()
        # A timed-out operation is deliberately not upgraded to "proven" just because the parent
        # raced to a natural exit, or because a tree-kill request returned and its inherited pipes
        # reached EOF. A detached descendant can close those pipes and continue reading the
        # verified installer. The outer finally retains staging instead.
        return $Process.HasExited -and $TreeTerminationProven
    }
    catch {
        return $false
    }
}

function Read-ChildOutput([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return [string]::Empty }
    $Item = Get-Item -LiteralPath $Path -Force
    if ($Item.Length -gt $MaximumCommandOutputBytes) {
        throw "A release child process exceeded its diagnostic-output limit."
    }
    # Opening only after Complete-ChildProcess has drained redirection makes this a proof rather
    # than a best-effort read. A sharing violation here means cleanup must quarantine staging.
    $Stream = [System.IO.File]::Open($Item.FullName, [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $Bytes = [byte[]]::new([int]$Stream.Length)
        $Offset = 0
        while ($Offset -lt $Bytes.Length) {
            $Read = $Stream.Read($Bytes, $Offset, $Bytes.Length - $Offset)
            if ($Read -eq 0) { throw "A release child-process output ended before it was drained." }
            $Offset += $Read
        }
        return [System.Text.Encoding]::UTF8.GetString($Bytes)
    }
    finally {
        $Stream.Dispose()
    }
}

function Invoke-BoundedChildProcess(
    [string] $FilePath,
    [string[]] $ArgumentList,
    [string] $Label,
    [int] $TimeoutSeconds) {
    $Nonce = [guid]::NewGuid().ToString("N")
    $Stdout = Join-Path $Staging "child-$Nonce.stdout"
    $Stderr = Join-Path $Staging "child-$Nonce.stderr"
    $ArgumentLine = (($ArgumentList | ForEach-Object { ConvertTo-NativeQuotedArgument $_ }) -join " ")
    $Process = $null
    $Completed = $false
    $Quiescent = $false
    $ExitCode = -1
    try {
        $Start = @{
            FilePath = $FilePath
            PassThru = $true
            RedirectStandardOutput = $Stdout
            RedirectStandardError = $Stderr
            # -WhatIf describes the installer activation, not the verification that must happen
            # before that decision. Do not let the caller's preference turn this into a null
            # Start-Process result and falsely claim that the signed bytes were checked.
            WhatIf = $false
        }
        if ($ArgumentLine.Length -gt 0) { $Start.ArgumentList = $ArgumentLine }
        $Process = Start-Process @Start
        if (-not $Process.WaitForExit($TimeoutSeconds * 1000)) {
            throw "$Label did not exit within $TimeoutSeconds seconds."
        }
        $Process.WaitForExit()
        $ExitCode = $Process.ExitCode
        $Completed = $true
    }
    finally {
        if ($null -ne $Process) {
            $Quiescent = Complete-ChildProcess $Process (-not $Completed)
            $Process.Dispose()
        }
        if (-not $Quiescent -and $null -ne $Process) {
            $script:StagingCleanupSafe = $false
        }
    }
    if (-not $Quiescent) {
        throw "$Label cleanup could not prove that its process tree exited and drained; staging was quarantined."
    }

    try {
        $Output = Read-ChildOutput $Stdout
        $ErrorOutput = Read-ChildOutput $Stderr
    }
    catch [System.IO.IOException] {
        $script:StagingCleanupSafe = $false
        throw "$Label output was not quiescent; staging was quarantined."
    }
    finally {
        if ($script:StagingCleanupSafe) {
            Remove-Item -LiteralPath $Stdout, $Stderr -Force -ErrorAction SilentlyContinue -WhatIf:$false
        }
    }
    return [ordered]@{ ExitCode = $ExitCode; Output = $Output; Error = $ErrorOutput }
}

# Refuses any bundle but a standardized v0.3 message-signature bundle over exactly these bytes.
function Assert-StandardBundle([string] $Path) {
    $Label = Split-Path -Leaf $Path
    $Refusal = "The signature bundle for $Label is not a standardized v0.3 Sigstore bundle."
    try { $Bundle = Read-BoundedJson "$Path.sigstore.json" $MaximumSignatureBytes "Signature bundle for $Label" } catch { throw $Refusal }
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
        ([string](Get-Property $Digest "digest") -cmatch '^[A-Za-z0-9+/]{43}=\z')
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
    $Result = Invoke-BoundedChildProcess $StagedCosign @(
        "verify-blob",
        "--bundle", "$Path.sigstore.json",
        "--trusted-root", $StagedTrustRoot,
        "--certificate-identity", $Identity,
        "--certificate-oidc-issuer", $Issuer,
        "--certificate-github-workflow-repository", $SignerRepository,
        "--certificate-github-workflow-ref", $SignerRef,
        $Path
    ) "cosign verifier" $VerifierTimeoutSeconds
    if ($Result.ExitCode -ne 0) {
        throw "The signature on $Label does not verify for the release publisher."
    }
}

# Orders two release versions; negative when $Left is older. Prerelease labels sort before their
# release and compare numerically where both identifiers are numbers.
function Compare-ReleaseVersion([string] $Left, [string] $Right) {
    Assert-ReleaseVersion $Left
    Assert-ReleaseVersion $Right
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
        $aNumber = $a -cmatch '^[0-9]+\z'
        $bNumber = $b -cmatch '^[0-9]+\z'
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
if ($FeedRepository -and $FeedRepository -cnotmatch '^[A-Za-z0-9-]+/[A-Za-z0-9._-]+\z') {
    throw "-FeedRepository must be owner/name."
}
if ($FeedRepository -and $FeedRepository -ieq $SignerRepository) {
    throw "The public source repository cannot be used as the authenticated release feed."
}

$Root = (Resolve-Path -LiteralPath $BundleDirectory).Path
$RootItem = Get-Item -LiteralPath $Root -Force
if (-not $RootItem.PSIsContainer -or ($RootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
    throw "The offline bundle root must be a plain directory."
}
if (-not (Test-Path -LiteralPath $TrustedRoot -PathType Leaf) -or
    ((Get-Item -LiteralPath $TrustedRoot -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
    throw "The trust root $TrustedRoot is missing."
}
$Staging = Join-Path ([System.IO.Path]::GetTempPath()) ("tarkov-offline-" + [guid]::NewGuid().ToString("N"))
New-PrivateDirectory $Staging
$StagingCleanupSafe = $true
try {
    # The verifier is copied and hashed too, so the cosign that is checked is the one that runs.
    $CosignSource = (Get-Command -Name $CosignPath -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $StagedCosign = Join-Path $Staging ("cosign" + [System.IO.Path]::GetExtension($CosignSource))
    $null = Copy-BoundedFile $CosignSource $StagedCosign $MaximumArtifactBytes "cosign verifier"
    $CosignDigest = Get-Sha256 $StagedCosign
    if ($CosignSha256) {
        if ($CosignSha256 -cnotmatch '^[0-9a-f]{64}\z') { throw "-CosignSha256 is not a sha256." }
        if ($CosignDigest -cne $CosignSha256) { throw "$CosignSource is not the cosign -CosignSha256 names." }
    } elseif ($CosignPins -cnotcontains $CosignDigest) {
        throw "$CosignSource (sha256 $CosignDigest) is not a pinned cosign."
    }
    # Copy-BoundedFile deliberately creates a new plain file. Windows executes it by extension;
    # Unix also requires an execute bit, and without one PowerShell delegates to xdg-open instead
    # of starting the verifier. The private copy remains user-only and is still the file hashed.
    if ($PSVersionTable.PSEdition -eq "Core" -and -not $IsWindows) {
        $UnixUserOnly = [System.IO.UnixFileMode]::UserRead -bor [System.IO.UnixFileMode]::UserWrite -bor [System.IO.UnixFileMode]::UserExecute
        [System.IO.File]::SetUnixFileMode($StagedCosign, $UnixUserOnly)
    }
    $StagedTrustRoot = Join-Path $Staging "trusted-root.json"
    $null = Copy-BoundedFile $TrustedRoot $StagedTrustRoot $MaximumJsonBytes "Sigstore trust root"
    $null = Read-BoundedJson $StagedTrustRoot $MaximumJsonBytes "Sigstore trust root"

    $ManifestPath = Copy-FromMedia "release-manifest.json" "release-manifest.json" $MaximumJsonBytes
    $null = Copy-FromMedia "release-manifest.json.sigstore.json" "the signature bundle for release-manifest.json" $MaximumSignatureBytes
    Invoke-Verification $ManifestPath
    $ManifestDigest = Get-Sha256 $ManifestPath

    $Manifest = Read-BoundedJson $ManifestPath $MaximumJsonBytes "Release manifest"
    if ($Manifest.schemaVersion -ne 1 -or [string]$Manifest.version -cnotmatch $VersionPattern -or [string]$Manifest.commit -cnotmatch '^[0-9a-f]{40}\z') {
        throw "The signed manifest does not describe a supported release."
    }
    Assert-ReleaseVersion ([string]$Manifest.version)
    $Artifacts = @($Manifest.artifacts)
    if ($Artifacts.Count -le 0 -or $Artifacts.Count -gt $MaximumArtifacts) {
        throw "The signed manifest artifact count is outside the supported range."
    }
    $ArtifactNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $VerifiedArtifacts = [System.Collections.Generic.Dictionary[string,string]]::new([System.StringComparer]::Ordinal)
    foreach ($Artifact in $Artifacts) {
        if (-not (Test-ExactProperties $Artifact @("name", "component", "role", "sha256", "size")) -or
            (Get-Property $Artifact "name") -isnot [string] -or
            (Get-Property $Artifact "component") -isnot [string] -or
            [string](Get-Property $Artifact "component") -cnotmatch '^[a-z][a-z0-9-]{0,63}\z' -or
            (Get-Property $Artifact "role") -isnot [string] -or
            [string](Get-Property $Artifact "role") -cnotmatch '^[a-z][a-z0-9-]{0,63}\z' -or
            (Get-Property $Artifact "sha256") -isnot [string] -or
            [string](Get-Property $Artifact "sha256") -cnotmatch '^[0-9a-f]{64}\z' -or
            -not (Test-BoundedInteger (Get-Property $Artifact "size") 1 $MaximumArtifactBytes)) {
            throw "The signed manifest artifact table is malformed."
        }

        $ArtifactName = [string](Get-Property $Artifact "name")
        if ($ArtifactName.Length -gt 200 -or
            $ArtifactName -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._+-]*\z' -or
            $ArtifactName.Contains("..") -or
            -not $ArtifactNames.Add($ArtifactName)) {
            throw "The signed manifest names an unsafe or repeated artifact: $ArtifactName."
        }
    }

    # The signed manifest is one transaction, not merely a way to locate an installer. Copy,
    # digest-check and signature-check every row before ring or break-glass authority can run any
    # byte. A tampered relay/data/model/SBOM therefore blocks Windows installation too.
    foreach ($Artifact in $Artifacts) {
        $ArtifactName = [string](Get-Property $Artifact "name")
        $ArtifactSize = [long](Get-Property $Artifact "size")
        $ArtifactPath = Copy-FromMedia $ArtifactName $ArtifactName $MaximumArtifactBytes
        $null = Copy-FromMedia "$ArtifactName.sigstore.json" "the signature bundle for $ArtifactName" $MaximumSignatureBytes
        if ((Get-Sha256 $ArtifactPath) -cne [string](Get-Property $Artifact "sha256") -or
            (Get-Item -LiteralPath $ArtifactPath).Length -ne $ArtifactSize) {
            throw "$ArtifactName does not match the signed manifest."
        }
        Invoke-Verification $ArtifactPath
        $VerifiedArtifacts.Add($ArtifactName, $ArtifactPath)
    }

    $Installers = @($Artifacts | Where-Object {
        (Get-Property $_ "component") -ceq "desktop" -and (Get-Property $_ "role") -ceq "installer"
    })
    if ($Installers.Count -ne 1) {
        throw "The signed manifest must name exactly one desktop installer."
    }
    $Installer = $Installers[0]
    $InstallerName = [string](Get-Property $Installer "name")
    $InstallerPath = [string]$VerifiedArtifacts[$InstallerName]
    # The verified installer normally runs on Windows, where its extension selects the loader.
    # The Unix recovery fixture is a script with the same signed role, and the private copy must
    # be executable there for the test to exercise the post-verification install boundary.
    if ($PSVersionTable.PSEdition -eq "Core" -and -not $IsWindows) {
        [System.IO.File]::SetUnixFileMode($InstallerPath, $UnixUserOnly)
    }
    $RollbackAuthorized = $false
    $Selection = "BREAK-GLASS: no ring decision was checked"
    if ($BreakGlass) {
        Write-Warning "BREAK-GLASS: installing a publisher-signed build without a signed ring decision. Ring, pause, rollback and generation policy were NOT applied; this is the operator's authority, not the ring's."
    } else {
        # Enumerate lazily and stop at a fixed ceiling; hostile removable media must not make
        # PowerShell allocate an unbounded directory listing before any signature is checked.
        $Decisions = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
        $DirectoryEntries = 0
        foreach ($Candidate in [System.IO.Directory]::EnumerateFileSystemEntries($Root)) {
            $DirectoryEntries++
            if ($DirectoryEntries -gt $MaximumDirectoryEntries) {
                throw "The offline bundle exceeds its directory-entry limit."
            }
            $Item = Get-Item -LiteralPath $Candidate -Force
            if (-not $Item.PSIsContainer -and $Item.Name -cmatch '^release-index-g[0-9]{10}\.json\z' -and
                -not ($Item.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
                if ($Decisions.Count -ge $MaximumDecisionFiles) {
                    throw "The offline bundle contains too many ring decision files."
                }
                $Decisions.Add($Item)
            }
        }
        $Decisions.Sort([System.Comparison[System.IO.FileInfo]]{
            param($Left, $Right)
            [string]::CompareOrdinal($Left.Name, $Right.Name)
        })
        if ($Decisions.Count -eq 0) {
            throw "The offline bundle holds no signed ring decision; add the ring's release-index-g*.json or pass -BreakGlass."
        }
        $IndexName = @($Decisions[$Decisions.Count - 1].Name)
        $EnvelopePath = Copy-FromMedia $IndexName[0] "the signed ring decision" $MaximumJsonBytes
        $Envelope = Read-BoundedJson $EnvelopePath $MaximumJsonBytes "Signed ring envelope"
        if (-not (Test-ExactProperties $Envelope @("schemaVersion", "mediaType", "payloadBase64", "sigstoreBundle")) -or
            (Get-Property $Envelope "schemaVersion") -ne 1 -or
            (Get-Property $Envelope "mediaType") -cne "application/vnd.tarkov-companion.signed-release-index.v1+json" -or
            (Get-Property $Envelope "payloadBase64") -isnot [string] -or
            (Get-Property $Envelope "sigstoreBundle") -isnot [System.Management.Automation.PSCustomObject]) {
            throw "The ring envelope is malformed."
        }
        $IndexPath = Join-Path $Staging "release-index.json"
        try { $IndexBytes = [Convert]::FromBase64String($Envelope.payloadBase64) } catch {
            throw "The ring envelope payload is not valid base64."
        }
        if ($IndexBytes.LongLength -le 0 -or $IndexBytes.LongLength -gt $MaximumJsonBytes) {
            throw "The signed ring payload is outside its JSON byte limit."
        }
        [System.IO.File]::WriteAllBytes($IndexPath, $IndexBytes)
        $BundleText = $Envelope.sigstoreBundle | ConvertTo-Json -Depth 32 -Compress
        $BundleBytes = [System.Text.Encoding]::UTF8.GetBytes($BundleText)
        if ($BundleBytes.LongLength -le 0 -or $BundleBytes.LongLength -gt $MaximumSignatureBytes) {
            throw "The ring envelope's signature bundle is outside its byte limit."
        }
        [System.IO.File]::WriteAllBytes("$IndexPath.sigstore.json", $BundleBytes)
        Invoke-Verification $IndexPath

        $Index = Read-BoundedJson $IndexPath $MaximumJsonBytes "Signed ring payload"
        $Generation = [long]$IndexName[0].Substring(15, 10)
        $Release = Get-Property $Index "release"
        $Authorization = Get-Property $Index "authorization"
        $IndexGeneration = Get-Property $Index "generation"
        $PreviousGeneration = Get-Property $Authorization "previousGeneration"
        if (-not (Test-BoundedInteger $IndexGeneration 1 9999999999) -or
            -not (Test-BoundedInteger $PreviousGeneration 0 9999999998)) {
            throw "The signed ring decision has an unsupported generation."
        }
        $Previous = Get-Property $Index "previous"
        $LastKnownGood = Get-Property $Index "lastKnownGood"
        $Rollback = Get-Property $Index "rollback"
        $Action = Get-Property $Authorization "action"
        $SourceRing = Get-Property $Authorization "sourceRing"
        $SourceGeneration = Get-Property $Authorization "sourceGeneration"
        $VerificationRunId = Get-Property $Authorization "verificationRunId"
        $IndexFields = @("schemaVersion", "mediaType", "feedRepository", "ring", "generation", "updatedUtc",
            "paused", "release", "previous", "lastKnownGood", "highWaterVersion", "rollback", "authorization")
        $AuthorizationFields = @("action", "actor", "reason", "workflowRunId", "verificationRunId", "sourceRing",
            "sourceGeneration", "previousGeneration")
        if (-not (Test-ExactProperties $Index $IndexFields) -or
            -not (Test-ExactProperties $Authorization $AuthorizationFields) -or
            (Get-Property $Index "schemaVersion") -ne 1 -or
            (Get-Property $Index "mediaType") -cne "application/vnd.tarkov-companion.release-index.v1+json" -or
            (Get-Property $Index "feedRepository") -cne $FeedRepository -or
            (Get-Property $Index "ring") -cne $Ring -or
            [long]$IndexGeneration -ne $Generation -or
            [long]$PreviousGeneration -ne ($Generation - 1) -or
            (Get-Property $Index "paused") -isnot [bool] -or
            -not (Test-ReleaseIdentity $Release) -or
            ($null -ne $Previous -and -not (Test-ReleaseIdentity $Previous)) -or
            ($null -ne $LastKnownGood -and -not (Test-ReleaseIdentity $LastKnownGood)) -or
            (Get-Property $Index "highWaterVersion") -isnot [string]) {
            throw "The signed ring decision is not a $Ring decision for $FeedRepository."
        }
        try {
            Assert-ReleaseVersion ([string](Get-Property $Index "highWaterVersion"))
            if ((Compare-ReleaseVersion ([string](Get-Property $Index "highWaterVersion")) ([string](Get-Property $Release "version"))) -lt 0) {
                throw "high water below release"
            }
        } catch { throw "The signed ring decision has an invalid high-water version." }
        if ($null -ne $Rollback) {
            $RollbackFrom = Get-Property $Rollback "from"
            if (-not (Test-ExactProperties $Rollback @("generation", "from")) -or
                -not (Test-BoundedInteger (Get-Property $Rollback "generation") 1 $Generation) -or
                -not (Test-ReleaseIdentity $RollbackFrom) -or
                -not (Test-SameRelease $RollbackFrom $Previous) -or
                -not (Test-SameRelease $Release $LastKnownGood) -or
                (Test-SameRelease $RollbackFrom $Release)) {
                throw "The signed ring decision has an inconsistent rollback authorization."
            }
        }
        $Actor = Get-Property $Authorization "actor"
        $Reason = Get-Property $Authorization "reason"
        $ExpectedSourceRing = if ($Ring -ceq "beta") { "canary" } elseif ($Ring -ceq "stable") { "beta" } else { $null }
        $KnownActions = @("publish", "promote", "pause", "resume", "mark-lkg", "rollback")
        if ($KnownActions -cnotcontains $Action -or
            $Actor -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$Actor) -or
            [string]$Actor -cne ([string]$Actor).Trim() -or ([string]$Actor).Length -gt 256 -or
            $Reason -isnot [string] -or ([string]$Reason).Length -gt 2048 -or
            -not (Test-DecimalIdentifier (Get-Property $Authorization "workflowRunId")) -or
            ($null -ne $VerificationRunId -and -not (Test-DecimalIdentifier $VerificationRunId)) -or
            (($null -eq $SourceRing) -ne ($null -eq $SourceGeneration)) -or
            ($null -ne $SourceRing -and $SourceRing -cnotin @("canary", "beta")) -or
            ($null -ne $SourceGeneration -and -not (Test-BoundedInteger $SourceGeneration 1 9999999999)) -or
            (($Action -ceq "promote") -ne ($null -ne $SourceRing)) -or
            ($Action -ceq "promote" -and $SourceRing -cne $ExpectedSourceRing) -or
            ($Action -ceq "publish" -and ($Ring -cne "canary" -or $null -eq $VerificationRunId)) -or
            ($Action -cne "publish" -and $null -ne $VerificationRunId) -or
            ($Action -cin @("publish", "promote") -and [bool](Get-Property $Index "paused")) -or
            ($Action -ceq "pause" -and -not [bool](Get-Property $Index "paused")) -or
            ($Action -ceq "resume" -and [bool](Get-Property $Index "paused")) -or
            ($Action -ceq "rollback" -and $null -eq $Rollback) -or
            ($null -ne $Rollback -and $Action -cnotin @("rollback", "pause", "resume"))) {
            throw "The signed ring decision has inconsistent transition authorization."
        }
        if ((Get-Property $Index "updatedUtc") -isnot [string]) {
            throw "The signed ring decision has an invalid or implausibly future timestamp."
        }
        $TimestampStyle = [Globalization.DateTimeStyles]::AssumeUniversal -bor
            [Globalization.DateTimeStyles]::AdjustToUniversal
        try {
            [datetimeoffset]$Timestamp = [datetimeoffset]::ParseExact(
                [string](Get-Property $Index "updatedUtc"), "yyyy-MM-dd'T'HH:mm:ss'Z'",
                [Globalization.CultureInfo]::InvariantCulture, $TimestampStyle)
        } catch {
            throw "The signed ring decision has an invalid or implausibly future timestamp."
        }
        if ($Timestamp -gt [datetimeoffset]::UtcNow.AddMinutes(5)) {
            throw "The signed ring decision has an invalid or implausibly future timestamp."
        }
        if ((Get-Property $Release "manifestSha256") -cne $ManifestDigest -or
            (Get-Property $Release "version") -cne [string]$Manifest.version -or
            (Get-Property $Release "commit") -cne [string]$Manifest.commit) {
            throw "The signed $Ring decision selects a different build from the manifest on this media."
        }
        if ($Generation -lt $MinimumGeneration) {
            throw "Refusing $Ring generation $Generation below the required generation $MinimumGeneration."
        }
        $RollbackAuthorized = $null -ne $Rollback
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
            $InfoItem = Get-Item -LiteralPath $InstalledInfo -Force
            if (($InfoItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -or $InfoItem.Length -gt 65536) {
                throw "The installed BUILD_INFO.txt is redirected or outside its byte limit."
            }
            $Line = @((Read-BoundedText $InstalledInfo 65536 "Installed BUILD_INFO.txt").Split("`n") |
                ForEach-Object { $_.TrimEnd("`r") } | Where-Object { $_ -cmatch '^version=' })
            if ($Line.Count -eq 1) { $InstalledVersion = $Line[0].Substring(8) }
        }
        if ($InstalledVersion -cnotmatch $VersionPattern) {
            if (-not $AllowDowngrade) {
                throw "The installed version under $Current cannot be read; refusing to install over it without -AllowDowngrade."
            }
            # Explicit downgrade authority permits the install, but an unreadable value still
            # cannot be handed to the ordering function as though it were a version.
            $InstalledVersion = ""
        } else {
            try { Assert-ReleaseVersion $InstalledVersion } catch {
                if (-not $AllowDowngrade) {
                    throw "The installed version under $Current cannot be read; refusing to install over it without -AllowDowngrade."
                }
                $InstalledVersion = ""
            }
        }
        if ($InstalledVersion -and (Compare-ReleaseVersion ([string]$Manifest.version) $InstalledVersion) -lt 0 -and -not $RollbackAuthorized -and -not $AllowDowngrade) {
            throw "Refusing to install $($Manifest.version) over the newer installed $InstalledVersion without a signed rollback or -AllowDowngrade."
        }
    }

    Write-Host "Verified signed Tarkov Companion $($Manifest.version) ($($Manifest.commit)) from a private copy of the offline bundle, $Selection."
    if (-not $PSCmdlet.ShouldProcess($InstallerPath, "Install signed Tarkov Companion $($Manifest.version)")) {
        return
    }

    $Arguments = if ($Headless) { @("--silent") } else { @() }
    $InstallResult = Invoke-BoundedChildProcess $InstallerPath $Arguments "verified installer" $InstallerTimeoutSeconds
    if ($InstallResult.ExitCode -ne 0) {
        throw "The verified installer exited with code $($InstallResult.ExitCode)."
    }
    $Installed = Join-Path $InstallRoot "current/TarkovCompanion.exe"
    if (-not (Test-Path -LiteralPath $Installed -PathType Leaf) -or
        ((Get-Item -LiteralPath $Installed -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
        throw "The installer reported success but the application is not installed."
    }
    $InstalledBuildInfo = Join-Path $InstallRoot "current/BUILD_INFO.txt"
    if (-not (Test-Path -LiteralPath $InstalledBuildInfo -PathType Leaf)) {
        throw "The installer reported success but installed no BUILD_INFO.txt identity."
    }
    $InstalledInfoItem = Get-Item -LiteralPath $InstalledBuildInfo -Force
    if (($InstalledInfoItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -or $InstalledInfoItem.Length -gt 65536) {
        throw "The installer reported success but installed a redirected or oversized BUILD_INFO.txt identity."
    }
    $InstalledIdentity = @{}
    foreach ($Line in (Read-BoundedText $InstalledBuildInfo 65536 "Installed BUILD_INFO.txt").Split("`n")) {
        $Line = $Line.TrimEnd("`r")
        if (-not $Line) { continue }
        if ($Line -cnotmatch '^(?<key>version|commit|built_utc)=(?<value>.+)$') {
            throw "The installed BUILD_INFO.txt contains an unknown or malformed line."
        }
        if ($InstalledIdentity.ContainsKey($Matches.key)) {
            throw "The installed BUILD_INFO.txt repeats $($Matches.key)."
        }
        $InstalledIdentity[$Matches.key] = $Matches.value
    }
    if ($InstalledIdentity.version -cne [string]$Manifest.version -or
        $InstalledIdentity.commit -cne [string]$Manifest.commit) {
        throw "The installer exited successfully but installed identity $($InstalledIdentity.version)+$($InstalledIdentity.commit), not signed release $($Manifest.version)+$($Manifest.commit)."
    }
    Write-Host "Installed signed Tarkov Companion $($Manifest.version)."
} finally {
    if ($StagingCleanupSafe) {
        Remove-Item -LiteralPath $Staging -Recurse -Force -ErrorAction SilentlyContinue -WhatIf:$false
    }
    else {
        Write-Warning "Release staging could not be proven quiescent and was quarantined at $Staging. Remove it only after the child process is confirmed absent or the machine restarts."
    }
}
