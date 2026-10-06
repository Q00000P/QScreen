using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;

namespace QScreen
{
    public static class OcrEngine
    {
        /// <summary>Офлайн-OCR системным движком Windows. Пустая строка — текста нет; null — движок недоступен (нет языкового пакета).
        /// Длинный скролл-скрин раньше целиком ужимался до лимита движка и становился нечитаемым — теперь режется
        /// на полосы с нахлёстом, повторы на стыках убираются.</summary>
        public static string? ExtractText(BitmapSource image)
        {
            try
            {
                var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
                             ?? Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.Select(l => Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(l)).FirstOrDefault(e => e != null);
                if (engine == null) return null;

                uint max = Windows.Media.Ocr.OcrEngine.MaxImageDimension;
                BitmapSource src = image;
                if (image.PixelWidth > max)   // шире лимита — ужимаем по ширине (редкость)
                {
                    double k = max / (double)image.PixelWidth;
                    src = new TransformedBitmap(image, new System.Windows.Media.ScaleTransform(k, k));
                }
                var bytes = BitmapUtil.GetBgra(src, out int w, out int h, out int stride);

                int tileH = (int)Math.Min(max - 200, 2400), overlap = 160;
                var lines = new List<string>();
                for (int y = 0; y < h; y += tileH)
                {
                    int th = Math.Min(tileH + overlap, h - y);
                    var tile = new byte[stride * th];
                    Buffer.BlockCopy(bytes, y * stride, tile, 0, tile.Length);
                    foreach (var (text, top) in Recognize(engine, tile, w, th))
                    {
                        var t = text.Trim();
                        if (y > 0 && top < overlap + 8 && lines.Skip(Math.Max(0, lines.Count - 12)).Contains(t)) continue;
                        lines.Add(t);
                    }
                    if (y + th >= h) break;
                }
                return string.Join("\n", lines).TrimEnd();
            }
            catch { return null; }
        }

        private static List<(string Text, double Top)> Recognize(Windows.Media.Ocr.OcrEngine engine, byte[] bgra, int w, int h)
        {
            return Task.Run(async () =>
            {
                using var sb = SoftwareBitmap.CreateCopyFromBuffer(bgra.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
                var result = await engine.RecognizeAsync(sb);
                var list = new List<(string, double)>();
                foreach (var line in result.Lines)
                {
                    double top = line.Words.Count > 0 ? line.Words.Min(wd => wd.BoundingRect.Y) : 0;
                    list.Add((line.Text, top));
                }
                return list;
            }).GetAwaiter().GetResult();
        }
    }
}
