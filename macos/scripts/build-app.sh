#!/bin/bash
# Builds "build/Remote for KEF.app" from the Swift package, with Sparkle.framework inside for updates.
# Signs it with $SIGN_IDENTITY when set (a "Developer ID Application: ..." identity in the keychain;
# hardened runtime and a secure timestamp, ready for notarization), otherwise ad hoc.
# Usage: [SIGN_IDENTITY=...] scripts/build-app.sh [--universal]
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

# Sparkle: SwiftPM leaves the framework next to the executable; the app loads it from Contents/Frameworks.
SPARKLE="$(find "$BIN_DIR" -maxdepth 3 -name Sparkle.framework | head -n 1)"
if [[ -z "$SPARKLE" ]]; then
    echo "error: Sparkle.framework not found under $BIN_DIR" >&2
    exit 1
fi
SPARKLE="$(cd "$SPARKLE" && pwd -P)"
FRAMEWORK="$APP/Contents/Frameworks/Sparkle.framework"
mkdir -p "$APP/Contents/Frameworks"
ditto "$SPARKLE" "$FRAMEWORK"
# Sparkle's XPC services are only for sandboxed apps; this one isn't sandboxed.
rm -rf "$FRAMEWORK/Versions/B/XPCServices" "$FRAMEWORK/XPCServices"
if ! otool -l "$APP/Contents/MacOS/KEFRemote" | grep -q "path @executable_path/../Frameworks "; then
    install_name_tool -add_rpath @executable_path/../Frameworks "$APP/Contents/MacOS/KEFRemote"
fi
cp Resources/Info.plist "$APP/Contents/Info.plist"
# Info.plist text per language; the folders also tell macOS which languages the app has.
cp -R Resources/*.lproj "$APP/Contents/Resources/"
# The license and the notices for what ships inside, Sparkle among them.
cp ../LICENSE ../THIRD-PARTY-NOTICES.md "$APP/Contents/Resources/"

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

IDENTITY="${SIGN_IDENTITY:-}"
if [[ -n "$IDENTITY" && "$IDENTITY" != "-" ]]; then
    SIGN=(codesign --force --sign "$IDENTITY" --options runtime --timestamp)
else
    SIGN=(codesign --force --sign - --timestamp=none)
fi
# Inside out and without --deep, as Sparkle's guide signs it: helpers, framework, then the app.
"${SIGN[@]}" "$FRAMEWORK/Versions/B/Autoupdate"
"${SIGN[@]}" "$FRAMEWORK/Versions/B/Updater.app"
"${SIGN[@]}" "$FRAMEWORK"
"${SIGN[@]}" "$APP"
codesign --verify --strict --deep "$APP"

echo "Built $APP (signed: ${IDENTITY:-ad hoc})"
