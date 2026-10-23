#!/bin/zsh
# Signs a release DMG for Sparkle and adds it to the appcast.
#
#   SPARKLE_ED_PRIVATE_KEY=<base64 key> scripts/mac-appcast.sh \
#       --dmg artifacts/mac/OverInk-0.3.0-mac.dmg --version 0.3.0 \
#       --tag v0.3.0 [--previous old-appcast.xml] [--repo owner/name] [--output artifacts/mac/appcast.xml]
#
# The private key is the one `generate_keys -x file` exports; keep it in a repository secret. The result is
# published to the rolling `mac-appcast` release, which is where the app's SUFeedURL points.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
dmg=""; version=""; tag=""; previous=""; repo="${GITHUB_REPOSITORY:-hickmanz/overink}"; output=""

while (( $# > 0 )); do
  case "$1" in
    --dmg) dmg="${2:?}"; shift 2 ;;
    --version) version="${2:?}"; shift 2 ;;
    --tag) tag="${2:?}"; shift 2 ;;
    --previous) previous="${2:?}"; shift 2 ;;
    --repo) repo="${2:?}"; shift 2 ;;
    --output) output="${2:?}"; shift 2 ;;
    *) print -u2 "Unknown argument: $1"; exit 2 ;;
  esac
done

[[ -f "$dmg" ]] || { print -u2 "--dmg must point at the DMG"; exit 2; }
[[ -n "$version" ]] || { print -u2 "--version is required"; exit 2; }
[[ -n "${SPARKLE_ED_PRIVATE_KEY:-}" ]] || { print -u2 "SPARKLE_ED_PRIVATE_KEY is not set"; exit 2; }
version="${version#v}"
tag="${tag:-v$version}"
output="${output:-$(dirname "$dmg")/appcast.xml}"

sign_update="${SPARKLE_SIGN_UPDATE:-$root/src/OverInk.Mac/.build/artifacts/sparkle/Sparkle/bin/sign_update}"
[[ -x "$sign_update" ]] || { print -u2 "sign_update not found at $sign_update (run swift build first)"; exit 2; }

key_file="$(mktemp)"
trap 'rm -f "$key_file"' EXIT
chmod 600 "$key_file"
print -rn -- "$SPARKLE_ED_PRIVATE_KEY" > "$key_file"

signature="$("$sign_update" --ed-key-file "$key_file" -p "$dmg")"
# sign_update reports some failures on stdout with a zero exit code, so check what came back.
if [[ ! "$signature" =~ '^[A-Za-z0-9+/]{80,}={0,2}$' ]]; then
  print -u2 "sign_update did not return a signature: $signature"
  exit 1
fi
length="$(stat -f%z "$dmg")"
url="https://github.com/$repo/releases/download/$tag/$(basename "$dmg")"

previous_args=()
[[ -n "$previous" ]] && previous_args=(--previous "$previous")

python3 "$root/scripts/mac_release.py" appcast \
  --output "$output" \
  "${previous_args[@]}" \
  --version "$version" \
  --url "$url" \
  --signature "$signature" \
  --length "$length" \
  --release-notes-url "https://github.com/$repo/releases/tag/$tag"

print "Wrote $output ($(basename "$dmg"), $length bytes)"
