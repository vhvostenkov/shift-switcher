#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
app="build/${APP_NAME:-Shift Switcher}.app"
# Replacing a running ad-hoc bundle invalidates the identity trusted by macOS.
if /bin/ps -axo comm= | /usr/bin/grep -Fx -- "$PWD/$app/Contents/MacOS/Shift Switcher" >/dev/null; then
  echo "Stop the app or choose a different APP_NAME before rebuilding: $app" >&2
  exit 1
fi
mkdir -p build
swiftc Sources/Core.swift Sources/CorrectionPreferences.swift Sources/Shortcuts.swift Sources/LicenseCore.swift Tests/main.swift -o build/core-tests
build/core-tests
mkdir -p "$app/Contents/MacOS"
swiftc -O -target arm64-apple-macos13.0 Sources/Core.swift Sources/CorrectionPreferences.swift Sources/Shortcuts.swift Sources/GlobalShortcuts.swift Sources/LicenseCore.swift Sources/Licensing.swift Sources/Acceptance.swift Sources/Settings.swift Sources/main.swift -framework AppKit -framework SwiftUI -framework Carbon -framework ServiceManagement -framework Security -framework CryptoKit -o "$app/Contents/MacOS/Shift Switcher"
cp Info.plist "$app/Contents/Info.plist"
if [ "${CANDIDATE_BUILD:-0}" = "1" ]; then
  /usr/libexec/PlistBuddy -c 'Set :CFBundleIdentifier local.viktor.ShiftSwitcher.Candidate' "$app/Contents/Info.plist"
fi
mkdir -p "$app/Contents/Resources"
cp Assets/license-public-keys.json "$app/Contents/Resources/license-public-keys.json"
cp Assets/AppIcon.icns "$app/Contents/Resources/AppIcon.icns"
codesign --force --sign - "$app"
echo "Built: $PWD/$app"
