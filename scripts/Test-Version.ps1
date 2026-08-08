[CmdletBinding()]
param(
    [string]$Version,
    [string]$Tag = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3.0

$root = Split-Path -Parent $PSScriptRoot
$versionFile = Join-Path $root "version.json"
if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) {
    throw "Version manifest was not found: $versionFile"
}

$manifest = Get-Content -LiteralPath $versionFile -Raw | ConvertFrom-Json
$manifestVersion = [string]$manifest.version
if ($manifestVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "version.json must contain a major.minor.patch version."
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $manifestVersion
}
if ($Version -ne $manifestVersion) {
    throw "Requested version $Version does not match version.json ($manifestVersion)."
}
if (-not [string]::IsNullOrWhiteSpace($Tag) -and $Tag -ne "v$Version") {
    throw "Release tag $Tag does not match version v$Version."
}

$main = Get-Content -LiteralPath (Join-Path $root "src\DR2NeutralPlugin.cs") -Raw
$assembly = Get-Content -LiteralPath (Join-Path $root "src\AssemblyInfo.cs") -Raw
$changelog = Get-Content -LiteralPath (Join-Path $root "CHANGELOG.md") -Raw

$checks = @(
    [PSCustomObject]@{
        Condition = $main.Contains("public const string PluginVersion = `"$Version`";")
        Message = "PluginVersion does not match $Version."
    },
    [PSCustomObject]@{
        Condition = $assembly.Contains("AssemblyVersion(`"$Version.0`")")
        Message = "AssemblyVersion does not match $Version.0."
    },
    [PSCustomObject]@{
        Condition = $assembly.Contains("AssemblyFileVersion(`"$Version.0`")")
        Message = "AssemblyFileVersion does not match $Version.0."
    },
    [PSCustomObject]@{
        Condition = $assembly.Contains("AssemblyInformationalVersion(`"$Version`")")
        Message = "AssemblyInformationalVersion does not match $Version."
    },
    [PSCustomObject]@{
        Condition = $changelog.Contains("## [$Version]")
        Message = "CHANGELOG.md has no section for $Version."
    }
)

foreach ($check in $checks) {
    if (-not $check.Condition) {
        throw $check.Message
    }
}

Write-Host "Version metadata is consistent: $Version" -ForegroundColor Green
return $Version
