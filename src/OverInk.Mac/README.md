# OverInk for macOS

Native Swift/AppKit implementation of OverInk.

## Current foundation

- Menu-bar application
- One transparent AppKit overlay per display
- Native `ignoresMouseEvents` draw/pass-through switching
- Shared multi-display annotation history with pressure-aware Core Graphics rendering
- Native `NSGlassEffectView` Liquid Glass toolbar on macOS 26+, with Lucide icons and compact menus
- Pen, line, arrow, rectangle, ellipse, whole-stroke eraser, pressure, color, width, and screen veil
- Global `Command-Shift-A` mode shortcut
- Screen Recording and Accessibility permission entry points
- ScreenCaptureKit clean-background capture with offscreen ink/veil composition
- Capture package with flattened PNG, `.orig.png`, and versioned editable `.overink.json`
- Native capture library with thumbnail grid, open, copy, reveal, and delete actions
- Native editor with pen, eraser, undo/redo, color, width, veil, save, and copy
- Platform-neutral Swift models for geometry, ink, and remote protocol v1

## Build and test

```bash
cd src/OverInk.Mac
swift build
swift test
swift run OverInkMac
```

`swift run` is useful for development, but it does not create a signed application bundle. Production packaging requires full Xcode, an app target with privacy usage descriptions and entitlements, a Developer ID certificate, and notarization.

The app starts in pass-through mode. Use the menu-bar pencil or `Command-Shift-A` to enter drawing mode. Press `Escape` at any time to leave drawing mode.

For Screen Recording permission testing, launch the ad-hoc signed development bundle from the repository root:

```bash
./scripts/run-mac-dev.sh
```

This creates `artifacts/dev/OverInk Dev.app` with a stable development bundle identifier and opens it.
