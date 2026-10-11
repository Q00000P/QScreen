using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace QScreen
{
    /// <summary>
    /// OCR на своей нейросети PP-OCRv5 (PaddleOCR, Apache-2.0) через ONNX Runtime на процессоре:
    ///  1) поиск строк (DB-детектор) — по карте вероятностей находятся прямоугольники текста;
    ///  2) распознавание строки целиком (модель eslav: кириллица + латиница + цифры + знаки), декодирование CTC;
    ///  3) правка «похожих букв»: если в слове смешались алфавиты (Кееnetic, ирtime), слово перечитывается
    ///     целиком латиницей или целиком кириллицей; кириллица, приклеенная дефисом к латинскому слову (xray-соге), — латиницей;
    ///  4) раскладка — общая с системным OCR: слова на одной высоте в одну строку, колонки через 4 пробела.
    /// Модели (~13 МБ) скачиваются один раз по ссылке с фиксированным коммитом и сверяются по SHA-256.
    /// </summary>
    public static class NeuralOcr
    {
        private const string BaseUrl = "https://raw.githubusercontent.com/Q00000P/QScreen/4df9e4592a57c5461b6d716e1a84606a251bbe7e/models/";
        private static readonly (string Name, string Sha)[] Files =
        {
            ("ppocrv5_det.onnx", "4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae"),
            ("ppocrv5_eslav_rec.onnx", "659505c2d521c078382d0a8ce57e0a8637db1335b4656cfffebc98436dbf641f"),
            ("ppocrv5_eslav_keys.txt", "f4a227901daa0e4557aaf0a8f2747cff5a2072e38c783ebe57d473a8ad7820de"),
        };
        private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QScreen", "models", "ocr");
        private static string PathOf(string name) => Path.Combine(Dir, name);

        public static bool Ready => !_broken && Files.All(f => File.Exists(PathOf(f.Name)));

        private static bool _asked, _broken;
        private static readonly object _lock = new();
        private static InferenceSession? _det, _rec;
        private static string[] _keys = Array.Empty<string>();
        private static bool[] _allowed = Array.Empty<bool>(), _lat = Array.Empty<bool>(), _cyr = Array.Empty<bool>();

        private const string Ru = "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯабвгдеёжзийклмнопрстуфхцчшщъыьэюя";
        private const string Extra = "«»—–…·•°€₽×№";
        private const string Homo = "АВЕКМНОРСТХУаеорсухABEKMHOPCTXYaeopcyxiI";   // одинаковые на вид буквы двух алфавитов
        private const string Confusable = "гпитмкГПИТМК";                         // кириллица, которую модель путает с r n u m k

        // ---------- модели ----------

        /// <summary>Модели на месте — true; иначе спросить (один раз за запуск) и скачать.</summary>
        public static async Task<bool> EnsureAsync(Window? owner)
        {
            if (Ready) return true;
            if (_asked) return false;
            _asked = true;
            var msg = "Для точного распознавания текста (русский и латиница вперемешку, мелкий шрифт) нужна нейросеть OCR — около 13 МБ, разовая загрузка.\n\nСкачать сейчас? Без неё будет работать встроенный OCR Windows.";
            var r = owner != null
                ? MessageBox.Show(owner, msg, "QScreen — OCR", MessageBoxButton.YesNo, MessageBoxImage.Question)
                : MessageBox.Show(msg, "QScreen — OCR", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return false;
            try
            {
                Directory.CreateDirectory(Dir);
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                client.DefaultRequestHeaders.Add("User-Agent", "QScreen-Win");
                foreach (var (name, sha) in Files)
                {
                    if (File.Exists(PathOf(name)) && Sha256(File.ReadAllBytes(PathOf(name))) == sha) continue;
                    var bytes = await client.GetByteArrayAsync(BaseUrl + name);
                    if (Sha256(bytes) != sha) throw new Exception($"{name}: контрольная сумма не совпала");
                    await File.WriteAllBytesAsync(PathOf(name), bytes);
                }
                _broken = false;
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось скачать модели OCR:\n" + ex.Message, "QScreen", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private static string Sha256(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

        private static void Load()
        {
            lock (_lock)
            {
                if (_det != null) return;
                foreach (var (name, sha) in Files)
                {
                    if (Sha256(File.ReadAllBytes(PathOf(name))) != sha)
                    {
                        try { File.Delete(PathOf(name)); } catch { }
                        _broken = true; _asked = false;
                        throw new InvalidOperationException("модель OCR повреждена");
                    }
                }
                _keys = File.ReadAllText(PathOf("ppocrv5_eslav_keys.txt"), Encoding.UTF8).Split('\n');
                if (_keys.Length > 0 && _keys[^1].Length == 0) _keys = _keys[..^1];
                int nc = _keys.Length + 1;
                _allowed = new bool[nc]; _lat = new bool[nc]; _cyr = new bool[nc];
                _allowed[0] = true;                                   // пусто (blank)
                for (int i = 0; i < _keys.Length; i++)
                {
                    char c = _keys[i].Length > 0 ? _keys[i][0] : ' ';
                    // редкие символы словаря (математика, ① ➀, диакритика) на скриншотах почти не встречаются, а путаницы дают много
                    _allowed[i + 1] = (c >= 32 && c < 127) || Extra.IndexOf(c) >= 0 || Ru.IndexOf(c) >= 0;
                    _lat[i + 1] = c < 128 && char.IsLetter(c);
                    _cyr[i + 1] = Ru.IndexOf(c) >= 0 || c == '№';
                }
                var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                _det = new InferenceSession(PathOf("ppocrv5_det.onnx"), so);
                _rec = new InferenceSession(PathOf("ppocrv5_eslav_rec.onnx"), so);
            }
        }

        // ---------- главный проход ----------

        private const int Strip = 1600, Overlap = 160;   // длинные картинки — полосами (пиксели исходника)
        private const int RecH = 48;

        public static string Extract(BitmapSource image)
        {
            Load();
            using var bmp = OcrEngine.ToBitmap(image, out int w, out int h);
            // мелкий экранный шрифт детектор находит лучше на ×2; очень широкие картинки не раздуваем
            double s = Math.Max(1.0, Math.Min(2.0, 3000.0 / w));

            var words = new List<OcrEngine.Word>();
            for (int y = 0; y < h; y += Strip)
            {
                int sh = Math.Min(Strip + Overlap, h - y);
                bool last = y + sh >= h;
                double ownTop = y == 0 ? double.MinValue : y + Overlap / 2.0;
                double ownBottom = last ? double.MaxValue : y + Strip + Overlap / 2.0;

                foreach (var b in Detect(bmp, new Rectangle(0, y, w, sh), s))
                {
                    double cy = (b.Top + b.Bottom) / 2;
                    if (cy < ownTop || cy >= ownBottom) continue;
                    var (text, conf) = Recognize(bmp, b, w, h);
                    text = text.Trim();
                    if (text.Length == 0 || (text.Length <= 2 && conf < 0.65)) continue;   // стрелки списков, иконки
                    words.Add(new OcrEngine.Word(text, b.Left, b.Top, b.Width, b.Height));
                }
                if (last) break;
            }
            return OcrEngine.Layout(words);
        }

        // ---------- поиск строк ----------

        private static readonly float[] Mean = { 0.485f * 255, 0.456f * 255, 0.406f * 255 };
        private static readonly float[] Std = { 0.229f * 255, 0.224f * 255, 0.225f * 255 };

        private static List<RectangleF> Detect(Bitmap bmp, Rectangle region, double s)
        {
            int W = Math.Max(32, (int)Math.Round(region.Width * s / 32) * 32);
            int H = Math.Max(32, (int)Math.Round(region.Height * s / 32) * 32);
            var px = Resample(bmp, region, W, H);
            int plane = W * H;
            var buf = new float[3 * plane];
            for (int i = 0, p = 0; p < plane; p++, i += 4)
            {
                buf[p] = (px[i + 2] - Mean[0]) / Std[0];               // R
                buf[plane + p] = (px[i + 1] - Mean[1]) / Std[1];       // G
                buf[2 * plane + p] = (px[i] - Mean[2]) / Std[2];       // B
            }
            var input = new DenseTensor<float>(buf, new[] { 1, 3, H, W });
            using var res = _det!.Run(new[] { NamedOnnxValue.CreateFromTensor(_det.InputMetadata.Keys.First(), input) });
            var prob = res.First().AsEnumerable<float>().ToArray();   // [1,1,H,W]

            // связные области выше порога: прямоугольник, средняя вероятность, расширение как в PaddleOCR (unclip 1.5)
            var boxes = new List<RectangleF>();
            var seen = new bool[plane];
            var stack = new Stack<int>();
            double kx = region.Width / (double)W, ky = region.Height / (double)H;
            for (int start = 0; start < plane; start++)
            {
                if (seen[start] || prob[start] <= 0.3f) continue;
                int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, n = 0;
                double sum = 0;
                seen[start] = true; stack.Push(start);
                while (stack.Count > 0)
                {
                    int q = stack.Pop(), qx = q % W, qy = q / W;
                    n++; sum += prob[q];
                    if (qx < x0) x0 = qx; if (qx > x1) x1 = qx; if (qy < y0) y0 = qy; if (qy > y1) y1 = qy;
                    if (qx > 0 && !seen[q - 1] && prob[q - 1] > 0.3f) { seen[q - 1] = true; stack.Push(q - 1); }
                    if (qx < W - 1 && !seen[q + 1] && prob[q + 1] > 0.3f) { seen[q + 1] = true; stack.Push(q + 1); }
                    if (qy > 0 && !seen[q - W] && prob[q - W] > 0.3f) { seen[q - W] = true; stack.Push(q - W); }
                    if (qy < H - 1 && !seen[q + W] && prob[q + W] > 0.3f) { seen[q + W] = true; stack.Push(q + W); }
                }
                int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
                if (sum / n < 0.6 || Math.Min(bw, bh) < 3) continue;
                double d = bw * bh * 1.5 / (2.0 * (bw + bh));
                double l = Math.Max(0, (x0 - d) * kx), t = Math.Max(0, (y0 - d) * ky);
                double r = Math.Min(region.Width, (x1 + 1 + d) * kx), b = Math.Min(region.Height, (y1 + 1 + d) * ky);
                boxes.Add(new RectangleF((float)(region.X + l), (float)(region.Y + t), (float)(r - l), (float)(b - t)));
            }
            return boxes;
        }

        // ---------- распознавание строки ----------

        private static (string Text, double Conf) Recognize(Bitmap bmp, RectangleF b, int w, int h)
        {
            int x0 = Math.Clamp((int)Math.Round(b.Left), 0, w - 1), y0 = Math.Clamp((int)Math.Round(b.Top), 0, h - 1);
            int x1 = Math.Clamp((int)Math.Round(b.Right), x0 + 1, w), y1 = Math.Clamp((int)Math.Round(b.Bottom), y0 + 1, h);
            int cw = x1 - x0, ch = y1 - y0;
            if (cw < 2 || ch < 2) return ("", 0);
            int tw = Math.Max(8, Math.Min(RecH * 60, (int)Math.Round(cw * (double)RecH / ch)));
            int W = Math.Max(64, (tw + 7) / 8 * 8);
            var px = Resample(bmp, new Rectangle(x0, y0, cw, ch), tw, RecH);

            var buf = new float[RecH * W * 3];   // справа — нули (серый), как в PaddleOCR
            for (int y = 0; y < RecH; y++)
                for (int x = 0; x < tw; x++)
                {
                    int i = (y * tw + x) * 4, o = (y * W + x) * 3;
                    buf[o] = (px[i + 2] - 127.5f) / 127.5f;
                    buf[o + 1] = (px[i + 1] - 127.5f) / 127.5f;
                    buf[o + 2] = (px[i] - 127.5f) / 127.5f;
                }
            var input = new DenseTensor<float>(buf, new[] { 1, RecH, W, 3 });
            using var res = _rec!.Run(new[] { NamedOnnxValue.CreateFromTensor(_rec.InputMetadata.Keys.First(), input) });
            var t = res.First().AsTensor<float>();
            int T = t.Dimensions[1], C = t.Dimensions[2];
            var o2 = t.ToArray();
            return Decode(o2, T, C);
        }

        // ---------- CTC и правка алфавита ----------

        private sealed class Dec { public string Text = ""; public double Score; public List<int> Steps = new(); public List<float> Conf = new(); }

        /// <summary>Жадное CTC-декодирование отрезка [t0,t1) с маской допустимых классов.</summary>
        private static Dec Greedy(float[] o, int C, int t0, int t1, Func<int, bool> ok)
        {
            var d = new Dec();
            var sb = new StringBuilder();
            int prev = 0;
            for (int t = t0; t < t1; t++)
            {
                int best = 0; float bp = -1;
                int baseI = t * C;
                for (int k = 0; k < C; k++)
                {
                    if (!ok(k)) continue;
                    float p = o[baseI + k];
                    if (p > bp) { bp = p; best = k; }
                }
                d.Score += Math.Log(Math.Max(bp, 1e-9f));
                if (best != 0 && best != prev && best - 1 < _keys.Length)
                {
                    var k = _keys[best - 1];
                    sb.Append(k.Length > 0 ? k : " ");
                    d.Conf.Add(bp); d.Steps.Add(t);
                }
                prev = best;
            }
            d.Text = sb.ToString();
            return d;
        }

        private static bool IsLat(char c) => c < 128 && char.IsLetter(c);
        private static bool IsCyr(char c) => Ru.IndexOf(c) >= 0;

        private static (string, double) Decode(float[] o, int T, int C)
        {
            if (C != _keys.Length + 1) throw new InvalidOperationException("модель и словарь OCR не совпадают");
            Func<int, bool> all = k => _allowed[k];
            Func<int, bool> latOnly = k => _allowed[k] && !_cyr[k];
            Func<int, bool> cyrOnly = k => _allowed[k] && !_lat[k];

            var d0 = Greedy(o, C, 0, T, all);
            string s = d0.Text;
            double conf = d0.Conf.Count > 0 ? d0.Conf.Average() : 0;
            if (s.Length == 0) return ("", 0);

            // токены: буквенные группы и всё остальное; у каждого — отрезок шагов CTC
            var toks = new List<(string Text, bool Word, int T0, int T1)>();
            for (int i = 0; i < s.Length;)
            {
                bool isw = char.IsLetter(s[i]);
                int j = i;
                while (j < s.Length && char.IsLetter(s[j]) == isw) j++;
                int t0 = i > 0 ? d0.Steps[i - 1] + 1 : 0;
                int t1 = j < s.Length ? d0.Steps[j] : T;
                toks.Add((s[i..j], isw, t0, t1));
                i = j;
            }
            static bool Single(string x) => x.Length > 0 && !x.Contains(' ');

            // 1) смесь алфавитов в слове — перечитать целиком одним алфавитом
            for (int k = 0; k < toks.Count; k++)
            {
                var tk = toks[k];
                if (!tk.Word) continue;
                bool L = tk.Text.Any(IsLat), Cy = tk.Text.Any(IsCyr);
                if (!(L && Cy)) continue;
                bool latDistinct = tk.Text.Any(c => IsLat(c) && Homo.IndexOf(c) < 0);
                bool cyrDistinct = tk.Text.Any(c => IsCyr(c) && Homo.IndexOf(c) < 0);
                var dl = Greedy(o, C, tk.T0, tk.T1, latOnly);
                var dc = Greedy(o, C, tk.T0, tk.T1, cyrOnly);
                // модель чаще ошибается в сторону кириллицы: явная латинская буква решает в пользу латиницы
                bool useLat = latDistinct || (!cyrDistinct && dl.Score >= dc.Score);
                var cand = (useLat ? dl.Text : dc.Text).Trim();
                if (Single(cand)) toks[k] = (cand, true, tk.T0, tk.T1);
            }

            // 2) «похожая» кириллица, приклеенная через - . _ / к латинскому слову (xray-соге → xray-core)
            bool LatWord(int k) => k >= 0 && k < toks.Count && toks[k].Word && toks[k].Text.All(IsLat);
            bool Joiner(int k) => k >= 0 && k < toks.Count && toks[k].Text.Length == 1 && "-._/".IndexOf(toks[k].Text[0]) >= 0;
            for (bool changed = true; changed;)
            {
                changed = false;
                for (int k = 0; k < toks.Count; k++)
                {
                    var tk = toks[k];
                    if (!tk.Word || !tk.Text.Any(IsCyr) || !tk.Text.All(c => Homo.IndexOf(c) >= 0 || Confusable.IndexOf(c) >= 0)) continue;
                    if (!((Joiner(k - 1) && LatWord(k - 2)) || (Joiner(k + 1) && LatWord(k + 2)))) continue;
                    var cand = Greedy(o, C, tk.T0, tk.T1, latOnly).Text.Trim();
                    if (Single(cand) && cand.All(IsLat)) { toks[k] = (cand, true, tk.T0, tk.T1); changed = true; }
                }
            }

            var outText = string.Concat(toks.Select(x => x.Text));
            outText = Regex.Replace(outText, "(?<=[A-Za-z])№|№(?=[A-Za-z])", "N");
            return (outText, conf);
        }

        // ---------- картинка ----------

        /// <summary>Кусок картинки, пересчитанный в W×H бикубикой (края без тёмной каймы); BGRA, непрозрачный, stride = W*4.</summary>
        private static byte[] Resample(Bitmap src, Rectangle r, int W, int H)
        {
            using var dst = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            using (var ia = new ImageAttributes())
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceCopy;
                ia.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(src, new Rectangle(0, 0, W, H), r.X, r.Y, r.Width, r.Height, GraphicsUnit.Pixel, ia);
            }
            var d = dst.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var px = new byte[W * H * 4];
            for (int y = 0; y < H; y++) Marshal.Copy(d.Scan0 + y * d.Stride, px, y * W * 4, W * 4);
            dst.UnlockBits(d);
            return px;
        }
    }
}
