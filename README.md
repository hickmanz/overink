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

Creating a production-signed `.app` still requires full Xcode. See `src/OverInk.Mac/README.md` for current status.

### macOS port: status and next steps

Work is tracked on the `mac-app` branch, in phases toward feature parity with Windows.

| Phase | Scope | Status |
| --- | --- | --- |
| 0 | Foundation: logic in `OverInkCore` with tests, stroke smoothing, Carbon hotkeys | Done |
| 1 | Overlay and toolbar: draw shortcuts, right-click menu, Shift constraints, custom colors | Done |
| 2 | Capture: window snap, auto / full-screen modes, regions across displays | Done |
| 3 | Editor and library: shapes, Select tool, zoom, grid/strip, rename, reorder, autosave | Done |
| 4 | Settings window, launch at login, onboarding, single instance, `overink://` URLs | Done |
| 5 | Remote (iPad) sketching | **In progress** |
| 6 | Distribution: signing, notarization, DMG, Sparkle updates | Not started |

**Phase 5 (remote): what is done**

- The iPad web client moved from `src/OverInk/Remote/wwwroot` to `shared/remote-web/`, so Windows and Mac serve the same files.
  - Windows embeds it through `OverInk.csproj` (`LogicalName` keeps the old resource names). **This csproj change has not been built yet**, because there is no .NET SDK on the Mac. Build it on Windows first.
  - Mac reaches it through the symlink `src/OverInk.Mac/Sources/OverInkMac/RemoteWeb`, declared as a SwiftPM resource.
- New pure code in `OverInkCore` (compiles; **no tests written yet**):
  - `RemoteProtocol.swift`: every wire message, client-message decoding, and the 17-byte binary frame encoder/decoder. Stroke ids are lowercase GUID strings, as the web client compares them as strings.
  - `RemoteNetwork.swift`: private-address check, LAN adapter ranking and choice, and the pairing URL (`http://<ip>:<port>/?join=…&pair=…`).
  - `RemotePairing.swift`: 10-minute single-use pair tokens, session join token, device keys stored as SHA-256 hashes, participant colors.
  - `RemoteLibrary.swift`: `/lib` DTOs, conversion between canvas points and image pixels, validation, revisions, and per-connection library tokens.
  - `RemoteStreaming.swift`: `RemoteSettings` (now `MacAppSettings.remote`), stream target and region resolver, `StreamPlan` (cuts the stream rectangle across displays), presets, estimator, and `RevisionTimeline`.

**Phase 5: next steps, in order**

1. **Unit tests** for everything above. Port the cases from `tests/OverInk.Tests` (`StreamTargetResolverTests`, `RemoteNetworkTests`, `RemoteLibraryTests`, `LanAdapterAndFirewallTests`). Add round-trip tests for frame encoding and for decoding each client message.
2. **Server** (`OverInkMac`): an `NWListener` on the configured port that speaks HTTP and upgrades `/ws` to WebSocket.
   - Serve `RemoteWeb` with no-store headers, and `/health`.
   - Reject peers that fail `RemoteNetwork.isAllowedRemote`.
   - Authenticate the first `hello` in this order: pair token, device key, join token, loopback auto-pair. Honor `maxClients` and the optional join approval.
   - Keep a per-client send queue: a frame slot of capacity 1 that drops the oldest frame, plus an unbounded control queue.
3. **`/lib` routes**: list, thumb, original, GET/POST strokes (409 on a stale `baseRev`), duplicate, delete. Guard them with `LibraryTokenRegistry`.
   - Extract `SaveJob` and `load` from `EditorWindowController` into a shared `CaptureStorage` so remote saves use the same code path.
   - Send `libraryChanged` and `libraryItemChanged` from `DirectoryWatcher`.
   - Handle the `capture` message with `CaptureCoordinator.capture(.region(streamRect))`.
4. **Pairing UI**: a window with a CoreImage QR code, adapter picker (`getifaddrs` plus `SCDynamicStore` for the default route), paired-device list with revoke, and the Local Network permission. Store paired devices in the Keychain behind `PairedDeviceStore`.
5. **Streaming**: one `SCStream` per display piece of `StreamPlan`, composed at `maxFps`, JPEG-encoded through ImageIO.
   - Skip unchanged frames with a hash, send a keepalive every 2 seconds, and retry at quality − 20 (minimum 40) when a JPEG is over 1 MB.
   - Fill gaps between displays with `StreamPlan.gapColorHex`.
   - Stamp each frame with `RevisionTimeline.revision(forFrameAt:latency:)` (about 50 ms).
   - Leave the overlay windows in the capture, because the Windows host streams the screen with ink drawn in.
6. **Remote ink**: a controller that turns normalized coordinates into a display and local point, and drives `AnnotationSession`.
   - `AnnotationSession` needs author-aware append, undo, redo, remove and replace (`EditHistory` already supports authors).
   - Add a partial-erase function in Core: points within `size/2 + stroke width/2` of the eraser are removed, and runs of at least 2 points and `0.75 × width` stay as new strokes.
   - Broadcast `strokesAdded`, `strokesRemoved`, `cleared`, `ack` (with ink revision), `roster`, and the `peer*` messages.
   - A stroke belongs to the display where it starts; strokes that cross displays are not split.
7. **Settings UI** for the remote section (enable, port, adapter, target, region, presets, estimator text), then a **schema and conformance pass**: extend `shared/protocol/remote-v1.schema.json` to cover every message and validate samples from both hosts against it.
8. **Manual test** with the real web client in a browser against `swift run OverInkMac` (loopback auto-pair works), then on an iPad.

**Phase 6 (distribution)**: Developer ID signing and notarization, a DMG, a macOS job in `.github/workflows/release.yml`, and Sparkle 2 with a beta channel, a "Not now" limit of 3 and a "Check now" button. This needs full Xcode and signing credentials.

Notes for whoever continues:

- Mac stream defaults are 10 fps, JPEG quality 80, long edge 2560. Windows uses native size at quality 95, which is too heavy for Retina displays.
- `swift test` can fail once with a compiler crash after large edits. Re-run it; it passes on retry.

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
