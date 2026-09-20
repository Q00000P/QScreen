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

# SPM-бандлы ресурсов (KeyboardShortcuts и пр.): resource_bundle_accessor ищет их рядом с
# исполняемым файлом, а не в Contents/Resources. Без этого на чужом маке — fatal error при старте.
for B in .build/release/*.bundle; do
  [ -d "$B" ] || continue
  cp -R "$B" "$APP/Contents/MacOS/"
  cp -R "$B" "$APP/Contents/Resources/"
done

# Стабильная подпись: TCC (Запись экрана) привязывается к сертификату, а не к хэшу бинарника.
# Один раз: Связка ключей → Ассистент сертификации → Создать сертификат → "QScreen Dev", тип "Подпись кода".
SIGN_ID="${SIGN_ID:-}"
if [ -z "$SIGN_ID" ] && security find-identity -v -p codesigning 2>/dev/null | grep -q "QScreen Dev"; then SIGN_ID="QScreen Dev"; fi
codesign --force --deep --sign "${SIGN_ID:--}" "$APP"

# Скачанный zip несёт com.apple.quarantine → Gatekeeper блокирует запуск без Developer ID
xattr -cr "$APP"

echo "OK: $APP (sign: ${SIGN_ID:-ad-hoc})"
