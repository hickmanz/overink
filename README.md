# OverInk

OverInk is a screen annotation app: draw over your display with a floating toolbar, capture to an editor with a local library, copy or drag images out, and optionally sketch from an iPad over the LAN.

## Windows build

```bash
dotnet build src/OverInk/OverInk.csproj
dotnet run --project src/OverInk/OverInk.csproj
```

## macOS build

The native Swift/AppKit macOS port is under `src/OverInk.Mac`. Its initial foundation includes a menu-bar app, per-display native overlays, draw/pass-through switching, Core Graphics ink, and ScreenCaptureKit capture support.

```bash
cd src/OverInk.Mac
swift build
swift test
swift run OverInkMac
```

For a development `.app` with a stable identity for Screen Recording permissions:

```bash
./scripts/run-mac-dev.sh
```

Release packaging (Developer ID signing, notarization, DMG and Sparkle updates) is in `scripts/package-mac.sh`. See the macOS port section below and `src/OverInk.Mac/README.md`.

### macOS port: status and next steps

Work is tracked on the `mac-app` branch, in phases toward feature parity with Windows.

| Phase | Scope | Status |
| --- | --- | --- |
| 0 | Foundation: logic in `OverInkCore` with tests, stroke smoothing, Carbon hotkeys | Done |
| 1 | Overlay and toolbar: draw shortcuts, right-click menu, Shift constraints, custom colors | Done |
| 2 | Capture: window snap, auto / full-screen modes, regions across displays | Done |
| 3 | Editor and library: shapes, Select tool, zoom, grid/strip, rename, reorder, autosave | Done |
| 4 | Settings window, launch at login, onboarding, single instance, `overink://` URLs | Done |
| 5 | Remote (iPad) sketching | Done; needs a first test on a real iPad |
| 6 | Distribution: signing, notarization, DMG, Sparkle updates | Built; needs your Apple and Sparkle credentials to run for real |

**Phase 5 (remote): what is done**

- The iPad web client lives in `shared/remote-web/`, so Windows and Mac serve the same files. Windows embeds it through `OverInk.csproj` (`LogicalName` keeps the old resource names); the Mac copies it into `Contents/Resources/RemoteWeb` (the dev script does this) and falls back to the SwiftPM resource for `swift run`.
- Pure code in `OverInkCore`, all unit-tested: wire protocol and binary frames, HTTP request parser, WebSocket handshake and codec (RFC 6455 vectors), network and adapter ranking, pairing, library conversion, stream planning, remote ink mapping, and partial erase.
- `OverInkMac`: `RemoteServer` (Network.framework), `RemoteHost` (auth, messages, ink, state, roster), `FrameStreamer` (ScreenCaptureKit and JPEG), `RemoteLibraryService` (`/lib`), `CaptureStorage` (shared with the editor), `RemoteWindowController` (pairing, devices, streaming and access settings), and a Keychain-or-file paired-device store.
- `shared/protocol/remote-v1.schema.json` covers every message, and `RemoteSchemaConformanceTests` validates what the host sends, what it accepts, and what the shipped `app.js` sends and handles.
- `scripts/remote-smoke.mjs` exercises the running host over a real WebSocket and HTTP: handshake, strokes, shapes, erase, per-author undo, state, ping, hover, clear, and the whole library API including the 409 conflict. It passes against the dev bundle.
- Checked in a real browser: the shared web client loads from the Mac, connects and shows the streamed screen.

**Phase 5: still to verify by hand**

1. **A real iPad.** On the first run macOS shows the firewall prompt ("accept incoming connections") and the Local Network prompt. Until both are answered, connections from other devices hang. Loopback works without them, which is why the smoke test passes regardless.
2. **The Windows build.** The `OverInk.csproj` change that embeds `shared/remote-web` has not been built, because there is no .NET SDK on the Mac. Build and run the Windows app once, and check that the iPad page still loads.
3. **Multiple displays.** Streaming across several displays (the piece placement, `sourceRect` and the gap color) has only been exercised on one display.
4. **Region and "follow the toolbar"** while the toolbar is dragged to another display.
5. **Keychain.** Paired devices use the Keychain only when the app is signed with a Team ID, which a development build is not (they use `paired-devices.json` instead). Check this on a signed build in phase 6.

Known gaps: ink that crosses displays is stored on the display where the stroke starts (not split); a remote save to a capture that is open in the Mac editor is picked up only when the editor reloads it.

**Phase 6 (distribution): what is done**

