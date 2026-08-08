[CmdletBinding()]
param(
    [string]$SimHubPath = "${env:ProgramFiles(x86)}\SimHub",
    [switch]$Elevated
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version 3.0

$pluginClassName = "DR2NeutralPlugin.DR2Neutral"
$shutdownTimeoutSeconds = 15
$loadTimeoutSeconds = 30
$maximumJsonBytes = 4MB
$maximumLogBytes = 8MB

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

function Assert-BoundedFile {
    param([string]$Path, [long]$MaximumBytes, [string]$Description)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description was not found: $Path"
    }

    $length = (Get-Item -LiteralPath $Path).Length
    if ($length -le 0 -or $length -gt $MaximumBytes) {
        throw "$Description has an invalid size ($length bytes): $Path"
    }
}

function Get-PluginSourcePath {
    $releasePath = Join-Path $PSScriptRoot "DR2Neutral.dll"
    if (Test-Path -LiteralPath $releasePath -PathType Leaf) {
        return $releasePath
    }

    $developerPath = Join-Path $PSScriptRoot "src\bin\Release\DR2Neutral.dll"
    if (Test-Path -LiteralPath $developerPath -PathType Leaf) {
        return $developerPath
    }

    throw "DR2Neutral.dll is missing. Re-extract the release package or run build.ps1."
}

function Assert-PluginBinary {
    param([string]$Path)

    [byte[]]$bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 512) {
        throw "DR2Neutral.dll is unexpectedly small."
    }

    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ($peOffset -lt 0 -or $peOffset + 6 -gt $bytes.Length) {
        throw "DR2Neutral.dll has an invalid PE header."
    }

    $machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
    if ($machine -ne 0x014c) {
        throw "DR2Neutral.dll is not an x86 plug-in."
    }

    $metadata = [Text.Encoding]::UTF8.GetString($bytes)
    if (-not $metadata.Contains(".NETFramework,Version=v4.8")) {
        throw "DR2Neutral.dll does not target .NET Framework 4.8."
    }
}

function Read-JsonArray {
    param([string]$Path)

    Assert-BoundedFile -Path $Path -MaximumBytes $maximumJsonBytes -Description "SimHub activation settings"
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
        throw "SimHub did not close within $shutdownTimeoutSeconds seconds. Close it manually and run the installer again."
    }
}

function Start-SimHub {
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

function Get-LogBaseline {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [PSCustomObject]@{ Length = 0L; Prefix = "" }
    }

    $item = Get-Item -LiteralPath $Path
    return [PSCustomObject]@{
        Length = [long]$item.Length
        Prefix = Get-LogPrefix -Path $Path
    }
}

function Get-LogPrefix {
    param([string]$Path)

    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite)
    try {
        $count = [int][Math]::Min(512, $stream.Length)
        $bytes = [byte[]]::new($count)
        $read = $stream.Read($bytes, 0, $count)
        return [Convert]::ToBase64String($bytes, 0, $read)
    }
    finally {
        $stream.Dispose()
    }
}

