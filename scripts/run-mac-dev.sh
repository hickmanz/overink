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

plutil -replace CFBundleDisplayName -string "OverInk Dev" "$contents/Info.plist"
plutil -replace CFBundleName -string "OverInk Dev" "$contents/Info.plist"
plutil -replace CFBundleIdentifier -string "app.overink.mac.dev" "$contents/Info.plist"
plutil -insert CFBundleExecutable -string "OverInkMac" "$contents/Info.plist"

codesign \
  --force \
  --deep \
  --sign - \
  --identifier "app.overink.mac.dev" \
  --requirements '=designated => identifier "app.overink.mac.dev"' \
  --entitlements "$package/Supporting/OverInk.entitlements" \
  "$app"

open "$app"
print "Launched $app"
