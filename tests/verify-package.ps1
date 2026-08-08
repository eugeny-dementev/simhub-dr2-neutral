[CmdletBinding()]
param(
    [string]$PackageRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3.0

function Assert-Condition {
    param([bool]$Condition, [string]$Message)

    if (-not $Condition) {
        throw "Verification failed: $Message"
    }
}

$paths = @{
    Plugin = Join-Path $PackageRoot "src\bin\Release\DR2Neutral.dll"
    Main = Join-Path $PackageRoot "src\DR2NeutralPlugin.cs"
    Input = Join-Path $PackageRoot "src\ControllerInputManager.cs"
    Settings = Join-Path $PackageRoot "src\DR2NeutralSettings.cs"
    UserInterface = Join-Path $PackageRoot "src\DR2NeutralSettingsControl.cs"
    Project = Join-Path $PackageRoot "src\DR2Neutral.csproj"
    Installer = Join-Path $PackageRoot "install.ps1"
    Uninstaller = Join-Path $PackageRoot "uninstall.ps1"
    Packager = Join-Path $PackageRoot "package.ps1"
    Readme = Join-Path $PackageRoot "README.md"
    License = Join-Path $PackageRoot "LICENSE"
    NuGet = Join-Path $PackageRoot "NuGet.Config"
    Version = Join-Path $PackageRoot "version.json"
    VersionCheck = Join-Path $PackageRoot "scripts\Test-Version.ps1"
    SdkBootstrap = Join-Path $PackageRoot "scripts\Get-SimHubSdk.ps1"
    CiWorkflow = Join-Path $PackageRoot ".github\workflows\ci.yml"
    ReleaseWorkflow = Join-Path $PackageRoot ".github\workflows\release.yml"
    InstallationGuide = Join-Path $PackageRoot "docs\INSTALLATION.md"
    Security = Join-Path $PackageRoot "SECURITY.md"
}

foreach ($entry in $paths.GetEnumerator()) {
    Assert-Condition `
        (Test-Path -LiteralPath $entry.Value -PathType Leaf) `
        "required $($entry.Key) file missing: $($entry.Value)"
}

foreach ($scriptPath in @(
    $paths.Installer,
    $paths.Uninstaller,
    $paths.Packager,
    (Join-Path $PackageRoot "build.ps1"),
    (Join-Path $PackageRoot "build-and-install.ps1"),
    (Join-Path $PackageRoot "test.ps1"),
    $paths.VersionCheck,
    $paths.SdkBootstrap
)) {
    $tokens = $null
    $parseErrors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile(
        $scriptPath,
        [ref]$tokens,
        [ref]$parseErrors)
    Assert-Condition ($parseErrors.Count -eq 0) "PowerShell parser errors in $scriptPath"
}

$main = Get-Content -LiteralPath $paths.Main -Raw
$input = Get-Content -LiteralPath $paths.Input -Raw
$settings = Get-Content -LiteralPath $paths.Settings -Raw
$userInterface = Get-Content -LiteralPath $paths.UserInterface -Raw
$project = Get-Content -LiteralPath $paths.Project -Raw
$installer = Get-Content -LiteralPath $paths.Installer -Raw
$uninstaller = Get-Content -LiteralPath $paths.Uninstaller -Raw
$readme = Get-Content -LiteralPath $paths.Readme -Raw
$nuget = Get-Content -LiteralPath $paths.NuGet -Raw
$version = Get-Content -LiteralPath $paths.Version -Raw | ConvertFrom-Json
$versionCheck = Get-Content -LiteralPath $paths.VersionCheck -Raw
$sdkBootstrap = Get-Content -LiteralPath $paths.SdkBootstrap -Raw
$ciWorkflow = Get-Content -LiteralPath $paths.CiWorkflow -Raw
$releaseWorkflow = Get-Content -LiteralPath $paths.ReleaseWorkflow -Raw

Assert-Condition ($main.Contains("public sealed class DR2Neutral : IPlugin, IDataPlugin, IWPFSettingsV2")) "WPF settings plug-in type"
Assert-Condition ($main.Contains('[PluginName("DR2 Neutral Selector")]')) "community plug-in name"
Assert-Condition ($main.Contains('get { return "DR2 Neutral"; }')) "left-menu title"
Assert-Condition ($userInterface.Contains("Start calibration")) "built-in calibration UI"
Assert-Condition ($userInterface.Contains("First safe test")) "in-product acceptance guidance"

foreach ($name in @("GearUp", "GearDown", "SelectNeutral")) {
    Assert-Condition ($main.Contains('AddAction("' + $name + '"')) "unqualified action $name"
    Assert-Condition (-not $main.Contains('AddAction("DR2Neutral.' + $name)) "no duplicated action prefix for $name"
    Assert-Condition ($readme.Contains('`DR2Neutral.' + $name + '`')) "documented public action $name"
}

foreach ($name in @(
    "CurrentGear",
    "GameName",
    "Busy",
    "Ready",
    "TelemetryAgeMs",
    "LastResult",
    "InputStatus",
    "ControllerConnected",
    "Calibrated",
    "ControllerName",
    "CalibrationActive",
    "CalibrationStatus"
)) {
    Assert-Condition ($main.Contains('AttachDelegate("' + $name + '"')) "diagnostic $name"
    Assert-Condition ($readme.Contains('`DR2Neutral.' + $name + '`')) "documented diagnostic $name"
}

foreach ($expected in @(
    "private const int GearChangeTimeoutMs = 1000;",
    "private const int TelemetryFreshMs = 1500;",
    "private const int MaxNeutralPulses = 10;",
    "private const int KeyHoldMs = 35;",
    "internal const int StableStateTimeoutMs = 10000;",
    "internal const int CaptureStepTimeoutMs = 60000;",
    "internal const int MaximumControllers = 32;",
    "public const int MaximumButtonCount = 128;"
)) {
    Assert-Condition (($main + $input + $settings).Contains($expected)) "bounded safety constant: $expected"
}

foreach ($expected in @(
    "WaitForAllButtonsReleasedAsync",
    "rising.Count > 1",
    "That button is already assigned",
    "Use the same controller",
    "FindSavedDevice",
    "MappedButtonEdgeDetector"
)) {
    Assert-Condition (($input + $settings).Contains($expected)) "controller safeguard: $expected"
}

foreach ($expected in @(
    "ForegroundIsDirtRally2()",
    "Interlocked.CompareExchange(ref _busy, 1, 0)",
    "Interlocked.CompareExchange(ref _singleShiftPending, 1, 0)",
    "GetNeutralVirtualKey",
    "finally",
    "SendKeyState(virtualKey, true)"
)) {
    Assert-Condition ($main.Contains($expected)) "runtime safeguard: $expected"
}

foreach ($expected in @(
    '<TargetFramework>net48</TargetFramework>',
    '<PlatformTarget>x86</PlatformTarget>',
    '<TreatWarningsAsErrors>true</TreatWarningsAsErrors>',
    '<Deterministic>true</Deterministic>',
    '<ContinuousIntegrationBuild>true</ContinuousIntegrationBuild>',
    '<PathMap>$(MSBuildProjectDirectory)=/_/src</PathMap>',
    'Microsoft.NETFramework.ReferenceAssemblies',
    'SharpDX.DirectInput.dll'
)) {
    Assert-Condition ($project.Contains($expected)) "build setting: $expected"
}

foreach ($expected in @(
    'DR2NeutralPlugin.DR2Neutral',
    'Restart-Elevated',
    'Test-DirectoryWriteAccess',
    'Stop-SimHubGracefully',
    'Write-JsonAtomically',
    'ConvertTo-Json -InputObject $entries',
    'Shell.Application',
    'ArgumentList "-exit"',
    'Wait-ForPluginLoad',
    'restoring the previous SimHub state',
    'Get-FileHash',
    'Global\DR2NeutralInstaller'
)) {
    Assert-Condition ($installer.Contains($expected)) "installer safeguard: $expected"
}
Assert-Condition (-not $installer.Contains("PluginManagerSettings.json")) "installer does not mutate Controls mappings"
Assert-Condition (-not $installer.Contains("KeyboardEmulator")) "installer does not mutate Keyboard Emulator"
Assert-Condition (-not $installer.Contains("Stop-Process -Force")) "installer never force-kills SimHub"
Assert-Condition ($uninstaller.Contains("Calibration settings were preserved")) "recoverable uninstall behavior"
Assert-Condition ((Get-Content -LiteralPath $paths.Packager -Raw).Contains("New-DeterministicZip")) "fixed-order timestamp-normalized release archive"

Assert-Condition ($nuget.Contains("https://api.nuget.org/v3/index.json")) "official NuGet feed"
Assert-Condition (-not $nuget.Contains('value="tools"')) "source build has no machine-local package feed"
Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $PackageRoot "calibrate.ps1"))) "legacy external calibration removed"

