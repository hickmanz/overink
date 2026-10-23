# Shared by run-mac-dev.sh and package-mac.sh (source this file).
#
# sign_sparkle <Sparkle.framework> <identity> <hardened: 1|0>
#   Signs Sparkle inside-out (XPC services, Autoupdate, Updater.app, then the framework itself), which is
#   what Sparkle's signing guide asks for and what notarization needs. Do not use `codesign --deep` for this:
#   it would stamp the app's identifier and requirements onto Sparkle's helpers.
sign_sparkle() {
  local framework="$1" identity="$2" hardened="$3"
  local flags=(--force --sign "$identity")
  if [[ "$hardened" == "1" ]]; then
    flags+=(--timestamp --options runtime)
  fi
  local version_dir="$framework/Versions/B"
  [[ -d "$version_dir" ]] || version_dir="$framework/Versions/A"

  local nested
  for nested in "$version_dir"/XPCServices/*.xpc "$version_dir/Autoupdate" "$version_dir/Updater.app"; do
    [[ -e "$nested" ]] || continue
    codesign "${flags[@]}" "$nested"
  done
  codesign "${flags[@]}" "$framework"
}
