# OverInk

OverInk is a Windows screen annotation app: draw over your display with a floating toolbar, capture to an editor with a local library, copy or drag images out, and optionally sketch from an iPad over the LAN.

## Build

```bash
dotnet build src/OverInk/OverInk.csproj
dotnet run --project src/OverInk/OverInk.csproj
```

## Releases

Every push and pull request to `main` is built and tested by `.github/workflows/ci.yml`.

To publish a release, push a version tag:

```bash
git tag v0.2.0
git push origin v0.2.0
```

`.github/workflows/release.yml` then runs the tests, builds a self-contained win-x64 package and attaches these to a GitHub Release (with auto-generated notes):

- `OverInk-Setup-<version>.exe` – installer (Inno Setup, per-user, no admin needed)
- `OverInk-<version>-win-x64.zip` – portable build
- `SHA256SUMS.txt`

Tags containing a hyphen (for example `v0.3.0-beta.1`) are marked as pre-releases. You can also run the workflow manually from the Actions tab to build the artifacts without publishing.

To build the same artifacts locally:

```powershell
./scripts/publish.ps1 -Version 0.2.0      # needs Inno Setup for the installer: winget install JRSoftware.InnoSetup
```

The builds are not code-signed, so Windows SmartScreen shows an "unknown publisher" prompt on first run: click **More info**, then **Run anyway**.

## Library folder

Captures are saved under `Pictures\OverInk`. Settings live in `%AppData%\OverInk`.

## License

MIT (add license file if you publish formally)
