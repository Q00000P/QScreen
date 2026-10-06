using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace QScreen
{
    /// <summary>
    /// «Улучшить ×2»: нейросеть Real-ESRGAN general-x4v3 (BSD-3) через ONNX Runtime — DirectML на видеокарте, иначе процессор.
    /// Сеть даёт ×4, результат усредняется 2×2 до ×2: так края текста чище, чем при прямом ×2.
    /// Картинка обрабатывается плитками 192 px с полями 12 px (без швов на стыках, без гигантских тензоров).
    /// Модель (~5 МБ) скачивается при первом использовании из репозитория по ссылке с фиксированным коммитом и сверяется по SHA-256.
    /// </summary>
    public static class AiUpscaler
    {
        private const string ModelUrl = "https://raw.githubusercontent.com/Q00000P/QScreen/70dbcce553d5107031a372ad49855660f08af53b/models/realesr-general-x4v3.onnx";
        private const string ModelSha256 = "6fab68b01597a761ff00e765bc8b009d891e0300ed9e21a73464aff23bc1c4de";
        private const int Tile = 192, Pad = 12, S = Tile + 2 * Pad;

        private static string ModelPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QScreen", "models", "realesr-general-x4v3.onnx");

        private static InferenceSession? _session;
        public static bool UsesGpu { get; private set; }

        /// <summary>Модель на месте и целая — путь; иначе спросить и скачать. null — отказались или не вышло.</summary>
        public static async Task<string?> EnsureModelAsync()
        {
            if (File.Exists(ModelPath) && Sha256(ModelPath) == ModelSha256) return ModelPath;

            var r = MessageBox.Show("Для «Улучшить ×2» нужна модель нейросети (около 5 МБ, разовая загрузка).\n\nСкачать сейчас?",
                "QScreen — улучшение", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                client.DefaultRequestHeaders.Add("User-Agent", "QScreen-Win");
                var bytes = await client.GetByteArrayAsync(ModelUrl);
                if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != ModelSha256) throw new Exception("контрольная сумма не совпала");
                await File.WriteAllBytesAsync(ModelPath, bytes);
                return ModelPath;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось скачать модель:\n" + ex.Message, "QScreen", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
        }

        private static string Sha256(string path)
        {
            using var f = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
        }

        private static InferenceSession Session()
        {
            if (_session != null) return _session;
            var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            try
            {
                // DirectML: любая видеокарта с DirectX 12
                so.EnableMemoryPattern = false;
                so.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                so.AppendExecutionProvider_DML(0);
                _session = new InferenceSession(ModelPath, so);
                UsesGpu = true;
            }
            catch
            {
                so.Dispose();
                _session = new InferenceSession(ModelPath, new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL });
                UsesGpu = false;
            }
            return _session;
        }

        /// <summary>×2. Вызывать в фоне. progress — 0..1.</summary>
        public static BitmapSource Upscale2x(BitmapSource src, Action<double>? progress)
        {
            var px = BitmapUtil.GetBgra(src, out int w, out int h, out int stride);
            int W2 = w * 2, H2 = h * 2;
            var dst = new byte[W2 * H2 * 4];
            var sess = Session();
            int plane = S * S;
            var inBuf = new float[3 * plane];
            var input = new DenseTensor<float>(inBuf, new[] { 1, 3, S, S });   // тензор поверх массива, без копий
            int tilesX = (w + Tile - 1) / Tile, tilesY = (h + Tile - 1) / Tile, total = tilesX * tilesY, done = 0;

            for (int ty = 0; ty < h; ty += Tile)
                for (int tx = 0; tx < w; tx += Tile)
                {
                    // плитка с полями; за краем картинки — повтор крайних пикселей
                    for (int yy = 0; yy < S; yy++)
                    {
                        int sy = Math.Clamp(ty - Pad + yy, 0, h - 1);
                        for (int xx = 0; xx < S; xx++)
                        {
                            int sx = Math.Clamp(tx - Pad + xx, 0, w - 1);
                            int i = sy * stride + sx * 4, o = yy * S + xx;
                            inBuf[o] = px[i + 2] / 255f;              // R
                            inBuf[plane + o] = px[i + 1] / 255f;      // G
                            inBuf[2 * plane + o] = px[i] / 255f;      // B
                        }
                    }

                    using var results = sess.Run(new[] { NamedOnnxValue.CreateFromTensor("input", input) });
                    var outT = results.First().AsTensor<float>();
                    var od = outT is DenseTensor<float> d ? d.Buffer.Span.ToArray() : outT.ToArray();
                    int S4 = S * 4, plane4 = S4 * S4;

                    int vw = Math.Min(Tile, w - tx), vh = Math.Min(Tile, h - ty);
                    for (int oy = 0; oy < vh * 2; oy++)
                    {
                        int y4 = Pad * 4 + oy * 2;
                        int drow = ((ty * 2 + oy) * W2 + tx * 2) * 4;
                        for (int ox = 0; ox < vw * 2; ox++)
                        {
                            int x4 = Pad * 4 + ox * 2;
                            int a = y4 * S4 + x4, b = a + S4;
                            float r = (od[a] + od[a + 1] + od[b] + od[b + 1]) * 0.25f;
                            float g = (od[plane4 + a] + od[plane4 + a + 1] + od[plane4 + b] + od[plane4 + b + 1]) * 0.25f;
                            float bl = (od[2 * plane4 + a] + od[2 * plane4 + a + 1] + od[2 * plane4 + b] + od[2 * plane4 + b + 1]) * 0.25f;
                            int di = drow + ox * 4;
                            dst[di] = ToByte(bl); dst[di + 1] = ToByte(g); dst[di + 2] = ToByte(r); dst[di + 3] = 255;
                        }
                    }
                    progress?.Invoke(++done / (double)total);
                }

            var bs = BitmapSource.Create(W2, H2, src.DpiX * 2, src.DpiY * 2, PixelFormats.Bgra32, null, dst, W2 * 4);
            bs.Freeze();
            return bs;
        }

        private static byte ToByte(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
    }
}
