using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using WinOcr = Windows.Media.Ocr.OcrEngine;

namespace QScreen
{
    /// <summary>
    /// Офлайн-OCR системным движком Windows. Пустая строка — текста нет; null — движок недоступен (нет языкового пакета).
    ///
    /// Почему не «как есть»:
    ///  • движок Windows однoязычный: русский читает латиницу как кириллицу (BACKUP → «вккир»), а части слов вообще не видит —
    ///    поэтому прогоняем и русский, и английский движки и по каждому слову выбираем вариант;
    ///  • мелкий экранный шрифт (11–13 px) он почти не читает — картинка увеличивается так, чтобы слова были ~36 px;
    ///  • строки он отдаёт по колонкам — собираем слова обратно в строки по их положению;
    ///  • длинный скролл-скрин режется на полосы с нахлёстом, каждое слово берётся из «своей» полосы — без повторов на стыках.
    /// </summary>
    public static class OcrEngine
    {
        private const double TargetWordH = 36;     // высота слова, на которой движок читает уверенно
        private const int Strip = 1200, Overlap = 120;   // полосы в пикселях исходника

        private readonly struct Word
        {
            public readonly string Text; public readonly double X, Y, W, H;
            public Word(string t, double x, double y, double w, double h) { Text = t; X = x; Y = y; W = w; H = h; }
            public double CY => Y + H / 2;
            public Word Shift(double dy) => new Word(Text, X, Y + dy, W, H);
        }

        public static string? ExtractText(BitmapSource image)
        {
            try
            {
                var (primary, english) = Engines();
                if (primary == null) return null;

                using var bmp = ToBitmap(image, out int w, out int h);
                uint max = WinOcr.MaxImageDimension;
                double s = PickScale(primary, bmp, w, h, max);

                var words = new List<Word>();
                for (int y = 0; y < h; y += Strip)
                {
                    int sh = Math.Min(Strip + Overlap, h - y);
                    bool last = y + sh >= h;
                    // слово принадлежит полосе, если его центр в «своей» зоне: стыки делятся посередине нахлёста
                    double ownTop = y == 0 ? double.MinValue : y + Overlap / 2.0;
                    double ownBottom = last ? double.MaxValue : y + Strip + Overlap / 2.0;

                    var (px, W, H) = Scale(bmp, y, w, sh, s);
                    var ru = Recognize(primary, px, W, H, s);
                    var en = english != null ? Recognize(english, px, W, H, s) : null;
                    foreach (var wd in Merge(ru, en))
                    {
                        var g = wd.Shift(y);
                        if (g.CY >= ownTop && g.CY < ownBottom) words.Add(g);
                    }
                    if (last) break;
                }
                return Layout(words);
            }
            catch { return null; }
        }

        // ---------- движки ----------

        public static bool HasEnglish => WinOcr.AvailableRecognizerLanguages.Any(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase));
        public static bool HasRussian => WinOcr.AvailableRecognizerLanguages.Any(l => l.LanguageTag.StartsWith("ru", StringComparison.OrdinalIgnoreCase));

