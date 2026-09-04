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
# Стабильная подпись: TCC (Запись экрана) привязывается к сертификату, а не к хэшу бинарника.
# Один раз: Связка ключей → Ассистент сертификации → Создать сертификат → "QScreen Dev", тип "Подпись кода".
SIGN_ID="${SIGN_ID:-}"
if [ -z "$SIGN_ID" ] && security find-identity -v -p codesigning 2>/dev/null | grep -q "QScreen Dev"; then SIGN_ID="QScreen Dev"; fi
codesign --force --deep --sign "${SIGN_ID:--}" "$APP"
echo "OK: $APP (sign: ${SIGN_ID:-ad-hoc})"