function Read-NewLogText {
    param([string]$Path, [long]$Offset)

    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite)
    try {
        if ($Offset -lt 0 -or $Offset -gt $stream.Length) {
            $Offset = 0
        }
        $null = $stream.Seek($Offset, [IO.SeekOrigin]::Begin)
        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true, 4096, $true)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Wait-ForPluginLoad {
    param([string]$LogPath, [object]$InitialBaseline)

    $deadline = [DateTime]::UtcNow.AddSeconds($loadTimeoutSeconds)
    do {
        if (Test-Path -LiteralPath $LogPath -PathType Leaf) {
            $log = Get-Item -LiteralPath $LogPath
            if ($log.Length -gt 0 -and $log.Length -le $maximumLogBytes) {
                $sameFile = $log.Length -ge $InitialBaseline.Length -and
                    (Get-LogPrefix -Path $LogPath) -eq $InitialBaseline.Prefix
                $offset = if ($sameFile) { [long]$InitialBaseline.Length } else { 0L }
                $text = Read-NewLogText -Path $LogPath -Offset $offset
                if ($text -match '\[DR2Neutral\] Loaded v') {
                    return $true
                }
                if ($text -match '(?im)^.*(DR2NeutralPlugin|DR2Neutral\.dll).*(fail|error|exception).*$|^.*(fail|error|exception).*(DR2NeutralPlugin|DR2Neutral\.dll).*$') {
                    throw "SimHub reported a DR2Neutral loading error. See $LogPath."
                }
            }
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    return $false
}

$SimHubPath = [IO.Path]::GetFullPath($SimHubPath.TrimEnd('\'))
if (-not (Test-IsAdministrator) -and -not (Test-DirectoryWriteAccess -Path $SimHubPath)) {
    if ($Elevated) {
        throw "Administrator permission was requested but not granted."
    }
    Write-Host "Windows will ask permission to install the plug-in into SimHub."
    Restart-Elevated
}

$simHubExecutable = Join-Path $SimHubPath "SimHubWPF.exe"
$activationPath = Join-Path $SimHubPath "PluginsData\PluginsActivation.json"
$logDirectory = Join-Path $SimHubPath "Logs"
$logPath = Join-Path $logDirectory "SimHub.txt"
$sourceDll = Get-PluginSourcePath
$destinationDll = Join-Path $SimHubPath "DR2Neutral.dll"
$sourcePdb = Join-Path (Split-Path -Parent $sourceDll) "DR2Neutral.pdb"
$destinationPdb = Join-Path $SimHubPath "DR2Neutral.pdb"

Assert-BoundedFile -Path $simHubExecutable -MaximumBytes 200MB -Description "SimHub"
Assert-BoundedFile -Path $activationPath -MaximumBytes $maximumJsonBytes -Description "SimHub activation settings"
Assert-PluginBinary -Path $sourceDll

$mutex = [Threading.Mutex]::new($false, "Global\DR2NeutralInstaller")
$lockTaken = $false
$simHubWasRunning = [bool](Get-Process -Name "SimHubWPF", "SimHub" -ErrorAction SilentlyContinue)
$backupRoot = Join-Path $env:LOCALAPPDATA "DR2Neutral\Backups"
$backupPath = Join-Path $backupRoot ("install-" + [DateTime]::Now.ToString("yyyyMMdd-HHmmss-fff"))
$oldDllExisted = Test-Path -LiteralPath $destinationDll -PathType Leaf
$oldPdbExisted = Test-Path -LiteralPath $destinationPdb -PathType Leaf
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
    if ($oldDllExisted) {
        Copy-Item -LiteralPath $destinationDll -Destination (Join-Path $backupPath "DR2Neutral.dll")
    }
    if ($oldPdbExisted) {
        Copy-Item -LiteralPath $destinationPdb -Destination (Join-Path $backupPath "DR2Neutral.pdb")
    }

    $activation = @()
    foreach ($entry in (Read-JsonArray -Path $activationPath)) {
        if ($null -eq $entry -or [string]::IsNullOrWhiteSpace($entry.ClassName)) {
            throw "SimHub activation settings contain an invalid entry."
        }
        if ($entry.ClassName -ne $pluginClassName) {
            $activation += $entry
        }
    }
    $activation += [PSCustomObject]@{
        ClassName = $pluginClassName
        IsEnabled = $true
        ShowInMainMenu = $true
        ShowInMainMenuPosition = 0
    }
    Write-JsonAtomically -Path $activationPath -Value $activation
    $configurationChanged = $true

    $temporaryDll = "$destinationDll.dr2neutral.$PID.tmp"
    Copy-Item -LiteralPath $sourceDll -Destination $temporaryDll -Force
    Move-Item -LiteralPath $temporaryDll -Destination $destinationDll -Force
    if ((Get-FileHash -LiteralPath $sourceDll -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $destinationDll -Algorithm SHA256).Hash) {
        throw "Installed DLL hash does not match the release DLL."
    }
    $binaryChanged = $true

    if (Test-Path -LiteralPath $sourcePdb -PathType Leaf) {
        Copy-Item -LiteralPath $sourcePdb -Destination $destinationPdb -Force
    }

    $initialLogBaseline = Get-LogBaseline -Path $logPath
    Start-SimHub -Executable $simHubExecutable -WorkingDirectory $SimHubPath
    if (-not (Wait-ForPluginLoad -LogPath $logPath -InitialBaseline $initialLogBaseline)) {
        throw "SimHub did not confirm DR2Neutral startup within $loadTimeoutSeconds seconds."
    }

    Write-Host "DR2Neutral installed, enabled, and loaded successfully." -ForegroundColor Green
    Write-Host "Open 'DR2 Neutral' in SimHub and click Start calibration."
    Write-Host "Recovery backup: $backupPath"
}
catch {
    $failure = $_
    Write-Warning "Installation failed; restoring the previous SimHub state."

    try {
        Stop-SimHubGracefully -Executable $simHubExecutable -WorkingDirectory $SimHubPath
        if ($configurationChanged) {
            Copy-Item -LiteralPath (Join-Path $backupPath "PluginsActivation.json") -Destination $activationPath -Force
        }
        if ($binaryChanged) {
            if ($oldDllExisted) {
                Copy-Item -LiteralPath (Join-Path $backupPath "DR2Neutral.dll") -Destination $destinationDll -Force
            }
            elseif (Test-Path -LiteralPath $destinationDll -PathType Leaf) {
                Remove-Item -LiteralPath $destinationDll -Force
            }
            if ($oldPdbExisted) {
                Copy-Item -LiteralPath (Join-Path $backupPath "DR2Neutral.pdb") -Destination $destinationPdb -Force
            }
            elseif (Test-Path -LiteralPath $destinationPdb -PathType Leaf) {
                Remove-Item -LiteralPath $destinationPdb -Force
            }
        }
        if ($simHubWasRunning) {
            Start-SimHub -Executable $simHubExecutable -WorkingDirectory $SimHubPath
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
