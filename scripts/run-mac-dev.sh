#!/bin/zsh
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
package="$root/src/OverInk.Mac"
app="$root/artifacts/dev/OverInk Dev.app"
contents="$app/Contents"

swift build --package-path "$package"
binary_dir="$(swift build --package-path "$package" --show-bin-path)"

rm -rf "$app"
mkdir -p "$contents/MacOS" "$contents/Resources"
cp "$binary_dir/OverInkMac" "$contents/MacOS/OverInkMac"
cp "$package/Supporting/Info.plist" "$contents/Info.plist"
cp "$package/Supporting/AppIcon.icns" "$contents/Resources/AppIcon.icns"
# The web client the iPad loads (shared with the Windows app). -L follows the symlink in the package.
cp -RL "$package/Sources/OverInkMac/RemoteWeb" "$contents/Resources/RemoteWeb"
# Sparkle is linked, so the framework has to be next to the executable (it never updates a dev build).
mkdir -p "$contents/Frameworks"
ditto "$binary_dir/Sparkle.framework" "$contents/Frameworks/Sparkle.framework"

plutil -replace CFBundleDisplayName -string "OverInk Dev" "$contents/Info.plist"
plutil -replace CFBundleName -string "OverInk Dev" "$contents/Info.plist"
plutil -replace CFBundleIdentifier -string "app.overink.mac.dev" "$contents/Info.plist"

source "$root/scripts/lib-mac-sign.sh"
sign_sparkle "$contents/Frameworks/Sparkle.framework" - 0

codesign \
  --force \
  --sign - \
  --identifier "app.overink.mac.dev" \
  --requirements '=designated => identifier "app.overink.mac.dev"' \
  --entitlements "$package/Supporting/OverInk.entitlements" \
  "$app"

# Extra arguments are passed to the app, for example `scripts/run-mac-dev.sh --remote`.
open "$app" --args "$@"
print "Launched $app"
