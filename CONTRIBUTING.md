# Contributing

Thank you for improving DR2 Neutral Selector. Bug reports, compatibility data,
documentation fixes, tests, and focused pull requests are welcome.

Participation is governed by the [Code of Conduct](CODE_OF_CONDUCT.md). For
usage questions, read [SUPPORT.md](SUPPORT.md). Security vulnerabilities must
follow the private process in [SECURITY.md](SECURITY.md).

## Before opening a change

- Search existing issues and pull requests.
- Open an issue first for a new public action, breaking behavior, or substantial
  UI/architecture change.
- Keep the public `DR2Neutral.*` names backward compatible.
- Preserve all foreground, telemetry-freshness, timeout, pulse-count,
  cancellation, input-release, and operation-locking safeguards.
- Do not commit SimHub assemblies or other third-party binaries.

## Development workflow

Follow [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) to prepare the toolchain.
Create a branch from `main`, make one coherent change, and run:

```powershell
.\test.ps1
.\package.ps1
```

Compiler warnings are errors. Add or update tests for every observable behavior
change. Test real key injection only while stationary, with the clutch held,
and never treat an on-road manual test as a substitute for automated checks.

## Pull requests

The description should explain the problem, user-visible effect, safety impact,
and verification performed. Include a settings-page image for UI changes. Keep
unrelated formatting or refactoring out of the patch.

By submitting a contribution, you agree that it is licensed under the MIT
License in this repository.
