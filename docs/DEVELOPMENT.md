# Development

## Prerequisites

- Windows 10 or later.
- Git and PowerShell 5.1 or later.
- The .NET SDK selected by `global.json`.
- SimHub installed locally for its plugin API assemblies.

The project builds with a current .NET SDK but targets .NET Framework 4.8/x86.
SimHub and SharpDX assemblies remain runtime-provided and are never copied into
the release package.

## Commands

```powershell
# Compile the plugin and verify x86 PE metadata
.\build.ps1

# Build, run core tests, and inspect package/runtime safeguards
.\test.ps1

# Produce artifacts\DR2Neutral-<version>-SimHub.zip and SHA256SUMS.txt
.\package.ps1
```

For a nonstandard SimHub directory:

```powershell
.\test.ps1 -SimHubPath 'D:\Apps\SimHub'
```

`build-and-install.ps1` is a developer convenience that tests before invoking
the normal rollback-capable installer.

## CI build inputs

GitHub Actions runs on a Windows image. `scripts/Get-SimHubSdk.ps1` downloads a
specific official SimHub release, checks the pinned SHA-256 digest and expected
Authenticode publisher, performs a silent temporary install, and exposes only
the required reference directory to the build. The ephemeral runner is then
discarded. This keeps builds reproducible without committing or redistributing
SimHub files.

## Tests

`tests/CoreTests.cs` checks deterministic gear normalization, Neutral routing,
settings validation, and input edge behavior. `tests/verify-package.ps1` checks
public action/property names, fixed safety bounds, project metadata, installer
rollback guarantees, architecture, and framework metadata.

Real-game acceptance remains manual because it depends on DiRT Rally 2.0,
SimHub telemetry, and physical controls. Follow the stationary test in README.
