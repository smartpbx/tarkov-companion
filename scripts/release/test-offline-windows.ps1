[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = Join-Path $env:RUNNER_TEMP ("tarkov-offline-windows-" + [guid]::NewGuid().ToString("N"))
$Bundle = Join-Path $Root "bundle"
$Install = Join-Path $Root "install"
$InstallerName = "fixture-installer.cmd"
$CompanionName = "fixture-data.json"
$Version = "1.0.608"
$Commit = "cccccccccccccccccccccccccccccccccccccccc"
$Feed = "example/tarkov-feed"
$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$InstallerScript = Join-Path $RepositoryRoot "scripts/release/install-offline.ps1"
$FixtureScript = Join-Path $RepositoryRoot "scripts/release/tests/create_windows_offline_fixture.py"

New-Item -ItemType Directory -Path $Bundle -Force | Out-Null
try {
    @"
@echo off
setlocal
echo %~f0>"%FAKE_INSTALL_ROOT%\ran-from"
if /I "%FAKE_INSTALL_MODE%"=="noop" exit /b 0
if /I "%FAKE_INSTALL_MODE%"=="hang-installer" (
  ping -n 31 127.0.0.1 >nul
  exit /b 99
)
if not exist "%FAKE_INSTALL_ROOT%\current" mkdir "%FAKE_INSTALL_ROOT%\current"
type nul >"%FAKE_INSTALL_ROOT%\current\TarkovCompanion.exe"
if /I "%FAKE_INSTALL_MODE%"=="wrong" (
  >"%FAKE_INSTALL_ROOT%\current\BUILD_INFO.txt" echo version=0.0.1
  >>"%FAKE_INSTALL_ROOT%\current\BUILD_INFO.txt" echo commit=dddddddddddddddddddddddddddddddddddddddd
) else (
  >"%FAKE_INSTALL_ROOT%\current\BUILD_INFO.txt" echo version=%FAKE_INSTALL_VERSION%
  >>"%FAKE_INSTALL_ROOT%\current\BUILD_INFO.txt" echo commit=%FAKE_INSTALL_COMMIT%
)
exit /b 0
"@ | Set-Content -LiteralPath (Join-Path $Bundle $InstallerName) -Encoding ascii
    '{"fixture":"data"}' | Set-Content -LiteralPath (Join-Path $Bundle $CompanionName) -Encoding ascii

    $Cosign = Join-Path $Root "cosign.cmd"
    @"
@echo off
setlocal
if defined FAKE_COSIGN_EXECUTABLE_LOG echo %~f0>>"%FAKE_COSIGN_EXECUTABLE_LOG%"
if /I "%FAKE_COSIGN_MODE%"=="hang" (
  ping -n 31 127.0.0.1 >nul
  exit /b 99
)
if /I "%FAKE_COSIGN_MODE%"=="reject" exit /b 23
exit /b 0
"@ | Set-Content -LiteralPath $Cosign -Encoding ascii
    $Trust = Join-Path $Root "trusted-root.json"
    '{"mediaType":"fixture-trusted-root"}' | Set-Content -LiteralPath $Trust -Encoding utf8
    & python $FixtureScript --bundle $Bundle --installer $InstallerName --companion $CompanionName `
        --version $Version --commit $Commit --feed $Feed
    if ($LASTEXITCODE -ne 0) { throw "Creating the Windows offline fixture failed." }
    $CosignDigest = (Get-FileHash -LiteralPath $Cosign -Algorithm SHA256).Hash.ToLowerInvariant()
    $CosignLog = Join-Path $Root "cosign-executables.log"

    function Invoke-InstallerCase([string] $Mode, [bool] $SeedOldBuild) {
        if (Test-Path -LiteralPath $Install) { Remove-Item -LiteralPath $Install -Recurse -Force }
        New-Item -ItemType Directory -Path $Install -Force | Out-Null
        if ($SeedOldBuild) {
            New-Item -ItemType Directory -Path (Join-Path $Install "current") -Force | Out-Null
            New-Item -ItemType File -Path (Join-Path $Install "current/TarkovCompanion.exe") -Force | Out-Null
            "version=1.0.500`ncommit=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa`n" |
                Set-Content -LiteralPath (Join-Path $Install "current/BUILD_INFO.txt") -Encoding utf8
        }
        $env:FAKE_INSTALL_ROOT = $Install
        $env:FAKE_INSTALL_MODE = $Mode
        $env:FAKE_INSTALL_VERSION = $Version
        $env:FAKE_INSTALL_COMMIT = $Commit
        $env:FAKE_COSIGN_EXECUTABLE_LOG = $CosignLog
        $env:FAKE_COSIGN_MODE = if ($Mode -ceq "reject-signature") { "reject" } elseif ($Mode -ceq "hang-cosign") { "hang" } else { "accept" }
        $Stdout = Join-Path $Root "$Mode.out.txt"
        $Stderr = Join-Path $Root "$Mode.err.txt"
        $Arguments = @(
            "-NoLogo", "-NoProfile", "-NonInteractive", "-File", $InstallerScript,
            "-BundleDirectory", $Bundle, "-TrustedRoot", $Trust, "-CosignPath", $Cosign,
            "-CosignSha256", $CosignDigest, "-InstallRoot", $Install, "-Headless",
            "-Ring", "stable", "-FeedRepository", $Feed)
        if ($Mode -ceq "hang-cosign") { $Arguments += @("-VerifierTimeoutSeconds", "1") }
        if ($Mode -ceq "hang-installer") { $Arguments += @("-InstallerTimeoutSeconds", "1") }
        $Process = Start-Process -FilePath (Get-Command pwsh).Source -ArgumentList $Arguments `
            -Wait -PassThru -RedirectStandardOutput $Stdout -RedirectStandardError $Stderr
        return [ordered]@{
            ExitCode = $Process.ExitCode
            Output = ((Get-Content -LiteralPath $Stdout -Raw -ErrorAction SilentlyContinue) +
                      (Get-Content -LiteralPath $Stderr -Raw -ErrorAction SilentlyContinue))
        }
    }

    $Installed = Invoke-InstallerCase "install" $false
    if ($Installed.ExitCode -ne 0) { throw "Windows offline install failed: $($Installed.Output)" }
    $CosignExecutions = @(Get-Content -LiteralPath $CosignLog)
    if ($CosignExecutions.Count -ne 4 -or
        @($CosignExecutions | Where-Object { $_ -ceq $Cosign -or (Test-Path -LiteralPath $_) }).Count -ne 0) {
        throw "Windows did not execute exactly four cleaned-up private verifier copies: $($CosignExecutions -join ' | ')"
    }
    $Identity = Get-Content -LiteralPath (Join-Path $Install "current/BUILD_INFO.txt") -Raw
    if ($Identity -cnotmatch "version=$Version" -or $Identity -cnotmatch "commit=$Commit") {
        throw "Windows offline install did not leave the requested identity."
    }
    foreach ($Mode in @("noop", "wrong")) {
        $Refused = Invoke-InstallerCase $Mode $true
        if ($Refused.ExitCode -eq 0 -or $Refused.Output -notmatch "installed identity") {
            throw "The Windows offline installer did not refuse the $Mode fixture: $($Refused.Output)"
        }
    }
    $RejectedSignature = Invoke-InstallerCase "reject-signature" $false
    if ($RejectedSignature.ExitCode -eq 0 -or $RejectedSignature.Output -notmatch "does not verify") {
        throw "The Windows offline installer ignored the verifier's native failure: $($RejectedSignature.Output)"
    }
    $TimedOutCosign = Invoke-InstallerCase "hang-cosign" $false
    $TimedOutCosignPath = @(Get-Content -LiteralPath $CosignLog)[-1]
    if ($TimedOutCosign.ExitCode -eq 0 -or $TimedOutCosign.Output -notmatch "did not exit within 1 seconds" -or
        (Test-Path -LiteralPath $TimedOutCosignPath)) {
        throw "Windows did not quiesce a timed-out verifier before cleanup: $($TimedOutCosign.Output)"
    }
    $TimedOutInstaller = Invoke-InstallerCase "hang-installer" $false
    $TimedOutInstallerPath = Get-Content -LiteralPath (Join-Path $Install "ran-from") -Raw
    if ($TimedOutInstaller.ExitCode -eq 0 -or $TimedOutInstaller.Output -notmatch "did not exit within 1 seconds" -or
        (Test-Path -LiteralPath $TimedOutInstallerPath.Trim())) {
        throw "Windows did not quiesce a timed-out installer before cleanup: $($TimedOutInstaller.Output)"
    }
    'tampered' | Set-Content -LiteralPath (Join-Path $Bundle $CompanionName) -Encoding ascii
    $TamperedCompanion = Invoke-InstallerCase "tampered-companion" $false
    if ($TamperedCompanion.ExitCode -eq 0 -or $TamperedCompanion.Output -notmatch "does not match the signed manifest") {
        throw "The Windows offline installer ignored a tampered non-installer artifact: $($TamperedCompanion.Output)"
    }
    '{"fixture":"data"}' | Set-Content -LiteralPath (Join-Path $Bundle $CompanionName) -Encoding ascii
    & python $FixtureScript --bundle $Bundle --installer $InstallerName --companion $CompanionName `
        --version $Version --commit $Commit --feed $Feed --inconsistent-rollback
    if ($LASTEXITCODE -ne 0) { throw "Creating the inconsistent rollback fixture failed." }
    $InvalidTransition = Invoke-InstallerCase "invalid-transition" $false
    if ($InvalidTransition.ExitCode -eq 0 -or $InvalidTransition.Output -notmatch "transition authorization") {
        throw "The Windows offline installer accepted inconsistent rollback authority: $($InvalidTransition.Output)"
    }
    Write-Host "Windows offline install, ACL staging, no-op, wrong-version and transition-authority refusals passed."
}
finally {
    if (Test-Path -LiteralPath $Root) { Remove-Item -LiteralPath $Root -Recurse -Force }
}
