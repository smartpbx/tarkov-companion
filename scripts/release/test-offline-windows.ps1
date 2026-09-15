[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Root = Join-Path $env:RUNNER_TEMP ("tarkov-offline-windows-" + [guid]::NewGuid().ToString("N"))
$Bundle = Join-Path $Root "bundle"
$Install = Join-Path $Root "install"
$InstallerName = "fixture-installer.cmd"
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

    $Cosign = Join-Path $Root "cosign.cmd"
    "@echo off`r`nexit /b 0`r`n" | Set-Content -LiteralPath $Cosign -Encoding ascii
    $Trust = Join-Path $Root "trusted-root.json"
    '{"mediaType":"fixture-trusted-root"}' | Set-Content -LiteralPath $Trust -Encoding utf8
    & python $FixtureScript --bundle $Bundle --installer $InstallerName --version $Version --commit $Commit --feed $Feed
    if ($LASTEXITCODE -ne 0) { throw "Creating the Windows offline fixture failed." }
    $CosignDigest = (Get-FileHash -LiteralPath $Cosign -Algorithm SHA256).Hash.ToLowerInvariant()

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
        $Stdout = Join-Path $Root "$Mode.out.txt"
        $Stderr = Join-Path $Root "$Mode.err.txt"
        $Arguments = @(
            "-NoLogo", "-NoProfile", "-NonInteractive", "-File", $InstallerScript,
            "-BundleDirectory", $Bundle, "-TrustedRoot", $Trust, "-CosignPath", $Cosign,
            "-CosignSha256", $CosignDigest, "-InstallRoot", $Install, "-Headless",
            "-Ring", "stable", "-FeedRepository", $Feed)
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
    & python $FixtureScript --bundle $Bundle --installer $InstallerName --version $Version --commit $Commit `
        --feed $Feed --inconsistent-rollback
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
