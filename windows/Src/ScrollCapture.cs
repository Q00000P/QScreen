using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace QScreen
{
    /// <summary>
    /// Склейка скролл-кадров (порт мак-версии).
    /// 1) Закреплённые шапка/подвал (строки, не изменившиеся между кадрами на тех же местах) отсекаются:
    ///    шапка берётся один раз из первого кадра, подвал — из последнего. Раньше они повторялись на каждом стыке.
    /// 2) Сдвиг ищется по сигнатурам строк (64 усреднённых столбца, без полосы прокрутки) и только по «информативным»
    ///    строкам — пустые строки текстовых страниц совпадают с чем угодно и сбивали прежний поиск.
    /// 3) Новые строки копируются из кадров байт-в-байт.
    /// </summary>
    public sealed class ScrollStitchEngine
    {
        public enum Result { First, Identical, NoMatch, Appended }

        private const int Bins = 64;
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int FrameCount { get; private set; }
        public int LastDy { get; private set; }
        private byte[] _prevPx = Array.Empty<byte>();
        private float[] _prevSig = Array.Empty<float>();
        private readonly List<byte[]> _strips = new();
        private int _outRows;
        private int _lastBottom;

        public int ResultHeight => FrameCount <= 1 ? Height : _outRows + _lastBottom;

        /// <param name="requireMatch">авто-режим: без надёжного совпадения кадр не добавляется (лучше пропуск, чем дубль)</param>
        public Result Add(Bitmap frame, int? expectedDy, bool requireMatch, bool relaxed = false)
        {
            var px = Pixels(frame);
            if (FrameCount == 0)
            {
                Width = frame.Width; Height = frame.Height;
                _prevPx = px; _prevSig = Signatures(px);
                FrameCount = 1;
                return Result.First;
            }
            if (frame.Width != Width || frame.Height != Height) return Result.NoMatch;
            var sig = Signatures(px);
            if (IsIdentical(_prevSig, sig)) return Result.Identical;

            int top = StaticTop(_prevSig, sig);
            int bot = StaticBottom(_prevSig, sig, top);
            int region = Height - top - bot;
            if (region <= 16) return Result.Identical;

            int? found = FindOffset(_prevSig, sig, top, bot, expectedDy, MovingBins(_prevSig, sig), relaxed ? 6.0f : 3.0f);
            if (found == null)
            {
                if (requireMatch) return Result.NoMatch;
                found = region;   // ручной режим, прокрутили больше чем на экран — стык без нахлёста
            }
            int d = Math.Min(found.Value, region);

            if (FrameCount == 1) AppendRows(_prevPx, 0, Height - bot);   // шапка + первый экран, без подвала
            AppendRows(px, Height - bot - d, Height - bot);               // только новые строки
            _prevPx = px; _prevSig = sig; _lastBottom = bot; LastDy = d;
            FrameCount++;
            return Result.Appended;
        }

        public Bitmap? Finish()
        {
            if (FrameCount == 0) return null;
            int rowBytes = Width * 4;
            int h = FrameCount == 1 ? Height : _outRows + _lastBottom;
            var bmp = new Bitmap(Width, h, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, Width, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int y = 0;
                void Put(byte[] src, int offset, int rows)
                {
                    for (int r = 0; r < rows; r++)
                        System.Runtime.InteropServices.Marshal.Copy(src, offset + r * rowBytes, data.Scan0 + (y + r) * data.Stride, rowBytes);
                    y += rows;
                }
                if (FrameCount == 1) Put(_prevPx, 0, Height);
                else
                {
                    foreach (var s in _strips) Put(s, 0, s.Length / rowBytes);
                    if (_lastBottom > 0) Put(_prevPx, (Height - _lastBottom) * rowBytes, _lastBottom);   // подвал — один раз
                }
            }
            finally { bmp.UnlockBits(data); }
            return bmp;
        }

        /// <summary>Картинка «успокоилась» (плавная прокрутка, подгрузка)</summary>
        public static bool RoughlyEqual(Bitmap a, Bitmap b)
        {
            if (a.Width != b.Width || a.Height != b.Height) return false;
            const int w = 48;
            int h = Math.Max(8, Math.Min(256, a.Height * w / Math.Max(1, a.Width)));
            using var ta = new Bitmap(a, w, h);
            using var tb = new Bitmap(b, w, h);
            long diff = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var ca = ta.GetPixel(x, y); var cb = tb.GetPixel(x, y);
                    diff += Math.Abs(ca.R - cb.R) + Math.Abs(ca.G - cb.G) + Math.Abs(ca.B - cb.B);
                }
            return diff / (double)(w * h * 3) < 0.6;
        }

        // ---------- внутреннее ----------

        private void AppendRows(byte[] px, int y0, int y1)
        {
            if (y1 <= y0) return;
            int rowBytes = Width * 4;
            var strip = new byte[(y1 - y0) * rowBytes];
            Buffer.BlockCopy(px, y0 * rowBytes, strip, 0, strip.Length);
            _strips.Add(strip);
            _outRows += y1 - y0;
        }

        private static byte[] Pixels(Bitmap bmp)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = bmp.Width * 4;
                var buf = new byte[rowBytes * bmp.Height];
                for (int y = 0; y < bmp.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, buf, y * rowBytes, rowBytes);
                return buf;
            }
            finally { bmp.UnlockBits(data); }
        }

        /// <summary>Сигнатура строки: 64 средних яркости; поля по 2% слева и 3% справа (полоса прокрутки) не учитываются</summary>
        private float[] Signatures(byte[] px)
        {
            var sig = new float[Height * Bins];
            int x0 = Width * 2 / 100, x1 = Math.Max(x0 + Bins, Width - Math.Max(Width * 3 / 100, 12));
            int span = x1 - x0;
            for (int y = 0; y < Height; y++)
            {
                int row = y * Width * 4;
                for (int b = 0; b < Bins; b++)
                {
                    int a = x0 + span * b / Bins, e = x0 + span * (b + 1) / Bins;
                    int sum = 0, n = 0;
                    for (int x = a; x < e; x += 2)
                    {
                        int i = row + x * 4;
                        sum += (px[i] * 29 + px[i + 1] * 150 + px[i + 2] * 77) >> 8;   // BGRA → Y
                        n++;
                    }
                    sig[y * Bins + b] = n > 0 ? (float)sum / n : 0;
                }
            }
            return sig;
        }

        /// <summary>Строка не изменилась: допускаем до 10% «живых» столбцов (анимация, индикатор, эквалайзер в шапке)</summary>
        private static bool RowStatic(float[] a, float[] b, int y)
        {
            int i = y * Bins, n = 0;
            for (int k = 0; k < Bins; k++)
                if (Math.Abs(a[i + k] - b[i + k]) > 4 && ++n > Bins / 10) return false;
            return true;
        }

        /// <summary>Средняя разница строк только по «подвижным» столбцам</summary>
        private static float RowDiff(float[] a, int ya, float[] b, int yb, int[] bins)
        {
            float d = 0; int ia = ya * Bins, ib = yb * Bins;
            foreach (var k in bins) d += Math.Abs(a[ia + k] - b[ib + k]);
            return d / bins.Length;
        }

        /// <summary>Столбцы, которые между кадрами почти нигде не поменялись, — закреплённые боковые панели или пустые поля.
        /// В поиске сдвига они только мешают: у правильного dy они «не совпадают», потому что не уехали вместе с контентом.</summary>
        private int[] MovingBins(float[] a, float[] b)
        {
            var same = new int[Bins]; int rows = 0;
            for (int y = 0; y < Height; y += 2)
            {
                rows++;
                for (int k = 0; k < Bins; k++) if (Math.Abs(a[y * Bins + k] - b[y * Bins + k]) < 1.5f) same[k]++;
            }
            var moving = new List<int>();
            for (int k = 0; k < Bins; k++) if (same[k] < rows * 0.92) moving.Add(k);
            if (moving.Count < 8) { moving.Clear(); for (int k = 0; k < Bins; k++) moving.Add(k); }
            return moving.ToArray();
        }

        private bool IsIdentical(float[] a, float[] b)
        {
            int total = 0, same = 0;
            for (int y = 0; y < Height; y += 2) { total++; if (RowStatic(a, b, y)) same++; }
            return same >= total * 0.97;   // анимация в паре мест не считается движением
        }

        private int StaticTop(float[] a, float[] b)
        {
            int y = 0;
            while (y < Height / 3 && RowStatic(a, b, y)) y++;
            return y;
        }

        private int StaticBottom(float[] a, float[] b, int top)
        {
            int n = 0;
            while (n < Height / 3 && Height - 1 - n > top && RowStatic(a, b, Height - 1 - n)) n++;
            return n;
        }

        /// <summary>Сдвиг dy: строка y нового кадра == строка y+dy прошлого (контент уехал вверх на dy).
        /// Оценка — усечённое среднее (лучшие 80% строк): анимации, подгружающиеся картинки и реклама не ломают стык.</summary>
        private int? FindOffset(float[] prev, float[] cur, int top, int bottom, int? expected, int[] bins, float threshold)
        {
            int n = Height - top - bottom;
            int minOverlap = Math.Max(24, n / 10);
            if (n - minOverlap < 1) return null;

            var info = new List<int>();
            for (int k = 0; k < n; k++)
            {
                int bs = (top + k) * Bins; float lo = 255, hi = 0;
                foreach (var b in bins) { float v = cur[bs + b]; if (v < lo) lo = v; if (v > hi) hi = v; }
                if (hi - lo > 10) info.Add(k);
            }
            if (info.Count < 12) { info.Clear(); for (int k = 0; k < n; k += 2) info.Add(k); }

            int bestDy = -1; float bestCost = float.MaxValue;
            int valid = info.Count;
            var diffs = new List<float>(160);
            for (int dy = 1; dy <= n - minOverlap; dy++)
            {
                int m = n - dy;
                while (valid > 0 && info[valid - 1] >= m) valid--;
                if (valid < 8) continue;
                int step = Math.Max(1, valid / 140);
                diffs.Clear();
                for (int i = 0; i < valid; i += step) diffs.Add(RowDiff(prev, top + dy + info[i], cur, top + info[i], bins));
                diffs.Sort();
                int keep = Math.Max(1, diffs.Count * 4 / 5);
                float sum = 0; for (int j = 0; j < keep; j++) sum += diffs[j];
                float cost = sum / keep;
                if (expected.HasValue) cost += Math.Abs(dy - expected.Value) * 0.0015f;   // при равенстве — ближе к ожидаемому шагу
                if (cost < bestCost) { bestCost = cost; bestDy = dy; }
            }
            return bestDy > 0 && bestCost < threshold ? bestDy : null;
        }
    }

    /// <summary>Скролл-захват: авто-прокрутка колесом до конца страницы со склейкой; ручной «+ Кадр» как запасной путь.</summary>
    public static class ScrollCaptureManager
    {
        private static ScrollPanelWindow? _panel;
        private static Rectangle _target;
        private static double _scale = 1.0;
        private static ScrollStitchEngine _engine = new();
        private static Action<BitmapSource>? _onFinished;
        private static int _session;
        private static bool _running;
        private static IntPtr _zoomedHwnd;          // окно, которому увеличили масштаб для чёткого текста
        private const int ZoomSteps = 5;            // Chrome/Edge: 100% → 200%

        /// <summary>Увести курсор на свою панель: браузер снимает hover и прячет строку со ссылкой внизу окна,
        /// которая иначе попадала в склейку</summary>
        private static void ParkCursor()
        {
            if (_panel == null) return;
            var h = new System.Windows.Interop.WindowInteropHelper(_panel).Handle;
            if (h != IntPtr.Zero && Win32.GetWindowRect(h, out var r)) Win32.SetCursorPos((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        }

        public static bool IsActive => _panel != null;
        public static bool IsRunning => _running;

        public static void StartSession(Rectangle target, double scale, Action<BitmapSource> onComplete)
        {
            Cancel();
            _session++;
            _target = target; _scale = scale; _onFinished = onComplete;
            _engine = new ScrollStitchEngine();
            _panel = new ScrollPanelWindow(target);
            _panel.Show();
            RunAuto();   // по умолчанию — сам
        }

        /// <summary>Ручной кадр</summary>
        public static void CaptureCurrentFrame()
        {
            if (_running) return;
            using var bmp = CaptureEngine.Capture(_target);
            if (bmp == null) return;
            _engine.Add(bmp, null, requireMatch: false);
            UpdatePanel();
        }

        /// <summary>Авто: крутим на ~60% высоты, ждём, пока картинка успокоится, клеим; конец — два шага без движения</summary>
        public static async void RunAuto()
        {
            if (_running || _panel == null) return;
            int token = _session;
            _running = true;
            UpdatePanel();
            try
            {
                var center = new System.Drawing.Point(_target.X + _target.Width / 2, _target.Y + _target.Height / 2);
                int notches = 3;                 // шагов колеса за раз; калибруется по первому сдвигу
                double pxPerNotch = 0;
                int direction = -1;              // -1 = вниз по странице
                bool directionChecked = _engine.FrameCount > 1;

                // «Чёткий текст»: увеличиваем масштаб страницы — текст рисуется реальными пикселями, а не растягивается
                if (_engine.FrameCount == 0 && AppSettings.ScrollHiDPI)
                {
                    _zoomedHwnd = Win32.ActivateWindowAt(center);
                    if (_zoomedHwnd != IntPtr.Zero)
                    {
                        await Task.Delay(250);
                        for (int i = 0; i < ZoomSteps; i++) { Win32.ZoomKey(true); await Task.Delay(90); }
                        await Task.Delay(700);   // перерисовка в новом масштабе
                        if (token != _session) return;
                    }
                }
                ParkCursor();
                int still = 0;

                if (_engine.FrameCount == 0)
                {
                    using var first = await CaptureSettled(token);
                    if (first == null || token != _session) return;
                    _engine.Add(first, null, requireMatch: true);
                    UpdatePanel();
                }

                int misses = 0;
                while (token == _session && _running && _engine.FrameCount < 200 && _engine.ResultHeight < 60000)
                {
                    if (misses == 0)
                    {
                        Win32.Wheel(center, direction * notches);
                        await Task.Delay(60);
                        ParkCursor();          // без курсора над ссылками
                        await Task.Delay(90);
                    }
                    else await Task.Delay(300);   // стык не нашёлся — обычно догружаются картинки/реклама: переснимаем то же место

                    using var img = await CaptureSettled(token);
                    if (img == null || token != _session || !_running) return;

                    int? expected = pxPerNotch > 0 ? (int)(pxPerNotch * notches) : null;
                    var r = _engine.Add(img, expected, requireMatch: true, relaxed: misses >= 2);

                    if (r == ScrollStitchEngine.Result.Appended)
                    {
                        directionChecked = true; still = 0; misses = 0;
                        if (pxPerNotch <= 0)
                        {
                            pxPerNotch = _engine.LastDy / (double)notches;
                            if (pxPerNotch > 0) notches = Math.Clamp((int)Math.Round(_target.Height * 0.6 / pxPerNotch), 1, 30);
                        }
                    }
                    else if (r == ScrollStitchEngine.Result.Identical)
                    {
                        misses = 0;
                        if (!directionChecked)
                        {
                            // первый шаг страницу не сдвинул — пробуем в другую сторону
                            directionChecked = true;
                            direction = -direction;
                            continue;
                        }
                        still++;                        // конец страницы — только когда реально ничего не движется
                    }
                    else
                    {
                        misses++;
                        if (misses > 3) still = 2;      // стык так и не нашёлся — заканчиваем на склеенном
                    }
                    UpdatePanel();
                    if (still >= 2) break;
                }
            }
            finally
            {
                if (token == _session) { _running = false; UpdatePanel(); }
            }
            if (token == _session) Finish();
        }

        private static async Task<Bitmap?> CaptureSettled(int token)
        {
            var last = CaptureEngine.Capture(_target, sound: false);
            for (int i = 0; i < 6 && last != null; i++)
            {
                if (token != _session) { last.Dispose(); return null; }
                await Task.Delay(90);
                var next = CaptureEngine.Capture(_target, sound: false);
                if (next == null) return last;
                bool eq = ScrollStitchEngine.RoughlyEqual(last, next);
                last.Dispose();
                last = next;
                if (eq) break;
            }
            return last;
        }

        private static void UpdatePanel() => _panel?.SetState(_engine.FrameCount, _engine.ResultHeight, _running);

        /// <summary>Готово / Стоп / Esc — склеить то, что есть</summary>
        public static async void Finish()
        {
            var cb = _onFinished;
            _running = false;
            _session++;
            ClosePanel();
            using var stitched = _engine.Finish();
            _engine = new ScrollStitchEngine();
            await RestoreZoom();   // сначала вернуть масштаб странице, потом открыть редактор поверх
            if (stitched != null)
            {
                CaptureEngine.PlayShutterSound();
                cb?.Invoke(OverlayManager.Tag(BitmapUtil.ToSource(stitched), _scale));
            }
        }

        private static async Task RestoreZoom()
        {
            var h = _zoomedHwnd;
            if (h == IntPtr.Zero) return;
            _zoomedHwnd = IntPtr.Zero;
            Win32.SetForegroundWindow(h);
            await Task.Delay(200);
            for (int i = 0; i < ZoomSteps; i++) { Win32.ZoomKey(false); await Task.Delay(90); }
        }

        public static void Cancel()
        {
            _ = RestoreZoom();
            _running = false;
            _session++;
            ClosePanel();
            _engine = new ScrollStitchEngine();
            _onFinished = null;
        }

        private static void ClosePanel() { _panel?.Close(); _panel = null; }
    }

    internal sealed class ScrollPanelWindow : Window
    {
        private const int HK_ESC = 0x5153;   // «QS»
        private readonly TextBlock _title, _height, _hint;
        private readonly System.Windows.Shapes.Ellipse _dot;
        private readonly StackPanel _btns;

        public ScrollPanelWindow(Rectangle target)
        {
            const int W = 300, H = 120;
            Title = "Скролл-скриншот";
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
            AllowsTransparency = true; Background = Brushes.Transparent;
            SizeToContent = SizeToContent.WidthAndHeight;
            Win32.MakeNonActivating(this);

            // Панель — сбоку от области, чтобы не попасть под курсор прокрутки и в кадр
            var scale = Win32.ScaleForPoint(target.X, target.Y);
            var wa = Win32.MonitorWorkAreaFromPoint(target.X, target.Y);
            int pw = (int)(W * scale), ph = (int)(H * scale);
            int px = target.Right + 15;
            if (px + pw > wa.Right - 10) px = target.Left - pw - 15;
            px = Math.Min(wa.Right - pw - 10, Math.Max(wa.Left + 10, px));
            int py = (int)Math.Max(wa.Top + 20, Math.Min(wa.Bottom - ph - 20, target.Y + target.Height / 2));
            WindowStartupLocation = WindowStartupLocation.Manual;
            SourceInitialized += (s, e) =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, px, py, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
                // Esc — закончить (на время сессии; окно не активируется, поэтому глобальный хоткей)
                Win32.RegisterHotKey(hwnd, HK_ESC, Win32.MOD_NOREPEAT, 0x1B);
                System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
                {
                    if (msg == Win32.WM_HOTKEY && wp.ToInt32() == HK_ESC) { handled = true; Dispatcher.BeginInvoke(new Action(ScrollCaptureManager.Finish)); }
                    return IntPtr.Zero;
                });
            };
            Closed += (s, e) => Win32.UnregisterHotKey(new System.Windows.Interop.WindowInteropHelper(this).Handle, HK_ESC);

            var root = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(31, 33, 38)), CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Width = W,
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), BorderThickness = new Thickness(1)
            };
            var stack = new StackPanel();
            var head = new DockPanel();
            _dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = Brushes.LimeGreen, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            _title = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Bold };
            _height = new TextBlock { Foreground = Brushes.Gray, FontSize = 10, FontFamily = new System.Windows.Media.FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(_height, Dock.Right);
            head.Children.Add(_height);
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(_dot); left.Children.Add(_title);
            head.Children.Add(left);
            stack.Children.Add(head);
            _hint = new TextBlock { Foreground = Brushes.Gray, FontSize = 10, Margin = new Thickness(0, 6, 0, 8), TextWrapping = TextWrapping.Wrap };
            stack.Children.Add(_hint);
            _btns = new StackPanel { Orientation = Orientation.Horizontal };
            stack.Children.Add(_btns);
            root.Child = stack;
            Content = root;
            SetState(0, 0, true);
        }

        public void SetState(int frames, int heightPx, bool running)
        {
            _dot.Fill = running ? Ui.Red : Brushes.LimeGreen;
            _title.Text = running ? "Авто-прокрутка…" : $"Кадров: {frames}";
            _height.Text = $"{heightPx} px";
            _hint.Text = running ? "Прокручиваю и склеиваю сам. Esc или «Стоп» — закончить здесь."
                                 : "«Авто» — прокрутить до конца самому, или вручную: прокрутите и «+ Кадр».";
            _btns.Children.Clear();
            if (running)
                _btns.Children.Add(Ui.MakeButton("■ Стоп", Ui.Red, () => ScrollCaptureManager.Finish()));
            else
            {
                _btns.Children.Add(Ui.MakeButton("▶ Авто", new SolidColorBrush(Color.FromRgb(175, 82, 222)), () => ScrollCaptureManager.RunAuto()));
                _btns.Children.Add(Ui.MakeButton("＋ Кадр", Ui.Blue, () => ScrollCaptureManager.CaptureCurrentFrame()));
                _btns.Children.Add(Ui.MakeButton("✓ Готово", Ui.Green, () => ScrollCaptureManager.Finish()));
            }
            _btns.Children.Add(Ui.MakeButton("Отмена", Brushes.Transparent, () => ScrollCaptureManager.Cancel(), Brushes.Gray));
        }
    }

    /// <summary>Общие HUD-элементы в стиле мак-версии.</summary>
    public static class Ui
    {
        public static readonly SolidColorBrush Panel = new(Color.FromRgb(31, 33, 38));
        public static readonly SolidColorBrush PanelDark = new(Color.FromRgb(20, 23, 26));
        public static readonly SolidColorBrush Blue = new(Color.FromRgb(0, 122, 255));
        public static readonly SolidColorBrush Green = new(Color.FromRgb(52, 199, 89));
        public static readonly SolidColorBrush Red = new(Color.FromRgb(255, 59, 48));
        public static readonly SolidColorBrush Ghost = new(Color.FromArgb(31, 255, 255, 255));

        public static Button MakeButton(string text, System.Windows.Media.Brush bg, Action onClick, System.Windows.Media.Brush? fg = null, string? tooltip = null, double? width = null)
        {
            var b = new Button
            {
                Content = text, Background = bg, Foreground = fg ?? Brushes.White, BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 0), FontSize = 11, FontWeight = FontWeights.Bold,
                Cursor = System.Windows.Input.Cursors.Hand, Height = 26, Focusable = false, ToolTip = tooltip
            };
            if (width.HasValue) b.Width = width.Value;
            b.Template = FlatTemplate();
            b.Click += (s, e) => onClick();
            return b;
        }

        public static ControlTemplate FlatTemplate()
        {
            var t = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);
            t.VisualTree = border;
            var trig = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            trig.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85));
            t.Triggers.Add(trig);
            return t;
        }

        public static Button IconButton(string glyph, Action onClick, string? tooltip = null, System.Windows.Media.Brush? fg = null)
        {
            var b = MakeButton(glyph, Ghost, onClick, fg, tooltip, 28);
            b.Padding = new Thickness(0);
            b.FontFamily = new System.Windows.Media.FontFamily("Segoe UI Emoji, Segoe UI Symbol, Segoe UI");
            b.FontSize = 13;
            return b;
        }
    }
}
