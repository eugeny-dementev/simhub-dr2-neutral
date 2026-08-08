# Installation and recovery

## Download and verify

Download these assets from the [latest GitHub release](https://github.com/eugeny-dementev/simhub-dr2-neutral/releases/latest):

- `DR2Neutral-<version>-SimHub.zip`
- `SHA256SUMS.txt`

Verify the archive before extracting it:

```powershell
Get-FileHash .\DR2Neutral-1.0.0-SimHub.zip -Algorithm SHA256
Get-Content .\SHA256SUMS.txt
```

The hashes must match. Releases also have GitHub artifact provenance:

```powershell
gh attestation verify .\DR2Neutral-1.0.0-SimHub.zip `
  --repo eugeny-dementev/simhub-dr2-neutral
```

## Install or upgrade

1. Extract the complete ZIP. Do not run the installer from inside the archive.
2. Close DiRT Rally 2.0.
3. Double-click `Install.cmd`.
4. Approve elevation only if Windows requests write access to the SimHub folder.
5. Wait for the success message. SimHub is restarted only if it was running.
6. Open **DR2 Neutral** in SimHub and calibrate the three controls.

Running a newer installer over an existing version is supported. Calibration is
stored in SimHub settings and is preserved across upgrades.

## What the installer changes

The installer changes only:

- `DR2Neutral.dll` and its optional PDB in the SimHub directory.
- The exact `DR2NeutralPlugin.DR2Neutral` entry in
  `PluginsData\PluginsActivation.json`.

It does not add SimHub Controls mappings or Keyboard Emulator entries. Before
every mutation it stops SimHub gracefully, validates bounded inputs, and creates
a timestamped backup under `%LOCALAPPDATA%\DR2Neutral\Backups`. Activation JSON
is replaced atomically. If plugin loading is not confirmed in the new SimHub log
segment, installation rolls back automatically.

## Uninstall

Double-click `Uninstall.cmd`. The uninstaller removes only the plugin binary,
optional symbols, and exact activation record. Calibration settings are kept for
a future reinstall, and recoverable copies are stored in the backup directory.

## Manual recovery

Normally the installer restores state automatically. If Windows or SimHub stops
the rollback, close SimHub and open the newest folder under:

```text
%LOCALAPPDATA%\DR2Neutral\Backups
```

Copy `DR2Neutral.dll`, `DR2Neutral.pdb` when present, and
`PluginsActivation.json` back to the matching SimHub locations. Keep a copy of
the failed state until SimHub starts successfully.
