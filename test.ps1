[CmdletBinding()]
param(
    [string]$SimHubPath = "${env:ProgramFiles(x86)}\SimHub"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3.0

& (Join-Path $PSScriptRoot "scripts\Test-Version.ps1") | Out-Null
& (Join-Path $PSScriptRoot "build.ps1") -SimHubPath $SimHubPath

function Find-DotNetSdk {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe")
    )
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

    throw "A .NET SDK was not found. Install the SDK selected by global.json."
}

$dotnet = Find-DotNetSdk
$testProject = Join-Path $PSScriptRoot "tests\DR2Neutral.Tests.csproj"

& $dotnet restore $testProject `
    --locked-mode `
    --nologo `
    --property:SimHubPath="$($SimHubPath.TrimEnd('\'))" `
    --property:PlatformTarget=x86
if ($LASTEXITCODE -ne 0) {
    throw "Test restore failed with exit code $LASTEXITCODE."
}

& $dotnet build $testProject `
    --configuration Release `
    --no-restore `
    --nologo `
    --property:BuildProjectReferences=false `
    --property:SimHubPath="$($SimHubPath.TrimEnd('\'))" `
    --property:PlatformTarget=x86
if ($LASTEXITCODE -ne 0) {
    throw "Test build failed with exit code $LASTEXITCODE."
}

$testExe = Join-Path $PSScriptRoot "tests\bin\Release\net48\DR2Neutral.Tests.exe"
if (-not (Test-Path -LiteralPath $testExe -PathType Leaf)) {
    throw "Test executable was not produced: $testExe"
}

& $testExe
if ($LASTEXITCODE -ne 0) {
    throw "Core tests failed with exit code $LASTEXITCODE."
}

& (Join-Path $PSScriptRoot "tests\verify-package.ps1")
