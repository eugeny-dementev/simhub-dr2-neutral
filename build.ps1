[CmdletBinding()]
param(
    [string]$SimHubPath = "${env:ProgramFiles(x86)}\SimHub",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3.0

$project = Join-Path $PSScriptRoot "src\DR2Neutral.csproj"
$outputDll = Join-Path $PSScriptRoot "src\bin\$Configuration\DR2Neutral.dll"
$userDotNet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"

function Assert-FileExists {
    param([string]$Path, [string]$Description)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description was not found: $Path"
    }
}

function Find-DotNetSdk {
    $candidates = @($userDotNet)
    $pathCommand = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($null -ne $pathCommand) {
        $candidates += $pathCommand.Source
    }

    foreach ($candidate in @($candidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }

        $sdks = @(& $candidate --list-sdks 2>$null)
        if ($LASTEXITCODE -eq 0 -and $sdks.Count -gt 0) {
            return $candidate
        }
    }

    throw "A .NET SDK was not found. Install the current .NET LTS SDK from https://dotnet.microsoft.com/download."
}

function Assert-X86PortableExecutable {
    param([string]$Path)

    [byte[]]$bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 512) {
        throw "Built DLL is too small to be a valid PE file."
    }

    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ($peOffset -lt 0 -or $peOffset + 6 -gt $bytes.Length) {
        throw "Built DLL has an invalid PE header offset."
    }

    $machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
    if ($machine -ne 0x014c) {
        throw "Built DLL is not x86 (PE machine 0x$($machine.ToString('x4')))."
    }
}

Assert-FileExists -Path $project -Description "Plug-in project"
foreach ($requiredAssembly in @(
    "GameReaderCommon.dll",
    "SimHub.Plugins.dll",
    "SimHub.Logging.dll",
    "log4net.dll",
    "SharpDX.dll",
    "SharpDX.DirectInput.dll"
)) {
    Assert-FileExists `
        -Path (Join-Path $SimHubPath $requiredAssembly) `
        -Description "SimHub SDK reference"
}

$dotnet = Find-DotNetSdk
$escapedSimHubPath = $SimHubPath.TrimEnd('\')

Write-Host "Building DR2Neutral $Configuration (x86 / .NET Framework 4.8)..."
& $dotnet build $project `
    --configuration $Configuration `
    --nologo `
    --property:RestoreLockedMode=true `
    --property:SimHubPath="$escapedSimHubPath" `
    --property:PlatformTarget=x86
if ($LASTEXITCODE -ne 0) {
    throw "Plug-in build failed with exit code $LASTEXITCODE."
}

Assert-FileExists -Path $outputDll -Description "Built plug-in"
Assert-X86PortableExecutable -Path $outputDll

$hash = Get-FileHash -LiteralPath $outputDll -Algorithm SHA256
Write-Host "Built: $outputDll" -ForegroundColor Green
Write-Host "SHA256: $($hash.Hash)"
