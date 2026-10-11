# Модель «Улучшить ×2»

`realesr-general-x4v3` из [Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN) (Xintao Wang и др., BSD-3-Clause),
сконвертирована из официального `realesr-general-x4v3.pth` (релиз v0.2.5.0) без изменения весов.

| Файл | Кто использует | Формат |
|---|---|---|
| `realesr-general-x4v3.onnx` | Windows (ONNX Runtime, DirectML/CPU) | ONNX opset 13, вход `input` [1,3,H,W] RGB 0..1, выход `output` [1,3,4H,4W] |
| `realesr-general-x4v3.bin` | macOS (MPSGraph) | `QSR1` + float32 LE: для каждого из 34 conv — W (OIHW), b, затем PReLU-наклон (кроме последнего) |

Приложение скачивает файл по ссылке с фиксированным коммитом и проверяет SHA-256.
Сеть даёт ×4, приложение усредняет 2×2 до ×2.

# Модели OCR (Windows)

PP-OCRv5 из [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) (Apache-2.0).

| Файл | Что это | Формат |
|---|---|---|
| `ppocrv5_det.onnx` | PP-OCRv5 mobile det — поиск строк текста (из пакета OnnxOCR, Apache-2.0) | вход `x` [1,3,H,W] RGB, ImageNet mean/std, H,W кратны 32; выход — карта вероятностей [1,1,H,W] |
| `ppocrv5_eslav_rec.onnx` | eslav PP-OCRv5 mobile rec — распознавание: русский/украинский/белорусский + латиница, цифры, знаки | вход `inputs_0` [1,48,W,3] RGB (x−127.5)/127.5, W кратна 8; выход [1,W/8,519] — вероятности CTC (0 — пусто) |
| `ppocrv5_eslav_keys.txt` | словарь распознавания (518 символов, последний — пробел) | UTF-8, по строке на класс 1…518 |

Распознаватель сконвертирован tf2onnx из FP16 TFLite-версии (проект PPOCRv5-Android, Apache-2.0),
фиксированная ширина 320 заменена на произвольную (в шести Reshape длина последовательности 40 → −1); на ширине 320 выход совпадает бит в бит.
