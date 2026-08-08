[CmdletBinding()]
param(
    [string]$SimHubPath = "${env:ProgramFiles(x86)}\SimHub"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3.0

& (Join-Path $PSScriptRoot "test.ps1") -SimHubPath $SimHubPath
if ($LASTEXITCODE -ne 0) {
    throw "Build or verification failed with exit code $LASTEXITCODE."
}

& (Join-Path $PSScriptRoot "install.ps1") -SimHubPath $SimHubPath
