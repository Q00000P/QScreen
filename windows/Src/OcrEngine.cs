using System;
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
        /// <summary>Офлайн-OCR системным движком Windows. Пустая строка — текста нет; null — движок недоступен (нет языкового пакета).</summary>
        public static string? ExtractText(BitmapSource image)
        {
            try
            {
                var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
                             ?? Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.Select(l => Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(l)).FirstOrDefault(e => e != null);
                if (engine == null) return null;

                // Лимит движка по размеру — уменьшаем, если надо
                uint max = Windows.Media.Ocr.OcrEngine.MaxImageDimension;
                BitmapSource src = image;
                double k = Math.Max(image.PixelWidth, image.PixelHeight) / (double)max;
                if (k > 1.0) src = new TransformedBitmap(image, new System.Windows.Media.ScaleTransform(1 / k, 1 / k));

                var bytes = BitmapUtil.GetBgra(src, out int w, out int h, out _);
                return Task.Run(async () =>
                {
                    using var sb = SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
                    var result = await engine.RecognizeAsync(sb);
                    var text = new StringBuilder();
                    foreach (var line in result.Lines) text.AppendLine(line.Text);
                    return text.ToString().TrimEnd();
                }).GetAwaiter().GetResult();
            }
            catch { return null; }
        }
    }
}