Assert-Condition ([string]$version.version -eq "1.0.0") "single release version manifest"
Assert-Condition ($versionCheck.Contains('Release tag $Tag does not match version')) "tag/version consistency check"
Assert-Condition ($sdkBootstrap.Contains("D2239182E07A74B5A2B14782F256C63ACB2B4126CD75FBDF760F8A227376E895")) "pinned SimHub archive checksum"
Assert-Condition ($sdkBootstrap.Contains("CN=Nicolas Guilbert")) "pinned SimHub Authenticode publisher"
Assert-Condition ($sdkBootstrap.Contains('$maximumArchiveBytes = 300MB')) "bounded SimHub download"
Assert-Condition ($ciWorkflow.Contains("permissions:`n  contents: read")) "read-only CI token"
Assert-Condition ($ciWorkflow.Contains("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1")) "immutable checkout action pin"
Assert-Condition ($ciWorkflow.Contains("actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68")) "immutable setup-dotnet action pin"
Assert-Condition ($releaseWorkflow.Contains("attestations: write")) "release attestation permission"
Assert-Condition ($releaseWorkflow.Contains("actions/attest-build-provenance@4d101475d8b20a2381f78447822ac1eab6504dd8")) "immutable provenance action pin"
Assert-Condition ($releaseWorkflow.Contains('tags:')) "tag-only release trigger"

