[CmdletBinding()]
param(
    [string]$SimHubPath = "${env:ProgramFiles(x86)}\SimHub",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3.0

function New-DeterministicZip {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $sourceRoot = [IO.Path]::GetFullPath($SourceDirectory.TrimEnd('\'))
    $destination = [IO.Path]::GetFullPath($DestinationPath)
    if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
        throw "Archive source directory was not found: $sourceRoot"
    }
    if (Test-Path -LiteralPath $destination) {
        throw "Archive destination already exists: $destination"
    }

    $fixedTimestamp = [DateTimeOffset]::new(
        2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $output = [IO.File]::Open(
        $destination,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $output,
            [IO.Compression.ZipArchiveMode]::Create,
            $true)
        try {
            $files = Get-ChildItem -LiteralPath $sourceRoot -File -Recurse |
                Sort-Object { $_.FullName.Substring($sourceRoot.Length + 1) }
            foreach ($file in $files) {
                $relativeName = $file.FullName.Substring($sourceRoot.Length + 1).Replace('\', '/')
                $entry = $archive.CreateEntry(
                    $relativeName,
                    [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $fixedTimestamp

                $input = [IO.File]::OpenRead($file.FullName)
                $entryOutput = $entry.Open()
                try {
                    $input.CopyTo($entryOutput)
                }
                finally {
                    $entryOutput.Dispose()
                    $input.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $output.Dispose()
    }
}

$Version = & (Join-Path $PSScriptRoot "scripts\Test-Version.ps1") -Version $Version

& (Join-Path $PSScriptRoot "test.ps1") -SimHubPath $SimHubPath
if ($LASTEXITCODE -ne 0) {
    throw "Tests failed with exit code $LASTEXITCODE."
}

$artifactRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "artifacts"))
$stagingRoot = Join-Path $artifactRoot "staging"
$releaseStage = Join-Path $stagingRoot ("DR2Neutral-$Version-SimHub")

if (Test-Path -LiteralPath $stagingRoot -PathType Container) {
    $resolvedStaging = [IO.Path]::GetFullPath($stagingRoot)
    if (-not $resolvedStaging.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar)) {
        throw "Refusing to clean an unexpected staging path: $resolvedStaging"
    }
    Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
}

New-Item -ItemType Directory -Path $releaseStage -Force | Out-Null

$pluginDll = Join-Path $PSScriptRoot "src\bin\Release\DR2Neutral.dll"
$pluginPdb = Join-Path $PSScriptRoot "src\bin\Release\DR2Neutral.pdb"
foreach ($required in @(
    $pluginDll,
    (Join-Path $PSScriptRoot "Install.cmd"),
    (Join-Path $PSScriptRoot "install.ps1"),
    (Join-Path $PSScriptRoot "Uninstall.cmd"),
    (Join-Path $PSScriptRoot "uninstall.ps1"),
    (Join-Path $PSScriptRoot "README.md"),
    (Join-Path $PSScriptRoot "docs\INSTALLATION.md"),
    (Join-Path $PSScriptRoot "docs\TROUBLESHOOTING.md"),
    (Join-Path $PSScriptRoot "LICENSE")
)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required release file is missing: $required"
    }
}

$releaseFiles = @(
    "Install.cmd",
    "install.ps1",
    "Uninstall.cmd",
    "uninstall.ps1",
    "README.md",
    "CONTRIBUTING.md",
    "CODE_OF_CONDUCT.md",
    "SECURITY.md",
    "SUPPORT.md",
    "THIRD_PARTY_NOTICES.md",
    "docs\INSTALLATION.md",
    "docs\TROUBLESHOOTING.md",
    "docs\ARCHITECTURE.md",
    "docs\DEVELOPMENT.md",
    "docs\RELEASING.md",
    "docs\images\settings-page.png",
    "LICENSE",
    "CHANGELOG.md"
)
foreach ($relativePath in $releaseFiles) {
    $destinationPath = Join-Path $releaseStage $relativePath
    $destinationDirectory = Split-Path -Parent $destinationPath
    if (-not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $relativePath) -Destination $destinationPath
}
Copy-Item -LiteralPath $pluginDll -Destination $releaseStage
if (Test-Path -LiteralPath $pluginPdb -PathType Leaf) {
    Copy-Item -LiteralPath $pluginPdb -Destination $releaseStage
}

$releaseChecksums = Get-ChildItem -LiteralPath $releaseStage -File -Recurse |
    Sort-Object FullName |
    ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        $relativeName = $_.FullName.Substring($releaseStage.Length + 1).Replace('\', '/')
        "$hash *$relativeName"
    }
[IO.File]::WriteAllLines(
    (Join-Path $releaseStage "SHA256SUMS.txt"),
    [string[]]$releaseChecksums,
    [Text.UTF8Encoding]::new($false))

$releaseZip = Join-Path $artifactRoot ("DR2Neutral-$Version-SimHub.zip")
if (Test-Path -LiteralPath $releaseZip -PathType Leaf) {
    Remove-Item -LiteralPath $releaseZip -Force
}

New-DeterministicZip -SourceDirectory $releaseStage -DestinationPath $releaseZip

$archiveChecksums = @($releaseZip) | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash
    "$hash *$(Split-Path -Leaf $_)"
}
[IO.File]::WriteAllLines(
    (Join-Path $artifactRoot "SHA256SUMS.txt"),
    [string[]]$archiveChecksums,
    [Text.UTF8Encoding]::new($false))

Write-Host "Community packages created:" -ForegroundColor Green
Write-Host "  $releaseZip"
Write-Host "  $(Join-Path $artifactRoot 'SHA256SUMS.txt')"
