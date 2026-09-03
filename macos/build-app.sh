#!/bin/sh
# Сборка QScreen.app из SPM-пакета. Запуск: ./build-app.sh  → macos/QScreen.app
set -e
cd "$(dirname "$0")"
swift build -c release
APP=QScreen.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp .build/release/QScreen "$APP/Contents/MacOS/QScreen"
cp Resources/Info.plist "$APP/Contents/Info.plist"
cp Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
if [ -d .build/release/KeyboardShortcuts_KeyboardShortcuts.bundle ]; then
  cp -R .build/release/KeyboardShortcuts_KeyboardShortcuts.bundle "$APP/Contents/Resources/"
fi
codesign --force --deep --sign - "$APP"
echo "OK: $APP"
