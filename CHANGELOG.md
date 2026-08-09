# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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

[Unreleased]: https://github.com/eugeny-dementev/simhub-dr2-neutral/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/eugeny-dementev/simhub-dr2-neutral/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/eugeny-dementev/simhub-dr2-neutral/releases/tag/v1.0.0
