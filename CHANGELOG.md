# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.2.0] - 2026-08-10

- Added a 50–200 ms Neutral pulse interval setting to the SimHub-native plugin
  page, with a 5 ms slider and exact whole-millisecond manual entry.
- Kept 200 ms as the compatibility default and transparently migrated version-1
  settings without changing keyboard bindings or controller calibration.
- Captured timing per Neutral operation and added the configured value to public
  diagnostics and correlated structured event logs.

## [1.1.1] - 2026-08-09

- Enforced a 200 ms minimum between Neutral key-down pulses so DiRT Rally 2.0
  does not discard rapid follow-up downshifts after unusually fast telemetry
  confirmation.
- Added correlated inter-pulse wait diagnostics and deterministic interval
  calculation tests based on captured runtime failure timing.

## [1.1.0] - 2026-08-09

- Added bounded JSON event logging with UTC timestamps and correlation IDs for
  controller edges, condition decisions, key pulses, telemetry confirmations,
  rejections, cancellations, failures, and successful Neutral completion.
- Added precise key-down/key-up and pulse timing while keeping logging outside
  the 10 ms telemetry polling loop.
- Isolated structured logging failures from controller handling and key-release
  safety.

## [1.0.0] - 2026-08-09

- Added a native SimHub settings page with guided DirectInput calibration.
- Added built-in pull, push, and reverse-trigger input handling for any
  compatible sequential shifter.
- Added telemetry-confirmed Neutral selection for DiRT Rally 2.0.
- Added configurable F1-F12 Gear Up and Gear Down keys, defaulting to F7/F8.
- Added foreground, stale-telemetry, timeout, pulse-count, cancellation, and
  operation-locking safeguards.
- Added rollback-capable one-click installation and recoverable uninstall.
- Added a reproducible .NET SDK 10 / .NET Framework 4.8 x86 build and tests.

[Unreleased]: https://github.com/eugeny-dementev/simhub-dr2-neutral/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/eugeny-dementev/simhub-dr2-neutral/compare/v1.1.1...v1.2.0
[1.1.1]: https://github.com/eugeny-dementev/simhub-dr2-neutral/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/eugeny-dementev/simhub-dr2-neutral/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/eugeny-dementev/simhub-dr2-neutral/releases/tag/v1.0.0
