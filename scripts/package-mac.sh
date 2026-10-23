#!/bin/zsh
# Builds OverInk.app and a DMG for macOS.
#
#   scripts/package-mac.sh --version 0.3.0 [--universal] [--skip-notarize] [--output artifacts/mac]
#
# With no signing identity it makes an ad-hoc signed app and DMG that run on this Mac (a quick way to check
# the bundle layout). For a release, set:
#
#   MACOS_SIGN_IDENTITY      "Developer ID Application: Name (TEAMID)"
#   one way to notarize:
#     NOTARY_PROFILE         a profile saved with `xcrun notarytool store-credentials`
#     NOTARY_KEY_PATH, NOTARY_KEY_ID, NOTARY_ISSUER    an App Store Connect API key
#     NOTARY_APPLE_ID, NOTARY_TEAM_ID, NOTARY_PASSWORD an Apple ID with an app-specific password
#   SPARKLE_PUBLIC_ED_KEY    the public half of the update signing key (`generate_keys -p`)
#   SPARKLE_FEED_URL         optional, defaults to the appcast on this repository's mac-appcast release
#
# Output (in --output): OverInk.app, OverInk-<version>-mac.dmg and SHA256SUMS-mac.txt.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
package="$root/src/OverInk.Mac"
version="0.1.0"
output="$root/artifacts/mac"
universal=0
notarize=1

while (( $# > 0 )); do
  case "$1" in
    --version) version="${2:?--version needs a value}"; shift 2 ;;
    --output) output="${2:?--output needs a value}"; shift 2 ;;
    --universal) universal=1; shift ;;
    --skip-notarize) notarize=0; shift ;;
    -h|--help) sed -n '2,19p' "$0"; exit 0 ;;
    *) print -u2 "Unknown argument: $1"; exit 2 ;;
  esac
done

version="${version#v}"
identity="${MACOS_SIGN_IDENTITY:--}"
feed_url="${SPARKLE_FEED_URL:-https://github.com/hickmanz/overink/releases/download/mac-appcast/appcast.xml}"
build_number="$(python3 "$root/scripts/mac_release.py" build-number "$version")"
source "$root/scripts/lib-mac-sign.sh"

hardened=0
[[ "$identity" != "-" ]] && hardened=1

notary_args=()
if [[ -n "${NOTARY_PROFILE:-}" ]]; then
  notary_args=(--keychain-profile "$NOTARY_PROFILE")
elif [[ -n "${NOTARY_KEY_PATH:-}" ]]; then
  notary_args=(--key "$NOTARY_KEY_PATH" --key-id "${NOTARY_KEY_ID:?NOTARY_KEY_ID is required}" --issuer "${NOTARY_ISSUER:?NOTARY_ISSUER is required}")
elif [[ -n "${NOTARY_APPLE_ID:-}" ]]; then
  notary_args=(--apple-id "$NOTARY_APPLE_ID" --team-id "${NOTARY_TEAM_ID:?NOTARY_TEAM_ID is required}" --password "${NOTARY_PASSWORD:?NOTARY_PASSWORD is required}")
fi

do_notarize=0
if (( notarize )) && (( hardened )) && (( ${#notary_args[@]} > 0 )); then
  do_notarize=1
fi

print "==> OverInk $version (build $build_number), signing with: ${identity/#-/ad-hoc}, notarization: $( (( do_notarize )) && print on || print off )"
if (( hardened )) && (( ! do_notarize )); then
  print "    (signed but not notarized: Gatekeeper will warn on other Macs)"
fi

# --- Build ---------------------------------------------------------------------------------------
build_args=(-c release --package-path "$package" --disable-keychain)
if (( universal )); then
  build_args+=(--arch arm64 --arch x86_64)
fi
print "==> Building"
swift build "${build_args[@]}"
bin_dir="$(swift build "${build_args[@]}" --show-bin-path)"

# --- Assemble the app ----------------------------------------------------------------------------
app="$output/OverInk.app"
contents="$app/Contents"
rm -rf "$output"
mkdir -p "$contents/MacOS" "$contents/Resources" "$contents/Frameworks"

cp "$bin_dir/OverInkMac" "$contents/MacOS/OverInkMac"
cp "$package/Supporting/Info.plist" "$contents/Info.plist"
# The iPad web client (shared with the Windows app); -L follows the symlink in the package.
cp -RL "$package/Sources/OverInkMac/RemoteWeb" "$contents/Resources/RemoteWeb"
ditto "$bin_dir/Sparkle.framework" "$contents/Frameworks/Sparkle.framework"

# App icon (regenerate with `swift scripts/make-mac-icon.swift`).
cp "$package/Supporting/AppIcon.icns" "$contents/Resources/AppIcon.icns"

plist="$contents/Info.plist"
plutil -replace CFBundleShortVersionString -string "$version" "$plist"
plutil -replace CFBundleVersion -string "$build_number" "$plist"
plutil -replace SUFeedURL -string "$feed_url" "$plist"
if [[ -n "${SPARKLE_PUBLIC_ED_KEY:-}" ]]; then
  plutil -replace SUPublicEDKey -string "$SPARKLE_PUBLIC_ED_KEY" "$plist"
else
  print "    warning: SPARKLE_PUBLIC_ED_KEY is not set, so this build cannot install updates"
fi
plutil -lint "$plist" >/dev/null

# --- Sign ----------------------------------------------------------------------------------------
print "==> Signing"
sign_sparkle "$contents/Frameworks/Sparkle.framework" "$identity" "$hardened"
app_flags=(--force --sign "$identity" --entitlements "$package/Supporting/OverInk.entitlements")
if (( hardened )); then
  app_flags+=(--timestamp --options runtime)
fi
codesign "${app_flags[@]}" "$app"
codesign --verify --deep --strict --verbose=2 "$app"

notarize_and_staple() {
  local target="$1" upload="$1"
  if [[ -d "$target" ]]; then
    upload="$(mktemp -d)/$(basename "$target").zip"
    ditto -c -k --keepParent "$target" "$upload"
  fi
  print "==> Notarizing $(basename "$target") (this can take a few minutes)"
  xcrun notarytool submit "$upload" "${notary_args[@]}" --wait
  xcrun stapler staple "$target"
}

if (( do_notarize )); then
  notarize_and_staple "$app"
  spctl --assess --type execute --verbose=2 "$app"
fi

# --- DMG -----------------------------------------------------------------------------------------
dmg="$output/OverInk-$version-mac.dmg"
print "==> Creating $(basename "$dmg")"
stage="$(mktemp -d)"
ditto "$app" "$stage/OverInk.app"
ln -s /Applications "$stage/Applications"
hdiutil create -volname "OverInk" -srcfolder "$stage" -fs HFS+ -format UDZO -ov "$dmg" >/dev/null
rm -rf "$stage"

if (( hardened )); then
  codesign --force --timestamp --sign "$identity" "$dmg"
fi
if (( do_notarize )); then
  notarize_and_staple "$dmg"
fi

( cd "$output" && shasum -a 256 "$(basename "$dmg")" > SHA256SUMS-mac.txt )

print ""
print "Done:"
ls -lh "$output" | tail -n +2
