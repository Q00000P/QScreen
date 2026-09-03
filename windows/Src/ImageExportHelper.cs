using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using BitmapFrame = System.Windows.Media.Imaging.BitmapFrame;
using WinRTEncoder = Windows.Graphics.Imaging.BitmapEncoder;

namespace QScreen
{
    public static class ImageExportHelper
    {
        /// <summary>Кодирует изображение в выбранный формат. Возвращает (данные, реальное расширение).</summary>
        public static (byte[] Data, string Ext)? ExportData(BitmapSource image, string? format = null, double? quality = null)
        {
            var fmt = (format ?? AppSettings.DefaultFormat).ToLowerInvariant();
            var q = quality ?? AppSettings.JpegQuality;
            try
            {
                switch (fmt)
                {
                    case "heic":
                    case "heif":
                        var heic = EncodeHeic(image, q);
                        if (heic != null) return (heic, "heic");
                        return (EncodeJpeg(image, q), "jpg"); // нет HEVC-кодека в системе
                    case "jpg":
                    case "jpeg":
                        return (EncodeJpeg(image, q), "jpg");
                    case "pdf":
                        return (EncodePdf(image), "pdf");
                    default:
                        return (EncodePng(image), "png");
                }
            }
            catch { return null; }
        }

        public static byte[] EncodePng(BitmapSource image)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(image));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        public static byte[] EncodeJpeg(BitmapSource image, double q)
        {
            var enc = new JpegBitmapEncoder { QualityLevel = (int)Math.Round(Math.Clamp(q, 0.1, 1.0) * 100) };
            // JPEG без альфы: кладём на белый, иначе прозрачные углы уходят в чёрный
            enc.Frames.Add(BitmapFrame.Create(Flatten(image)));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }

        private static BitmapSource Flatten(BitmapSource src)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, src.PixelWidth, src.PixelHeight));
                dc.DrawImage(src, new System.Windows.Rect(0, 0, src.PixelWidth, src.PixelHeight));
            }
            var rtb = new RenderTargetBitmap(src.PixelWidth, src.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        /// <summary>HEIC через WIC HEIF-энкодер (нужно расширение «HEVC Video Extensions»). null — если кодека нет.</summary>
        public static byte[]? EncodeHeic(BitmapSource image, double q)
        {
            try
            {
                var bytes = BitmapUtil.GetBgra(image, out int w, out int h, out _);
                return Task.Run(async () =>
                {
                    using var stream = new InMemoryRandomAccessStream();
                    var props = new BitmapPropertySet
                    {
                        { "ImageQuality", new BitmapTypedValue((float)Math.Clamp(q, 0.1, 1.0), Windows.Foundation.PropertyType.Single) }
                    };
                    var enc = await WinRTEncoder.CreateAsync(WinRTEncoder.HeifEncoderId, stream, props);
                    enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)w, (uint)h, 96, 96, bytes);
                    await enc.FlushAsync();
                    var result = new byte[stream.Size];
                    using var reader = new DataReader(stream.GetInputStreamAt(0));
                    await reader.LoadAsync((uint)stream.Size);
                    reader.ReadBytes(result);
                    return result;
                }).GetAwaiter().GetResult();
            }
            catch { return null; }
        }

        /// <summary>Одностраничный PDF с изображением без потерь (FlateDecode RGB). Размер страницы = логический размер (pt).</summary>
        public static byte[] EncodePdf(BitmapSource image)
        {
            var bgra = BitmapUtil.GetBgra(image, out int w, out int h, out int stride);
            var rgb = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int s = y * stride + x * 4, d = (y * w + x) * 3;
                    byte a = bgra[s + 3];
                    // на белый
                    rgb[d] = (byte)((bgra[s + 2] * a + 255 * (255 - a)) / 255);
                    rgb[d + 1] = (byte)((bgra[s + 1] * a + 255 * (255 - a)) / 255);
                    rgb[d + 2] = (byte)((bgra[s] * a + 255 * (255 - a)) / 255);
                }
            byte[] flate;
            using (var ms = new MemoryStream())
            {
                using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(rgb, 0, rgb.Length);
                flate = ms.ToArray();
            }

            double scale = Math.Max(1.0, image.DpiX / 96.0);
            double pw = w / scale, ph = h / scale;
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            var outMs = new MemoryStream();
            var offsets = new long[6];
            void W(string s) { var b = Encoding.ASCII.GetBytes(s); outMs.Write(b, 0, b.Length); }

            W("%PDF-1.4\n%\u00e2\u00e3\u00cf\u00d3\n");
            offsets[1] = outMs.Position; W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            offsets[2] = outMs.Position; W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
            offsets[3] = outMs.Position; W($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pw.ToString("0.##", inv)} {ph.ToString("0.##", inv)}] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");
            offsets[4] = outMs.Position;
            W($"4 0 obj\n<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {flate.Length} >>\nstream\n");
            outMs.Write(flate, 0, flate.Length);
            W("\nendstream\nendobj\n");
            var content = $"q {pw.ToString("0.##", inv)} 0 0 {ph.ToString("0.##", inv)} 0 0 cm /Im0 Do Q";
            offsets[5] = outMs.Position; W($"5 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
            long xref = outMs.Position;
            W("xref\n0 6\n0000000000 65535 f \n");
            for (int i = 1; i <= 5; i++) W(offsets[i].ToString("D10") + " 00000 n \n");
            W($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return outMs.ToArray();
        }
    }
}
