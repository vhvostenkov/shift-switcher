#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p build
swiftc Sources/Core.swift Tests/main.swift -o build/core-tests
build/core-tests
app="build/Shift Switcher.app"
mkdir -p "$app/Contents/MacOS"
swiftc -O -target arm64-apple-macos13.0 Sources/Core.swift Sources/Acceptance.swift Sources/Settings.swift Sources/main.swift -framework AppKit -framework SwiftUI -framework Carbon -o "$app/Contents/MacOS/Shift Switcher"
cp Info.plist "$app/Contents/Info.plist"
mkdir -p "$app/Contents/Resources"
cp Assets/AppIcon.icns "$app/Contents/Resources/AppIcon.icns"
codesign --force --sign - "$app"
echo "Built: $PWD/$app"
