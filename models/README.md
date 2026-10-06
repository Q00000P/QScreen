# Модель «Улучшить ×2»

`realesr-general-x4v3` из [Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN) (Xintao Wang и др., BSD-3-Clause),
сконвертирована из официального `realesr-general-x4v3.pth` (релиз v0.2.5.0) без изменения весов.

| Файл | Кто использует | Формат |
|---|---|---|
| `realesr-general-x4v3.onnx` | Windows (ONNX Runtime, DirectML/CPU) | ONNX opset 13, вход `input` [1,3,H,W] RGB 0..1, выход `output` [1,3,4H,4W] |
| `realesr-general-x4v3.bin` | macOS (MPSGraph) | `QSR1` + float32 LE: для каждого из 34 conv — W (OIHW), b, затем PReLU-наклон (кроме последнего) |

Приложение скачивает файл по ссылке с фиксированным коммитом и проверяет SHA-256.
Сеть даёт ×4, приложение усредняет 2×2 до ×2.
