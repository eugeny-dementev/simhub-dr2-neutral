# Releasing

Maintainers publish releases from immutable `vMAJOR.MINOR.PATCH` tags.

1. Update `version.json`, `PluginVersion`, and all three assembly version
   attributes.
2. Move entries from **Unreleased** into a dated `CHANGELOG.md` section and add
   comparison links.
3. Run `scripts\Test-Version.ps1`, `test.ps1`, and `package.ps1` locally.
4. Merge the release commit to `main` and confirm CI is green.
5. Create and push a signed or annotated tag matching `version.json`:

   ```powershell
   git tag -a v1.0.0 -m 'DR2 Neutral Selector 1.0.0'
   git push origin v1.0.0
   ```

The release workflow rebuilds from the tag against the pinned SimHub SDK input,
runs all tests, packages the installer, creates an artifact provenance
attestation, and creates the GitHub release with the ZIP and checksum manifest.
If any step fails, no release is published.

After publication, install the release ZIP on a clean SimHub setup and perform
the stationary acceptance test. Do not replace files on an existing tag; make a
new patch release so users retain a verifiable history.