        /// <summary>Поставить английский пакет OCR (компонент Windows; нужен UAC)</summary>
        public static void InstallEnglish()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe",
                    "-NoProfile -Command \"Add-WindowsCapability -Online -Name 'Language.OCR~~~en-US~0.0.1.0'\"")
                { Verb = "runas", UseShellExecute = true });
            }
            catch { }   // отказались в UAC
        }

        /// <summary>Основной (русский, если есть, иначе язык профиля) и английский — если основной не английский.</summary>
        private static (WinOcr? primary, WinOcr? english) Engines()
        {
            WinOcr? ByPrefix(string p)
            {
                var lang = WinOcr.AvailableRecognizerLanguages.FirstOrDefault(l => l.LanguageTag.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                return lang != null ? WinOcr.TryCreateFromLanguage(lang) : null;
            }
            var ru = ByPrefix("ru");
            var en = ByPrefix("en");
            var primary = ru ?? WinOcr.TryCreateFromUserProfileLanguages()
                          ?? WinOcr.AvailableRecognizerLanguages.Select(l => WinOcr.TryCreateFromLanguage(l)).FirstOrDefault(e => e != null);
            if (primary == null) return (null, null);
            bool primaryIsEn = primary.RecognizerLanguage.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase);
            return (primary, primaryIsEn ? null : en);
        }

        /// <summary>Масштаб: по медианной высоте слов на первой полосе. Мелкий шрифт увеличиваем до ~36 px (до ×4).</summary>
        private static double PickScale(WinOcr engine, Bitmap bmp, int w, int h, uint max)
        {
            double limit = (max - 1) / (double)w;                // шире лимита движка нельзя
            double s0 = Math.Min(1.0, limit);
            var (px, W, H) = Scale(bmp, 0, w, Math.Min(h, Strip + Overlap), s0);
            var hs = Recognize(engine, px, W, H, s0).Select(x => x.H).OrderBy(x => x).ToList();
            double s = hs.Count == 0 ? 3.0 : TargetWordH / hs[hs.Count / 2];   // ничего не нашёл — текст, скорее всего, совсем мелкий
            return Math.Min(limit, Math.Max(1.0, Math.Min(4.0, s)));
        }

        private static List<Word> Recognize(WinOcr engine, byte[] bgra, int w, int h, double s)
        {
            return Task.Run(async () =>
            {
                using var sb = SoftwareBitmap.CreateCopyFromBuffer(bgra.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
                var result = await engine.RecognizeAsync(sb);
                var list = new List<Word>();
                foreach (var line in result.Lines)
                    foreach (var wd in line.Words)
                    {
                        var r = wd.BoundingRect;
                        if (!string.IsNullOrWhiteSpace(wd.Text))
                            list.Add(new Word(wd.Text.Trim(), r.X / s, r.Y / s, r.Width / s, r.Height / s));   // в пикселях исходника
                    }
                return list;
            }).GetAwaiter().GetResult();
        }

        // ---------- картинка ----------

        private static Bitmap ToBitmap(BitmapSource src, out int w, out int h)
        {
            var px = BitmapUtil.GetBgra(src, out w, out h, out int stride);
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++) Marshal.Copy(px, y * stride, d.Scan0 + y * d.Stride, w * 4);
            bmp.UnlockBits(d);
            return bmp;
        }

        /// <summary>Полоса [y, y+sh) исходника, увеличенная в s раз бикубикой (края без тёмной каймы), непрозрачная.</summary>
        private static (byte[] px, int W, int H) Scale(Bitmap src, int y, int w, int sh, double s)
        {
            int W = Math.Max(1, (int)Math.Round(w * s)), H = Math.Max(1, (int)Math.Round(sh * s));
            using var dst = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            using (var ia = new ImageAttributes())
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.None;
                ia.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(src, new Rectangle(0, 0, W, H), 0, y, w, sh, GraphicsUnit.Pixel, ia);
            }
            var d = dst.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var px = new byte[W * H * 4];
            for (int r = 0; r < H; r++) Marshal.Copy(d.Scan0 + r * d.Stride, px, r * W * 4, W * 4);
            dst.UnlockBits(d);
            for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            return (px, W, H);
        }

        // ---------- русский + английский: выбор по слову ----------

        private enum Case { None, Lower, Upper, Title, Mixed }

        private static Case CaseOf(string t)
        {
            var letters = t.Where(char.IsLetter).ToArray();
            if (letters.Length == 0) return Case.None;
            if (letters.All(char.IsUpper)) return letters.Length == 1 ? Case.Title : Case.Upper;
            if (letters.All(char.IsLower)) return Case.Lower;
            if (char.IsUpper(letters[0]) && letters.Skip(1).All(char.IsLower)) return Case.Title;
            return Case.Mixed;
        }

        private static bool IsCyr(char c) => (c >= 'А' && c <= 'я') || c == 'Ё' || c == 'ё';
        private static bool IsLat(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

        /// <summary>Правдоподобное английское слово: только ASCII, в длинных словах есть гласные, регистр без хаоса.</summary>
        private static bool CleanEn(string t)
        {
            if (t.Any(c => c > 127)) return false;
            var l = t.Where(char.IsLetter).ToArray();
            if (l.Length == 0) return t.Any(char.IsDigit);
            if (l.Length >= 4 && !l.Any(c => "aeiouyAEIOUY".IndexOf(c) >= 0)) return false;
            return true;
        }

        /// <summary>Правдоподобное русское слово: буквы только кириллица, в длинных — гласные, регистр без хаоса.</summary>
        private static bool CleanRu(string t)
        {
            var l = t.Where(char.IsLetter).ToArray();
            if (l.Length == 0) return true;
            if (!l.All(IsCyr)) return false;
            if (l.Length >= 4 && !l.Any(c => "аеёиоуыэюяАЕЁИОУЫЭЮЯ".IndexOf(c) >= 0)) return false;
            return CaseOf(t) != Case.Mixed;
        }

        private const string CyrLook = "АВЕКМНОРСТХУаеорсухіІ", LatLook = "ABEKMHOPCTXYaeopcyxiI";
        private static string Translit(string t) => new string(t.Select(c => { int i = CyrLook.IndexOf(c); return i >= 0 ? LatLook[i] : c; }).ToArray());

        /// <summary>По одному месту на картинке два прочтения — какое верное.</summary>
        private static bool PreferEn(string ru, string en)
        {
            if (!CleanEn(en)) return false;
            if (!ru.Any(IsCyr)) return false;                                         // русский движок сам прочёл латиницу/цифры
            if (string.Equals(Translit(ru), en, StringComparison.OrdinalIgnoreCase)) return true;   // «ХРОN» → «XPON»
            if (ru.Any(IsLat)) return true;                                           // каша из двух алфавитов в одном слове
            if (!CleanRu(ru)) return true;
            // латинские капсы русский движок читает строчной кириллицей (BACKUP → «вккир», CPH → «сен»);
            // настоящие русские капсы он видит капсами — тогда регистры совпадают и остаётся русское
            return CaseOf(en) == Case.Upper && CaseOf(ru) != Case.Upper;
        }

        private static double Inter(Word a, Word b)
        {
            double w = Math.Min(a.X + a.W, b.X + b.W) - Math.Max(a.X, b.X);
            double h = Math.Min(a.Y + a.H, b.Y + b.H) - Math.Max(a.Y, b.Y);
            return w > 0 && h > 0 ? w * h : 0;
        }

        private static List<Word> Merge(List<Word> ru, List<Word>? en)
        {
            if (en == null || en.Count == 0) return ru;
            var res = new List<Word>(ru.Count + 8);
            var touched = new bool[en.Count];
            foreach (var r in ru)
            {
                int best = -1; double bestK = 0;
                for (int i = 0; i < en.Count; i++)
                {
                    double inter = Inter(r, en[i]);
                    if (inter <= 0) continue;
                    if (inter / Math.Min(r.W * r.H, en[i].W * en[i].H) > 0.3) touched[i] = true;
                    double k = inter / Math.Max(r.W * r.H, en[i].W * en[i].H);       // похожие рамки — одно и то же слово
                    if (k > bestK) { bestK = k; best = i; }
                }
                res.Add(best >= 0 && bestK >= 0.5 && PreferEn(r.Text, en[best].Text) ? en[best] : r);
            }
            // что русский движок не увидел вовсе (FORTEX, WG, цифры): берём английское, если похоже на настоящее
            for (int i = 0; i < en.Count; i++)
            {
                if (touched[i]) continue;
                var t = en[i].Text;
                if (CleanEn(t) && (t.Any(char.IsDigit) || t.Any(char.IsUpper))) res.Add(en[i]);
            }
            return res;
        }

        // ---------- раскладка ----------

        /// <summary>Слова → строки по вертикали, внутри строки — слева направо; большой разрыв (колонки) — 4 пробела.</summary>
        private static string Layout(List<Word> words)
        {
            if (words.Count == 0) return "";
            var rows = new List<List<Word>>();
            double top = 0, bottom = 0;
            foreach (var wd in words.OrderBy(x => x.CY))
            {
                if (rows.Count > 0)
                {
                    double ov = Math.Min(bottom, wd.Y + wd.H) - Math.Max(top, wd.Y);
                    if (ov >= 0.5 * Math.Min(bottom - top, wd.H))
                    {
                        rows[^1].Add(wd);
                        top = Math.Min(top, wd.Y); bottom = Math.Max(bottom, wd.Y + wd.H);
                        continue;
                    }
                }
                rows.Add(new List<Word> { wd });
                top = wd.Y; bottom = wd.Y + wd.H;
            }

            var sb = new StringBuilder();
            foreach (var row in rows)
            {
                var r = row.OrderBy(x => x.X).ToList();
                double hRef = r.Select(x => x.H).OrderBy(x => x).ElementAt(r.Count / 2);
                sb.Append(r[0].Text);
                for (int i = 1; i < r.Count; i++)
                {
                    double gap = r[i].X - (r[i - 1].X + r[i - 1].W);
                    sb.Append(gap > 1.2 * hRef ? "    " : " ").Append(r[i].Text);
                }
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd();
        }
    }
}