- `scripts/package-mac.sh --version X [--universal]` builds `OverInk.app` (version, build number, feed URL and update key stamped into `Info.plist`, the icon, the iPad web client and `Sparkle.framework` inside), signs it inside-out with the hardened runtime, notarizes and staples the app, then makes, signs, notarizes and staples `OverInk-X-mac.dmg` and `SHA256SUMS-mac.txt`. With no credentials it makes an ad-hoc app and DMG that run on the same Mac, which is how CI checks the bundle builds.
- Sparkle 2 (Swift package dependency, only in the app target). `UpdateController` starts it only when the bundle has an HTTPS `SUFeedURL` and a `SUPublicEDKey` and is not the `.dev` bundle. It checks quietly 3 seconds after launch and daily, has a **Check for Updates…** menu item and a **Check now** button in Settings, a **Beta updates** switch (beta items carry `<sparkle:channel>beta</sparkle:channel>`), and stops showing the automatic prompt after "Not now" has been chosen 3 times for the same release (a newer release, or Check now, starts over). The counting rules live in `OverInkCore` (`UpdateSettings`) with tests, and match `UpdateReminder` on Windows. Sparkle's own button reads "Remind Me Later" rather than "Not now".
- The feed is `appcast.xml` on a rolling release named `mac-appcast` (`https://github.com/hickmanz/overink/releases/download/mac-appcast/appcast.xml`). `scripts/mac-appcast.sh` signs the DMG with Sparkle's `sign_update` and `scripts/mac_release.py` adds the item (newest first, last 40 kept). Sparkle compares dotted numbers, so the build number is `X.Y.Z.<n>`: `<n>` is the beta number for `-beta.N` tags and 10000 for a stable release, so `0.3.0-beta.2` < `0.3.0-beta.10` < `0.3.0`. The script's tests are in `scripts/test_mac_release.py`.
- `.github/workflows/release.yml` now has three jobs: `windows` (as before, minus publishing), `mac` (tests, signing, notarization, appcast), and `publish` (one GitHub release with the Windows and Mac files, then the appcast upload). If the Apple secrets are missing, the Mac build is still made as a workflow artifact but is not attached to the release, so Windows releases keep working. CI also runs the script tests and an ad-hoc `package-mac.sh --universal` on every push.

**Phase 6: what you need to do once (none of this can be done from the repo)**

1. **Developer ID certificate.** In your Apple Developer account create a "Developer ID Application" certificate, export it from Keychain Access as a `.p12`, then run `base64 -i cert.p12 | pbcopy`.
2. **Repository secrets** (Settings → Secrets and variables → Actions):
   - `MACOS_CERTIFICATE_P12` (the base64 text) and `MACOS_CERTIFICATE_PASSWORD`
   - `MACOS_SIGN_IDENTITY`, for example `Developer ID Application: Your Name (TEAMID)` (see it with `security find-identity -v -p codesigning`)
   - `NOTARY_APPLE_ID`, `NOTARY_TEAM_ID`, and `NOTARY_PASSWORD` (an app-specific password from appleid.apple.com)
3. **Update signing key.** Run `src/OverInk.Mac/.build/artifacts/sparkle/Sparkle/bin/generate_keys` (after one `swift build`). It stores the private key in your login Keychain and prints the public key. Add the public key as the repository **variable** `SPARKLE_PUBLIC_ED_KEY`. Export the private key with `generate_keys -x sparkle-private.key`, add its contents as the secret `SPARKLE_ED_PRIVATE_KEY`, and keep a copy somewhere safe: if you lose it, installed apps can never be updated.
4. **Try it** with a beta tag (`git tag v0.3.0-beta.1 && git push origin v0.3.0-beta.1`), install the DMG, then tag `v0.3.0-beta.2` and use Check now with Beta updates on. Also run `spctl --assess --type execute --verbose OverInk.app` on the installed app: it should say "Notarized Developer ID".
5. To build a DMG locally: `scripts/package-mac.sh --version 0.3.0 --universal` (set `MACOS_SIGN_IDENTITY`, `NOTARY_PROFILE` and `SPARKLE_PUBLIC_ED_KEY` for a real one).

Not verified (needs the credentials above): hardened-runtime signing, notarization and stapling, the Keychain-backed paired-device store on a Team-signed build, and an actual Sparkle update from one release to the next. The ad-hoc package was checked: it launches, serves the iPad page, and its appcast item and signature verify with Sparkle's tool.

Notes for whoever continues:

- Mac stream defaults are 10 fps, JPEG quality 80, long edge 2560. Windows uses native size at quality 95, which is too heavy for Retina displays.
- `swift test` can fail once with a compiler crash after large edits, or with "plugin for module 'TestingMacros' not found". Re-run it; it passes on retry.
- Never read Keychain items from the main thread in an unsigned build: every rebuild changes the app's identity and macOS stops the app with a password prompt. `KeychainPairedDeviceStore` only uses the Keychain for Team-signed builds.
- ScreenCaptureKit reports no displays while the display is asleep or the Mac is locked, so streaming and capture cannot be tested then (`caffeinate -u -t 10` wakes the display).

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

When the macOS signing secrets are set, the same release also gets `OverInk-<version>-mac.dmg` and `SHA256SUMS-mac.txt`, and the Sparkle appcast is refreshed (see the macOS port section for the one-time setup).

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
