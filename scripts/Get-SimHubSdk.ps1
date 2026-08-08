[CmdletBinding()]
param(
    [string]$Version = "9.11.22",
    [string]$ExpectedSha256 = "D2239182E07A74B5A2B14782F256C63ACB2B4126CD75FBDF760F8A227376E895",
    [Parameter(Mandatory = $true)]
    [string]$Destination
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3.0

$maximumArchiveBytes = 300MB
$requiredAssemblies = @(
    "GameReaderCommon.dll",
    "SimHub.Plugins.dll",
    "SimHub.Logging.dll",
    "log4net.dll",
    "SharpDX.dll",
    "SharpDX.DirectInput.dll"
)

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "SimHub version must use major.minor.patch format."
}
if ($ExpectedSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
    throw "ExpectedSha256 must be a SHA-256 digest."
}

$Destination = [IO.Path]::GetFullPath($Destination.TrimEnd('\'))
$complete = $true
foreach ($assembly in $requiredAssemblies) {
    if (-not (Test-Path -LiteralPath (Join-Path $Destination $assembly) -PathType Leaf)) {
        $complete = $false
        break
    }
}
if ($complete) {
    Write-Host "Using cached SimHub SDK: $Destination"
    return $Destination
}

$workingRoot = Join-Path ([IO.Path]::GetTempPath()) ("DR2Neutral-SimHubSdk-" + [Guid]::NewGuid().ToString("N"))
$archivePath = Join-Path $workingRoot "SimHub.zip"
$extractPath = Join-Path $workingRoot "archive"
$installerPath = Join-Path $extractPath "SimHubSetup_$Version.exe"
$downloadUrl = "https://github.com/SHWotever/SimHub/releases/download/$Version/SimHub.$Version.zip"

try {
    New-Item -ItemType Directory -Path $workingRoot, $extractPath -Force | Out-Null
    Write-Host "Downloading pinned SimHub $Version build input..."
    Invoke-WebRequest -Uri $downloadUrl -OutFile $archivePath -MaximumRedirection 5

    $archive = Get-Item -LiteralPath $archivePath
    if ($archive.Length -le 0 -or $archive.Length -gt $maximumArchiveBytes) {
        throw "Downloaded SimHub archive has an invalid size: $($archive.Length) bytes."
    }

    $actualSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    if ($actualSha256 -ne $ExpectedSha256.ToUpperInvariant()) {
        throw "SimHub archive checksum mismatch. Expected $ExpectedSha256; received $actualSha256."
    }

    Expand-Archive -LiteralPath $archivePath -DestinationPath $extractPath
    if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
        throw "Pinned SimHub archive did not contain $installerPath."
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $installerPath
    $subject = if ($null -eq $signature.SignerCertificate) {
        ""
    }
    else {
        [string]$signature.SignerCertificate.Subject
    }
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or
        -not $subject.Contains("CN=Nicolas Guilbert")) {
        throw "SimHub installer signature is not valid for the expected publisher. Status: $($signature.Status); signer: $subject"
    }

    if (Test-Path -LiteralPath $Destination) {
        $existing = Get-ChildItem -LiteralPath $Destination -Force -ErrorAction SilentlyContinue
        if ($existing.Count -gt 0) {
            throw "Refusing to install build inputs into non-empty directory: $Destination"
        }
    }
    else {
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    }

    $logPath = Join-Path $workingRoot "SimHubSetup.log"
    $arguments = @(
        "/VERYSILENT",
        "/SUPPRESSMSGBOXES",
        "/NORESTART",
        "/SP-",
        "/NOICONS",
        "/DIR=`"$Destination`"",
        "/LOG=`"$logPath`""
    )
    $process = Start-Process -FilePath $installerPath -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "SimHub temporary installation failed with exit code $($process.ExitCode). See $logPath."
    }

    foreach ($assembly in $requiredAssemblies) {
        $path = Join-Path $Destination $assembly
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "SimHub build input is missing after installation: $path"
        }
    }

    Write-Host "Prepared verified SimHub $Version SDK inputs: $Destination" -ForegroundColor Green
    return $Destination
}
finally {
    if (Test-Path -LiteralPath $workingRoot) {
        Remove-Item -LiteralPath $workingRoot -Recurse -Force
    }
}
