using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace QScreen
{
    /// <summary>Окно верхнего уровня; все прямоугольники — физические пиксели экрана.</summary>
    public class WindowTarget
    {
        public IntPtr Hwnd;
        public string Title = "";
        public Rectangle Frame;        // DWMWA_EXTENDED_FRAME_BOUNDS (видимая рамка без невидимых полей)
        public Rectangle WindowRect;   // GetWindowRect (с невидимыми полями)
    }

    public static class WindowDetector
    {
        public static List<WindowTarget> GetVisibleWindows(Rectangle? onMonitor = null)
        {
            var list = new List<WindowTarget>();
            Win32.EnumWindows((hwnd, _) =>
            {
                if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd)) return true;
                if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                int ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
                if ((ex & Win32.WS_EX_TOOLWINDOW) != 0 && (ex & Win32.WS_EX_APPWINDOW) == 0) return true;

                Win32.GetWindowRect(hwnd, out var wr);
                if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.RECT fr, Marshal.SizeOf<Win32.RECT>()) != 0 || fr.Width <= 10) fr = wr;
                if (fr.Width <= 60 || fr.Height <= 60) return true;

                var sb = new StringBuilder(256);
                Win32.GetWindowText(hwnd, sb, 256);
                var title = sb.ToString().Trim();
                if (title == "Program Manager" || title.Length == 0) return true;

                var frame = Rectangle.FromLTRB(fr.Left, fr.Top, fr.Right, fr.Bottom);
                if (onMonitor.HasValue && !onMonitor.Value.IntersectsWith(frame)) return true;

                list.Add(new WindowTarget { Hwnd = hwnd, Title = title, Frame = frame, WindowRect = Rectangle.FromLTRB(wr.Left, wr.Top, wr.Right, wr.Bottom) });
                return true;
            }, IntPtr.Zero);
            return list; // порядок EnumWindows = сверху вниз по Z, первый подходящий = верхний
        }
    }

    public static class CaptureEngine
    {
        /// <summary>Снимок области экрана в физических пикселях (композиция DWM — со всеми слоями).</summary>
        public static Bitmap? Capture(Rectangle rect, bool sound = true)
        {
            if (rect.Width < 2 || rect.Height < 2) return null;
            var bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(rect.Left, rect.Top, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
            }
            if (sound) PlayShutterSound();
            return bmp;
        }

        /// <summary>Чистый снимок окна без перекрывающих окон (PrintWindow + PW_RENDERFULLCONTENT).</summary>
        public static Bitmap? CaptureWindow(WindowTarget t, Bitmap? screenFallback = null, Rectangle? fallbackOrigin = null)
        {
            try
            {
                var wr = t.WindowRect;
                if (wr.Width > 0 && wr.Height > 0)
                {
                    var full = new Bitmap(wr.Width, wr.Height, PixelFormat.Format32bppArgb);
                    bool ok;
                    using (var g = Graphics.FromImage(full))
                    {
                        IntPtr hdc = g.GetHdc();
                        ok = Win32.PrintWindow(t.Hwnd, hdc, Win32.PW_RENDERFULLCONTENT);
                        g.ReleaseHdc(hdc);
                    }
                    if (ok && !IsBlank(full))
                    {
                        // Обрезаем невидимые поля до видимой рамки
                        var crop = new Rectangle(t.Frame.X - wr.X, t.Frame.Y - wr.Y, t.Frame.Width, t.Frame.Height);
                        crop.Intersect(new Rectangle(0, 0, full.Width, full.Height));
                        if (crop.Width > 0 && crop.Height > 0)
                        {
                            var res = full.Clone(crop, PixelFormat.Format32bppArgb);
                            full.Dispose();
                            PlayShutterSound();
                            return res;
                        }
                    }
                    full.Dispose();
                }
            }
            catch { }

            // Фолбэк: вырезаем из замороженного снимка экрана либо снимаем заново
            if (screenFallback != null && fallbackOrigin.HasValue)
            {
                var r = t.Frame; r.Offset(-fallbackOrigin.Value.X, -fallbackOrigin.Value.Y);
                r.Intersect(new Rectangle(0, 0, screenFallback.Width, screenFallback.Height));
                if (r.Width > 1 && r.Height > 1) { PlayShutterSound(); return screenFallback.Clone(r, PixelFormat.Format32bppArgb); }
            }
            return Capture(t.Frame);
        }

        public static Bitmap? CaptureFullScreen()
        {
            var p = Win32.CursorPos();
            return Capture(Win32.MonitorRectFromPoint(p.X, p.Y));
        }

        private static bool IsBlank(Bitmap b)
        {
            // Быстрая проверка: PrintWindow иногда возвращает true и чёрный/пустой буфер (UWP/защищённые окна)
            int[] xs = { b.Width / 4, b.Width / 2, b.Width * 3 / 4 };
            int[] ys = { b.Height / 4, b.Height / 2, b.Height * 3 / 4 };
            foreach (var x in xs) foreach (var y in ys)
            {
                var c = b.GetPixel(x, y);
                if (c.A != 0 && (c.R | c.G | c.B) != 0) return false;
            }
            return true;
        }

        private static System.Media.SoundPlayer? _shutter;
        public static void PlayShutterSound()
        {
            try
            {
                if (_shutter == null)
                {
                    var p = Path.Combine(AppContext.BaseDirectory, "shutter.wav");
                    if (!File.Exists(p)) return;
                    _shutter = new System.Media.SoundPlayer(p);
                    _shutter.Load();
                }
                _shutter.Play();
            }
            catch { }
        }
    }

    public static class BitmapUtil
    {
        public static BitmapSource ToSource(Bitmap bmp)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var bs = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgra32, null, data.Scan0, data.Stride * bmp.Height, data.Stride);
                bs.Freeze();
                return bs;
            }
            finally { bmp.UnlockBits(data); }
        }

        public static Bitmap ToBitmap(BitmapSource src)
        {
            var conv = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            var bmp = new Bitmap(conv.PixelWidth, conv.PixelHeight, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { conv.CopyPixels(Int32Rect.Empty, data.Scan0, data.Stride * bmp.Height, data.Stride); }
            finally { bmp.UnlockBits(data); }
            return bmp;
        }

        public static byte[] GetBgra(BitmapSource src, out int width, out int height, out int stride)
        {
            var conv = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            width = conv.PixelWidth; height = conv.PixelHeight; stride = width * 4;
            var buf = new byte[stride * height];
            conv.CopyPixels(buf, stride, 0);
            return buf;
        }

        /// <summary>Аналог CIPixellate scale=16: блоки 16 физических пикселей.</summary>
        public static BitmapSource Pixellate(BitmapSource src, int block = 16)
        {
            using var bmp = ToBitmap(src);
            int sw = Math.Max(1, bmp.Width / block), sh = Math.Max(1, bmp.Height / block);
            using var small = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(bmp, new Rectangle(0, 0, sw, sh));
            }
            using var big = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(big))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(small, new Rectangle(0, 0, bmp.Width, bmp.Height));
            }
            return ToSource(big);
        }

        public static BitmapSource Crop(BitmapSource src, Int32Rect r)
        {
            r.X = Math.Max(0, r.X); r.Y = Math.Max(0, r.Y);
            r.Width = Math.Min(r.Width, src.PixelWidth - r.X);
            r.Height = Math.Min(r.Height, src.PixelHeight - r.Y);
            var c = new CroppedBitmap(src, r);
            var copy = new WriteableBitmap(c); // отвязываем от исходника
            copy.Freeze();
            return copy;
        }
    }
}
