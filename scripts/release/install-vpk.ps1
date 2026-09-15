[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Destination
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Version = "1.2.0"
$MaximumBytes = 140000000
$PackageName = "vpk.$Version.nupkg"
$PackageUri = "https://api.nuget.org/v3-flatcontainer/vpk/$Version/$PackageName"
$PinFile = Join-Path $PSScriptRoot "vpk.sha256"

if (-not (Test-Path -LiteralPath $PinFile -PathType Leaf)) {
    throw "The committed Velopack content pin is missing."
}
$PinLine = (Get-Content -LiteralPath $PinFile -Raw).Trim()
if ($PinLine -notmatch "^(?<digest>[0-9a-f]{64})  (?<name>[a-z0-9.-]+)$" -or $Matches.name -cne $PackageName) {
    throw "The committed Velopack content pin is malformed or names another package."
}
$ExpectedDigest = $Matches.digest

$DestinationPath = [System.IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $DestinationPath) {
    throw "The Velopack tool destination already exists: $DestinationPath"
}
$Staging = "$DestinationPath.download"
if (Test-Path -LiteralPath $Staging) {
    throw "The Velopack staging directory already exists: $Staging"
}
[System.IO.Directory]::CreateDirectory($Staging) | Out-Null
$Package = Join-Path $Staging $PackageName

try {
    Add-Type -AssemblyName System.Net.Http
    $Handler = [System.Net.Http.HttpClientHandler]::new()
    $Handler.AllowAutoRedirect = $false
    $Client = [System.Net.Http.HttpClient]::new($Handler)
    try {
        $Response = $Client.GetAsync(
            $PackageUri,
            [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            if (-not $Response.IsSuccessStatusCode) {
                throw "NuGet returned HTTP $([int] $Response.StatusCode) for the pinned Velopack package."
            }
            if ($Response.Content.Headers.ContentLength -and $Response.Content.Headers.ContentLength -gt $MaximumBytes) {
                throw "The pinned Velopack package is larger than the $MaximumBytes-byte limit."
            }
            $Input = $Response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $Output = [System.IO.File]::Create($Package)
            try {
                $Buffer = New-Object byte[] 65536
                [long] $Total = 0
                while (($Read = $Input.Read($Buffer, 0, $Buffer.Length)) -gt 0) {
                    $Total += $Read
                    if ($Total -gt $MaximumBytes) {
                        throw "The pinned Velopack package exceeded the $MaximumBytes-byte limit while downloading."
                    }
                    $Output.Write($Buffer, 0, $Read)
                }
            }
            finally {
                $Output.Dispose()
                $Input.Dispose()
            }
        }
        finally {
            $Response.Dispose()
        }
    }
    finally {
        $Client.Dispose()
        $Handler.Dispose()
    }

    $ActualDigest = (Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($ActualDigest -cne $ExpectedDigest) {
        throw "The Velopack package digest is $ActualDigest, not the reviewed $ExpectedDigest."
    }

    $Config = Join-Path $Staging "NuGet.Config"
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="verified-local" value="$Staging" />
  </packageSources>
</configuration>
"@ | Set-Content -LiteralPath $Config -Encoding utf8

    # This script's success output is an API: its caller captures the one executable path below.
    # Keep dotnet's informational output visible without letting it become part of that value.
    & dotnet tool install --tool-path $DestinationPath vpk --version $Version --configfile $Config --no-cache |
        Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Installing the content-verified Velopack CLI failed with exit code $LASTEXITCODE."
    }
    $Executable = Join-Path $DestinationPath "vpk.exe"
    if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
        throw "The content-verified Velopack install did not produce vpk.exe."
    }
    Write-Output $Executable
}
finally {
    if (Test-Path -LiteralPath $Staging) {
        Remove-Item -LiteralPath $Staging -Recurse -Force
    }
}
