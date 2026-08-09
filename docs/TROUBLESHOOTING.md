# Troubleshooting

## Controller is not found

Verify Windows sees it in **Set up USB game controllers**, close software that
opens the device exclusively, reconnect it, and retry calibration. Calibration
supports up to 32 DirectInput devices and 128 buttons per device.

## One movement shifts twice

Remove the remaining direct in-game binding and any SimHub Controls or Keyboard
Emulator mapping for the same button. Keep the plugin's built-in controller
reader enabled unless an advanced SimHub action mapping replaces it.

## A key is ignored

Keep DiRT Rally 2.0 focused. Run SimHub and the game at the same Windows
privilege level. Confirm the F-key configured on the plugin page matches the
game's Keyboard binding.

## Neutral stops before N

The game rejected a shift, focus changed, telemetry became stale, or telemetry
did not confirm a gear change within one second. Stop the car, hold the clutch,
and retry. The plugin deliberately does not send an unverified key burst.

The plugin does not have a vehicle-speed, throttle, or brake lockout. When the
car is rolling backward, DiRT Rally 2.0 can reject a sequential downshift even
though the same key works once stationary. A structured trace distinguishes
that case: `neutral.conditions_checked` has `"allowed":true`,
`pulse.key_down_sent` and `pulse.key_up_sent` appear, and then
`gear_change.timeout` reports that telemetry never changed.

## Collect a structured event trace

Reproduce the problem once, exit the game when convenient, then run:

```powershell
Select-String -Path "${env:ProgramFiles(x86)}\SimHub\Logs\SimHub.txt" `
  -SimpleMatch "[DR2Neutral.Event]" |
  Select-Object -Last 150
```

Each event contains a precise ISO-8601 UTC timestamp. Follow one `operationId`
from `input.button_pressed` through `neutral.conditions_checked`,
`pulse.key_down_sent`, `pulse.key_up_sent`, and either
`gear_change.confirmed` or `gear_change.timeout`. Rejections include a stable
`reason` code. The logger intentionally omits the repeated 10 ms poll samples.

Attach only the relevant lines to a bug report after removing usernames or
unrelated paths. The JSON fields are bounded to keep traces manageable.

## Settings page is missing

Rerun `Install.cmd`; it is idempotent and creates a new backup. If the problem
continues, inspect `SimHub\Logs\SimHub.txt` for `[DR2Neutral]`, then open a bug
report with the relevant lines, SimHub version, Windows version, and plugin
version. Remove usernames and unrelated personal paths from logs first.

## Installation cannot close SimHub

Save any SimHub work and close it manually, then rerun the installer. The
installer intentionally refuses to force-kill SimHub because that could corrupt
unrelated settings.
