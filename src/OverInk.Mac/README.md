# OverInk for macOS

Native Swift/AppKit implementation of OverInk.

## Current foundation

- Menu-bar application
- One transparent AppKit overlay per display
- Native `ignoresMouseEvents` draw/pass-through switching
- Shared multi-display annotation history with pressure-aware Core Graphics rendering
- Native `NSGlassEffectView` Liquid Glass toolbar on macOS 26+, with Lucide icons and compact menus
- Pen, line, arrow, rectangle, ellipse, whole-stroke eraser, pressure, color, width, and screen veil
- Draw-mode shortcuts (P/E/L/A/R/O, Command-Z, Shift-Command-Z, Escape), right-click tool menu, Shift to constrain shapes, freehand smoothing, and a pen-size cursor
- Toolbar: draggable grip (double-click flips vertical/horizontal), compact or expanded tools, color swatches with saved custom colors and a color wheel, width and veil sliders, off-screen recovery, and optional auto-hide
- Global draw toggle (default `Control-Shift-D`, matching Windows) registered with Carbon hotkeys, so no Accessibility permission is needed
- Screen Recording permission entry point
- ScreenCaptureKit clean-background capture with offscreen ink/veil composition
- Capture package with flattened PNG, `.orig.png`, and versioned editable `.overink.json`
- Editor and library in one window:
  - Tools: pen, eraser, line, arrow, rectangle, ellipse (Shift constrains), and a Select tool (`V`) with marquee, Shift-click, move, resize handles, recolor, delete, arrow-key nudge, and grouped strokes
  - Zoom: pinch or Command-scroll, `⌘0` fit, `⌘1` actual size, `⌘+`/`⌘-`, brush-size cursor that follows zoom
  - `⌘S` save, `⇧⌘S` save a copy as PNG, `⌘C` copy; edits also autosave in the background when the window loses focus, closes, or the app quits, and an unchanged original is never re-encoded
  - Recent-captures strip under the canvas and a searchable grid (`⌘F`) with rename, duplicate, copy, reveal, trash, drag to reorder (stored in `.overink-order.json`), drag out to other apps, and drop PNGs in to import them
  - The library folder is watched, so captures added or removed in Finder appear immediately. A plain PNG becomes editable on its first save (its pixels are kept as the `.orig.png`)
  - Library folder can be changed from the menu-bar menu (default `~/Pictures/OverInk`)
- Capture modes: display under the cursor or all displays stitched together, drag-to-select regions that can span displays, and window snap (hover a window and click)
- Optional global capture hotkey (unbound by default), include-toolbar and open-editor-after-capture options, native-resolution Retina captures
- Settings window (menu bar → Settings…): hotkey recorders for drawing and capture, capture and toolbar options, capture folder, open at login, and Screen Recording status
- First-run welcome that walks through the Screen Recording permission and the draw hotkey
- Open at login through `SMAppService` (works from the installed app)
- Single instance: launching a second copy shows the first one and quits
- `overink://` links (need the bundled app): `overink://editor[?capture=<name or path in the library>]`, `overink://library`, `overink://settings`, `overink://draw`. Capture paths are only honored inside the library folder
- Persistent drawing, toolbar position, and capture behavior settings
- iPad remote (menu bar → iPad Remote…), compatible with the Windows host and serving the same web client from `shared/remote-web`:
  - One port (default 8787) serves the web client, a WebSocket at `/ws`, `/health` and the `/lib` library routes. It is built on Network.framework with a small HTTP parser and WebSocket codec in `OverInkCore` (both unit-tested), and it only accepts private-network peers
  - Pairing with a QR code (one-time code valid for 10 minutes), a chooser for the network interface the code advertises, a paired-devices list with "Forget", a shareable guest link with optional approval, and the Local Network permission. Device keys are stored as SHA-256 hashes, in the Keychain for signed builds and in a private file for development builds. Connections from this Mac's own loopback address are trusted without being remembered
  - Live screen streaming with ScreenCaptureKit (one `SCStream` per display piece, composed and JPEG-encoded, only while someone is watching), with presets, a rectangle or display or all-displays target, and the "follow the toolbar" display
  - Remote ink on the overlay: live strokes, shapes, partial erase, per-author undo/redo, clear, a roster and hover cursors, with ink revisions stamped on each frame so the iPad can drop its local copy at the right moment
  - Remote library: browse, thumbnails, the original image, ink read and write (409 on a stale revision), duplicate, delete, capture from the iPad, and change notifications when files change on the Mac
  - `shared/protocol/remote-v1.schema.json` describes every message; `RemoteSchemaConformanceTests` checks the host's output, the decoder and the shipped web client against it
- Updates with Sparkle 2: Check for Updates… in the menu, Check now and a Beta updates switch in Settings, quiet checks at launch and daily, and "Not now" stops the automatic prompt after three times for a release (installed, signed builds only)
- Platform-neutral Swift models for geometry, ink, and remote protocol v1

Capture documents are `CaptureDocument` version 2 (adds stroke `groupId`); version 1 files still open. The Mac format is independent of the Windows ISF-based library.

## Development launch arguments

`OverInkMac --library`, `--settings`, `--welcome` and `--remote-window` open those windows on launch, and `OverInkMac --open-editor <capture.png>` opens a capture in the editor. `--remote` runs the iPad remote server for this launch even when it is off in settings. `--snapshot-remote-window <png>` and `--snapshot-settings <png>` write that window to a file and quit (for layout checks without a screen). All of these skip the single-instance check.

`scripts/run-mac-dev.sh` passes its arguments to the app, for example `./scripts/run-mac-dev.sh --remote`.

## Testing the iPad remote

```bash
./scripts/run-mac-dev.sh --remote        # the dev bundle keeps its Screen Recording permission
node scripts/remote-smoke.mjs             # protocol, ink, library and hygiene checks (Node 22+)
```

The smoke test connects from the same Mac, so it is auto-trusted. It draws on the real overlay and makes (then deletes) its own capture, which needs an awake screen; on a locked or sleeping display the library part is skipped. A real iPad needs the Mac's firewall and Local Network prompts answered once.

## Build and test

```bash
cd src/OverInk.Mac
swift build
swift test
swift run OverInkMac
```

`swift run` is useful for development, but it does not create an application bundle (so no updater, login item or `overink://` links). SwiftPM downloads Sparkle on first build; if `swift package resolve` hangs on a Keychain prompt, add `--disable-keychain`.

To make a distributable app and DMG, run `scripts/package-mac.sh --version 0.3.0 [--universal]` from the repository root. Without credentials it is ad-hoc signed; for a Developer ID signed and notarized build see the macOS port section in the top-level `README.md`. `swift scripts/make-mac-icon.swift` regenerates `Supporting/AppIcon.icns` from the Windows artwork.

The app starts in pass-through mode. Use the menu-bar pencil or `Control-Shift-D` to enter drawing mode. Press `Escape` at any time to leave drawing mode.

For Screen Recording permission testing, launch the ad-hoc signed development bundle from the repository root:

```bash
./scripts/run-mac-dev.sh
```

This creates `artifacts/dev/OverInk Dev.app` with a stable development bundle identifier and opens it.
