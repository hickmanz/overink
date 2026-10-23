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
- Platform-neutral Swift models for geometry, ink, and remote protocol v1

Capture documents are `CaptureDocument` version 2 (adds stroke `groupId`); version 1 files still open. The Mac format is independent of the Windows ISF-based library.

## Development launch arguments

`OverInkMac --library`, `--settings`, and `--welcome` open those windows on launch, and `OverInkMac --open-editor <capture.png>` opens a capture in the editor. These also skip the single-instance check.

## Build and test

```bash
cd src/OverInk.Mac
swift build
swift test
swift run OverInkMac
```

`swift run` is useful for development, but it does not create a signed application bundle. Production packaging requires full Xcode, an app target with privacy usage descriptions and entitlements, a Developer ID certificate, and notarization.

The app starts in pass-through mode. Use the menu-bar pencil or `Control-Shift-D` to enter drawing mode. Press `Escape` at any time to leave drawing mode.

For Screen Recording permission testing, launch the ad-hoc signed development bundle from the repository root:

```bash
./scripts/run-mac-dev.sh
```

This creates `artifacts/dev/OverInk Dev.app` with a stable development bundle identifier and opens it.
