[CmdletBinding()]
param(
    [string]$SimHubPath = "${env:ProgramFiles(x86)}\SimHub",
    [switch]$Elevated
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3.0

$pluginClassName = "DR2NeutralPlugin.DR2Neutral"
$shutdownTimeoutSeconds = 15
$maximumJsonBytes = 4MB

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-DirectoryWriteAccess {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return $false
    }

    $probePath = Join-Path $Path (".dr2neutral-write-probe-" + [Guid]::NewGuid().ToString("N") + ".tmp")
    try {
        $stream = [IO.File]::Open(
            $probePath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        $stream.Dispose()
        return $true
    }
    catch {
        return $false
    }
    finally {
        if (Test-Path -LiteralPath $probePath -PathType Leaf) {
            Remove-Item -LiteralPath $probePath -Force
        }
    }
}

function Restart-Elevated {
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", ('"' + $PSCommandPath + '"'),
        "-SimHubPath", ('"' + $SimHubPath + '"'),
        "-Elevated"
    )
    $process = Start-Process `
        -FilePath "powershell.exe" `
        -Verb RunAs `
        -ArgumentList $arguments `
        -Wait `
        -PassThru
    exit $process.ExitCode
}

function Read-Activation {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "SimHub activation settings were not found: $Path"
    }
    $length = (Get-Item -LiteralPath $Path).Length
    if ($length -le 0 -or $length -gt $maximumJsonBytes) {
        throw "SimHub activation settings have an invalid size ($length bytes)."
    }
    return @(Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
}

function Write-JsonAtomically {
    param([string]$Path, [object]$Value)

    $temporaryPath = "$Path.dr2neutral.$PID.tmp"
    $replacementBackupPath = "$Path.dr2neutral.$PID.replace-backup"
    try {
        $entries = @()
        foreach ($entry in $Value) {
            if ($null -eq $entry -or [string]::IsNullOrWhiteSpace($entry.ClassName)) {
                throw "Refusing to write an invalid SimHub activation entry."
            }
            $entries += $entry
        }
        $json = ConvertTo-Json -InputObject $entries -Depth 20
        [IO.File]::WriteAllText(
            $temporaryPath,
            $json + [Environment]::NewLine,
            [Text.UTF8Encoding]::new($false))
        $null = Get-Content -LiteralPath $temporaryPath -Raw | ConvertFrom-Json
        [IO.File]::Replace($temporaryPath, $Path, $replacementBackupPath)
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
        if (Test-Path -LiteralPath $replacementBackupPath -PathType Leaf) {
            Remove-Item -LiteralPath $replacementBackupPath -Force
        }
    }
}

function Stop-SimHubGracefully {
    param([string]$Executable, [string]$WorkingDirectory)

    $processes = @(Get-Process -Name "SimHubWPF", "SimHub" -ErrorAction SilentlyContinue)
    if ($processes.Count -eq 0) {
        return
    }
    $deadline = [DateTime]::UtcNow.AddSeconds($shutdownTimeoutSeconds)
    $nextExitRequest = [DateTime]::MinValue
    do {
        if ([DateTime]::UtcNow -ge $nextExitRequest) {
            Start-Process `
                -FilePath $Executable `
                -ArgumentList "-exit" `
                -WorkingDirectory $WorkingDirectory `
                -WindowStyle Hidden
            $nextExitRequest = [DateTime]::UtcNow.AddSeconds(2)
        }
        foreach ($process in $processes) {
            try { $null = $process.CloseMainWindow() } catch { }
        }
        Start-Sleep -Milliseconds 250
        $processes = @(Get-Process -Name "SimHubWPF", "SimHub" -ErrorAction SilentlyContinue)
    } while ($processes.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline)
    if ($processes.Count -gt 0) {
        throw "SimHub did not close within $shutdownTimeoutSeconds seconds. Close it manually and try again."
    }
}

function Start-SimHubUnelevated {
    param([string]$Executable, [string]$WorkingDirectory)

    $shell = New-Object -ComObject "Shell.Application"
    try {
        $shell.ShellExecute($Executable, "", $WorkingDirectory, "open", 1)
    }
    finally {
        if ($null -ne $shell) {
            $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
        }
    }
}

$SimHubPath = [IO.Path]::GetFullPath($SimHubPath.TrimEnd('\'))
if (-not (Test-IsAdministrator) -and -not (Test-DirectoryWriteAccess -Path $SimHubPath)) {
    if ($Elevated) {
        throw "Administrator permission was requested but not granted."
    }
    Write-Host "Windows will ask permission to remove the plug-in from SimHub."
    Restart-Elevated
}

$simHubExecutable = Join-Path $SimHubPath "SimHubWPF.exe"
$activationPath = Join-Path $SimHubPath "PluginsData\PluginsActivation.json"
$destinationDll = Join-Path $SimHubPath "DR2Neutral.dll"
$destinationPdb = Join-Path $SimHubPath "DR2Neutral.pdb"

if (-not (Test-Path -LiteralPath $simHubExecutable -PathType Leaf)) {
    throw "SimHub was not found at: $SimHubPath"
}

$mutex = [Threading.Mutex]::new($false, "Global\DR2NeutralInstaller")
$lockTaken = $false
$simHubWasRunning = [bool](Get-Process -Name "SimHubWPF", "SimHub" -ErrorAction SilentlyContinue)
$backupRoot = Join-Path $env:LOCALAPPDATA "DR2Neutral\Backups"
$backupPath = Join-Path $backupRoot ("uninstall-" + [DateTime]::Now.ToString("yyyyMMdd-HHmmss-fff"))
$dllExisted = Test-Path -LiteralPath $destinationDll -PathType Leaf
$pdbExisted = Test-Path -LiteralPath $destinationPdb -PathType Leaf
$configurationChanged = $false
$binaryChanged = $false

try {
    $lockTaken = $mutex.WaitOne([TimeSpan]::FromSeconds(10))
    if (-not $lockTaken) {
        throw "Another DR2Neutral install or uninstall is already running."
    }

    New-Item -ItemType Directory -Path $backupPath -Force | Out-Null
    Stop-SimHubGracefully -Executable $simHubExecutable -WorkingDirectory $SimHubPath

    Copy-Item -LiteralPath $activationPath -Destination (Join-Path $backupPath "PluginsActivation.json")
    if ($dllExisted) {
        Copy-Item -LiteralPath $destinationDll -Destination (Join-Path $backupPath "DR2Neutral.dll")
    }
    if ($pdbExisted) {
        Copy-Item -LiteralPath $destinationPdb -Destination (Join-Path $backupPath "DR2Neutral.pdb")
    }

    $activation = @()
    foreach ($entry in (Read-Activation -Path $activationPath)) {
        if ($null -eq $entry -or [string]::IsNullOrWhiteSpace($entry.ClassName)) {
            throw "SimHub activation settings contain an invalid entry."
        }
        if ($entry.ClassName -ne $pluginClassName) {
            $activation += $entry
        }
    }
    Write-JsonAtomically -Path $activationPath -Value $activation
    $configurationChanged = $true

    foreach ($path in @($destinationDll, $destinationPdb)) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Remove-Item -LiteralPath $path -Force
        }
    }
    $binaryChanged = $true

    if ($simHubWasRunning) {
        Start-SimHubUnelevated -Executable $simHubExecutable -WorkingDirectory $SimHubPath
    }

    Write-Host "DR2Neutral uninstalled successfully." -ForegroundColor Green
    Write-Host "Calibration settings were preserved for a future reinstall."
    Write-Host "Recovery backup: $backupPath"
}
catch {
    $failure = $_
    Write-Warning "Uninstall failed; restoring the previous SimHub state."
    try {
        Stop-SimHubGracefully -Executable $simHubExecutable -WorkingDirectory $SimHubPath
        if ($configurationChanged) {
            Copy-Item -LiteralPath (Join-Path $backupPath "PluginsActivation.json") -Destination $activationPath -Force
        }
        if ($binaryChanged) {
            if ($dllExisted) {
                Copy-Item -LiteralPath (Join-Path $backupPath "DR2Neutral.dll") -Destination $destinationDll -Force
            }
            if ($pdbExisted) {
                Copy-Item -LiteralPath (Join-Path $backupPath "DR2Neutral.pdb") -Destination $destinationPdb -Force
            }
        }
        if ($simHubWasRunning) {
            Start-SimHubUnelevated -Executable $simHubExecutable -WorkingDirectory $SimHubPath
        }
    }
    catch {
        Write-Warning "Automatic rollback also failed: $($_.Exception.Message)"
    }
    throw $failure
}
finally {
    if ($lockTaken) {
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}
