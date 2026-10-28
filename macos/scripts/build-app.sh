#!/bin/bash
# Builds "build/Remote for KEF.app" from the Swift package and signs it ad hoc.
# Usage: scripts/build-app.sh [--universal]
set -euo pipefail
cd "$(dirname "$0")/.."

ARCH_FLAGS=()
if [[ "${1:-}" == "--universal" ]]; then
    ARCH_FLAGS=(--arch arm64 --arch x86_64)
fi

swift build -c release --product KEFRemote ${ARCH_FLAGS[@]+"${ARCH_FLAGS[@]}"}
BIN_DIR="$(swift build -c release --product KEFRemote ${ARCH_FLAGS[@]+"${ARCH_FLAGS[@]}"} --show-bin-path)"

APP="build/Remote for KEF.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_DIR/KEFRemote" "$APP/Contents/MacOS/KEFRemote"
cp Resources/Info.plist "$APP/Contents/Info.plist"

# App icon made in Icon Composer, saved as Resources/AppIcon.icon. Compiling it needs
# actool from Xcode 26; without it the app builds with the generic icon.
if [[ -d Resources/AppIcon.icon ]]; then
    if xcrun actool Resources/AppIcon.icon --compile "$APP/Contents/Resources" \
        --app-icon AppIcon --platform macosx --target-device mac --minimum-deployment-target 14.0 \
        --output-partial-info-plist build/AppIcon.plist --errors --warnings >/dev/null; then
        /usr/libexec/PlistBuddy -c "Add :CFBundleIconName string AppIcon" -c "Add :CFBundleIconFile string AppIcon" "$APP/Contents/Info.plist"
    else
        echo "warning: could not compile Resources/AppIcon.icon (needs Xcode 26); using the generic icon" >&2
    fi
fi

codesign --force --sign - --timestamp=none "$APP"

echo "Built $APP"