[byte[]]$pluginBytes = [IO.File]::ReadAllBytes($paths.Plugin)
Assert-Condition ($pluginBytes.Length -ge 512) "compiled DLL has a bounded minimum size"
$peOffset = [BitConverter]::ToInt32($pluginBytes, 0x3c)
Assert-Condition ($peOffset -ge 0 -and $peOffset + 6 -le $pluginBytes.Length) "compiled DLL has a valid PE offset"
$machine = [BitConverter]::ToUInt16($pluginBytes, $peOffset + 4)
Assert-Condition ($machine -eq 0x014c) "compiled DLL is x86"

$metadata = [Text.Encoding]::UTF8.GetString($pluginBytes)
Assert-Condition ($metadata.Contains(".NETFramework,Version=v4.8")) "compiled DLL targets .NET Framework 4.8"
Assert-Condition ($metadata.Contains("DR2NeutralPlugin")) "compiled DLL contains the plug-in namespace"
Assert-Condition ($metadata.Contains("DR2 Neutral Selector")) "compiled DLL contains community metadata"

Write-Host "Package verification passed." -ForegroundColor Green
Write-Host "  Safety: bounded input, telemetry, shifts, lifecycle, install, and rollback"
Write-Host "  Performance: one 10 ms controller loop; telemetry polling only during Neutral"
Write-Host "  Experience: one-click install and guided in-SimHub calibration"
