#!/bin/bash
# Writes appcast.xml for RemoteForKEF-macos.zip in <folder>, signed with the Sparkle key in
# $SPARKLE_ED_PRIVATE_KEY. Stops if that key does not belong to SUPublicEDKey in Info.plist, since
# installed copies would refuse the update. Used by the release workflow.
# Needs: SPARKLE_ED_PRIVATE_KEY, SPARKLE_VERSION, SPARKLE_TOOLS_SHA256, TAG, GITHUB_REPOSITORY.
# Usage: scripts/sign-appcast.sh <folder> <appcast.xml>
set -euo pipefail
cd "$(dirname "$0")/.."

FOLDER="$1"
OUT="$2"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

if [[ -z "${SPARKLE_ED_PRIVATE_KEY:-}" ]]; then
    echo "error: SPARKLE_ED_PRIVATE_KEY is empty (see CONTRIBUTING.md)" >&2
    exit 1
fi
printf '%s' "$SPARKLE_ED_PRIVATE_KEY" > "$WORK/sparkle.key"
expected="$(plutil -extract SUPublicEDKey raw Resources/Info.plist)"
actual="$(swift scripts/sparkle-public-key.swift "$WORK/sparkle.key")"
if [[ "$expected" != "$actual" ]]; then
    echo "error: SPARKLE_ED_PRIVATE_KEY does not belong to SUPublicEDKey in Info.plist" >&2
    exit 1
fi

curl -fsSL -o "$WORK/sparkle.tar.xz" \
    "https://github.com/sparkle-project/Sparkle/releases/download/$SPARKLE_VERSION/Sparkle-$SPARKLE_VERSION.tar.xz"
echo "$SPARKLE_TOOLS_SHA256  $WORK/sparkle.tar.xz" | shasum -a 256 -c -
tar -xJf "$WORK/sparkle.tar.xz" -C "$WORK" ./bin/generate_appcast

# generate_appcast reads every archive in the folder, so it only gets the Mac zip.
mkdir "$WORK/archives"
cp "$FOLDER/RemoteForKEF-macos.zip" "$WORK/archives/"
"$WORK/bin/generate_appcast" \
    --ed-key-file "$WORK/sparkle.key" \
    --download-url-prefix "https://github.com/$GITHUB_REPOSITORY/releases/download/$TAG/" \
    --full-release-notes-url "https://github.com/$GITHUB_REPOSITORY/releases" \
    --link "https://github.com/$GITHUB_REPOSITORY" \
    -o "$OUT" "$WORK/archives"
grep -q "sparkle:edSignature" "$OUT"
cat "$OUT"
