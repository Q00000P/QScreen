# QScreen

Скриншотер и скринрекордер для macOS и Windows. Одна логика, два нативных движка.

| | macOS (`macos/`) | Windows (`windows/`) |
|---|---|---|
| Захват | CGWindowList / ScreenCaptureKit | GDI + PrintWindow / Windows.Graphics.Capture |
| Видео | SCK по дисплею → CoreImage композиция → AVAssetWriter | WGC по монитору → Direct2D композиция → ffmpeg |
| OCR | Vision | Windows.Media.Ocr |
| UI | SwiftUI + AppKit | WPF |

Захват области / умный захват окна / скролл-скриншот / весь экран, редактор с 11 инструментами, Beautify, DnD, Pin, OCR, PNG/HEIC/JPG/PDF.
Запись зоны с живым перемещением и ресайзом (в т.ч. через стык мониторов), пауза, микрофон, Live Blur (пикселизация в видео).

## Сборка

**macOS**
```sh
cd macos && ./build-app.sh
```

**Windows** (.NET 8 SDK)
```powershell
cd windows
dotnet build -c Release --no-incremental
# релиз:
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```
ffmpeg для записи видео скачивается приложением по запросу (или `winget install Gyan.FFmpeg`).

## Релиз
Тег `vX.Y.Z`, в ассетах два zip: `QScreen-macOS-vX.Y.Z.zip` и `QScreen-Windows-vX.Y.Z.zip` — автообновление на каждой платформе берёт свой по имени.
