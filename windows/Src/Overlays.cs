using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using Cursors = System.Windows.Input.Cursors;

namespace QScreen
{
    public enum OverlayMode { Area, Scroll, Record, Smart }

    /// <summary>Полноэкранный оверлей на мониторе под курсором. Фон — замороженный снимок экрана, всё в физических пикселях.</summary>
    public sealed class OverlayWindow : Window
    {
        public readonly Rectangle Monitor;
        public readonly Bitmap Frozen;
        public readonly OverlayMode Mode;
        public Action<Rectangle>? OnRect;
        public Action<WindowTarget>? OnWindow;
        public Action? OnCancel;

        private readonly BitmapSource _bg;
        private readonly OverlayCanvas _canvas;
        private double _scale = 1.0;
        private bool _done;

        public OverlayWindow(Rectangle monitor, Bitmap frozen, OverlayMode mode, List<WindowTarget>? windows)
        {
            Monitor = monitor; Frozen = frozen; Mode = mode;
            _bg = BitmapUtil.ToSource(frozen);
            _scale = Win32.ScaleForPoint(monitor.X + monitor.Width / 2, monitor.Y + monitor.Height / 2);

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            AllowsTransparency = false;
            Background = Brushes.Black;
            Cursor = Cursors.None;
            WindowStartupLocation = WindowStartupLocation.Manual;
            SizeToContent = SizeToContent.Manual;
            Left = monitor.X; Top = monitor.Y; Width = monitor.Width / _scale; Height = monitor.Height / _scale;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;

            _canvas = new OverlayCanvas(this, _bg, mode, windows ?? new List<WindowTarget>());
            Content = _canvas;

            SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
                Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, ex | Win32.WS_EX_TOOLWINDOW);
                // Хук ДО первого SetWindowPos: WM_DPICHANGED глотаем, иначе WPF пересчитает окно по своему «рекомендованному» rect
                HwndSource.FromHwnd(hwnd)?.AddHook(WndHook);
                Win32.PlaceWindowPhysical(this, Monitor, true);
            };
            Loaded += (s, e) => Dispatcher.BeginInvoke(new Action(() => { Place(0); Activate(); _canvas.Focus(); }), System.Windows.Threading.DispatcherPriority.Loaded);
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Cancel(); };
            Deactivated += (s, e) => { if (!_done && IsVisible) Activate(); };
        }

        private IntPtr WndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Win32.WM_DPICHANGED)
            {
                handled = true; // остаёмся в DPI создания; физический rect держим сами
                Dispatcher.BeginInvoke(new Action(() => Place(0)), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            return IntPtr.Zero;
        }

        /// <summary>Ставит окно в физ. rect монитора и проверяет результат (до 3 попыток), пишет в лог.</summary>
        private void Place(int attempt)
        {
            if (_done) return;
            Win32.PlaceWindowPhysical(this, Monitor, true);
            RefreshScale();
            var hwnd = new WindowInteropHelper(this).Handle;
            Win32.GetWindowRect(hwnd, out var r);
            bool ok = r.Left == Monitor.X && r.Top == Monitor.Y && r.Width == Monitor.Width && r.Height == Monitor.Height;
            Program.Trace($"overlay place#{attempt} target={Monitor} actual={r.Left},{r.Top} {r.Width}x{r.Height} scale={_scale:0.###} dip={ActualWidth:0}x{ActualHeight:0} ok={ok}");
            if (!ok && attempt < 3)
                Dispatcher.BeginInvoke(new Action(() => Place(attempt + 1)), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void RefreshScale()
        {
            // Реальная матрица рендера этого окна (после проглоченного WM_DPICHANGED она НЕ меняется — это и нужно)
            var src = PresentationSource.FromVisual(this);
            var m = src?.CompositionTarget?.TransformToDevice.M11 ?? 0;
            if (m > 0) _scale = m;
            _canvas.InvalidateVisual();
        }

        public double Scale => _scale;

        public System.Drawing.Point ToPhysical(Point dip) =>
            new System.Drawing.Point(Monitor.X + (int)Math.Round(dip.X * _scale), Monitor.Y + (int)Math.Round(dip.Y * _scale));

        public Rect ToDip(Rectangle phys) =>
            new Rect((phys.X - Monitor.X) / _scale, (phys.Y - Monitor.Y) / _scale, phys.Width / _scale, phys.Height / _scale);

        public void FinishRect(Rectangle physRect)
        {
            if (_done) return; _done = true;
            Hide();
            OnRect?.Invoke(physRect);
            Close();
        }

        public void FinishWindow(WindowTarget t)
        {
            if (_done) return; _done = true;
            Hide();
            OnWindow?.Invoke(t);
            Close();
        }

        public void Cancel()
        {
            if (_done) return; _done = true;
            Hide();
            OnCancel?.Invoke();
            Close();
        }
    }

    internal sealed class OverlayCanvas : FrameworkElement
    {
        private readonly OverlayWindow _win;
        private readonly BitmapSource _bg;
        private readonly OverlayMode _mode;
        private readonly List<WindowTarget> _windows;

        private Point? _start, _current;
        private Point _mouse;
        private bool _dragging;
        private WindowTarget? _hover;

        private static readonly Typeface Mono = new Typeface(new System.Windows.Media.FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        private static readonly Typeface Ui = new Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        public OverlayCanvas(OverlayWindow win, BitmapSource bg, OverlayMode mode, List<WindowTarget> windows)
        {
            _win = win; _bg = bg; _mode = mode; _windows = windows;
            Focusable = true;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        }

        private Rect SelDip()
        {
            var s = _start!.Value; var c = _current!.Value;
            return new Rect(Math.Min(s.X, c.X), Math.Min(s.Y, c.Y), Math.Abs(s.X - c.X), Math.Abs(s.Y - c.Y));
        }

        private Rectangle SelPhys()
        {
            var a = _win.ToPhysical(_start!.Value); var b = _win.ToPhysical(_current!.Value);
            return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        private WindowTarget? WindowAt(Point dip)
        {
            var p = _win.ToPhysical(dip);
            return _windows.FirstOrDefault(w => w.Frame.Contains(p));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            _mouse = e.GetPosition(this);
            if (_start.HasValue && e.LeftButton == MouseButtonState.Pressed)
            {
                _current = _mouse;
                if (_mode == OverlayMode.Smart && !_dragging && Math.Sqrt(Math.Pow(_mouse.X - _start.Value.X, 2) + Math.Pow(_mouse.Y - _start.Value.Y, 2)) > 5) { _dragging = true; _hover = null; }
                if (_mode != OverlayMode.Smart) _dragging = true;
            }
            else if (_mode == OverlayMode.Smart) _hover = WindowAt(_mouse);
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            CaptureMouse();
            _start = _current = _mouse = e.GetPosition(this);
            _dragging = false;
            if (_mode == OverlayMode.Smart) _hover = WindowAt(_mouse);
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            ReleaseMouseCapture();
            if (!_start.HasValue) return;
            _current = e.GetPosition(this);
            var phys = SelPhys();
            bool big = phys.Width > 5 && phys.Height > 5;

            if (_mode == OverlayMode.Smart)
            {
                if (_dragging && big) _win.FinishRect(phys);
                else
                {
                    var w = WindowAt(_current.Value);
                    if (w != null) _win.FinishWindow(w);
                    else if (big) _win.FinishRect(phys);
                }
            }
            else if (big) _win.FinishRect(phys);

            _start = _current = null; _dragging = false;
            InvalidateVisual();
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e) => _win.Cancel();

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { _win.Cancel(); e.Handled = true; }
        }

        protected override void OnRender(DrawingContext dc)
        {
            var full = new Rect(0, 0, ActualWidth, ActualHeight);
            dc.DrawImage(_bg, full);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(_mode == OverlayMode.Smart ? (byte)82 : (byte)90, 0, 0, 0)), null, full);

            if (_mode == OverlayMode.Smart && !_dragging && _hover != null)
            {
                var r = _win.ToDip(_hover.Frame);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(46, 0, 122, 255)), new Pen(new SolidColorBrush(Color.FromRgb(0, 122, 255)), 3), r, 8, 8);
                var title = $"{_hover.Title} (Клик: захват окна)";
                var ft = new FormattedText(title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Ui, 11, Brushes.White, 1.0);
                var badge = new Rect(r.X + 8, Math.Max(4, r.Y + 6), ft.Width + 14, 20);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0, 122, 255)), null, badge, 4, 4);
                dc.DrawText(ft, new Point(badge.X + 7, badge.Y + 2));
            }

            bool hasSel = _start.HasValue && _current.HasValue && _dragging;
            if (hasSel)
            {
                var r = SelDip();
                if (r.Width > 1 && r.Height > 1)
                {
                    dc.PushClip(new RectangleGeometry(r));
                    dc.DrawImage(_bg, full);
                    dc.Pop();
                    Brush stroke = _mode == OverlayMode.Record ? new SolidColorBrush(Color.FromRgb(255, 59, 48))
                                 : _mode == OverlayMode.Scroll ? new SolidColorBrush(Color.FromRgb(175, 82, 222)) : Brushes.White;
                    dc.DrawRectangle(null, new Pen(stroke, 1.5), r);
                    DrawCrosshair(dc, _mouse, true, SelPhys());
                    return;
                }
            }
            DrawCrosshair(dc, _mouse, false, Rectangle.Empty);
        }

        private void DrawCrosshair(DrawingContext dc, Point pt, bool dragging, Rectangle sel)
        {
            var blue = new Pen(new SolidColorBrush(Color.FromArgb(230, 51, 179, 255)), 1.5);
            var white = new Pen(new SolidColorBrush(Color.FromArgb(242, 255, 255, 255)), 1.5);
            dc.DrawEllipse(null, blue, pt, 10, 10);
            dc.DrawLine(white, new Point(pt.X - 22, pt.Y), new Point(pt.X - 4, pt.Y));
            dc.DrawLine(white, new Point(pt.X + 4, pt.Y), new Point(pt.X + 22, pt.Y));
            dc.DrawLine(white, new Point(pt.X, pt.Y - 22), new Point(pt.X, pt.Y - 4));
            dc.DrawLine(white, new Point(pt.X, pt.Y + 4), new Point(pt.X, pt.Y + 22));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 64, 89)), null, pt, 1.5, 1.5);

            var phys = _win.ToPhysical(pt);
            string t1 = dragging ? $"W: {sel.Width}" : $"{phys.X}";
            string t2 = dragging ? $"H: {sel.Height}" : $"{phys.Y}";
            var f1 = new FormattedText(t1, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 10, Brushes.White, 1.0);
            var f2 = new FormattedText(t2, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 10, Brushes.White, 1.0);
            double w = Math.Max(f1.Width, f2.Width) + 12, h = 30;
            double bx = pt.X + 14, by = pt.Y + 14;
            if (bx + w > ActualWidth) bx = pt.X - w - 14;
            if (by + h > ActualHeight) by = pt.Y - h - 14;
            var badge = new Rect(bx, by, w, h);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(204, 0, 0, 0)), new Pen(new SolidColorBrush(Color.FromArgb(51, 255, 255, 255)), 1), badge, 5, 5);
            dc.DrawText(f1, new Point(bx + (w - f1.Width) / 2, by + 2));
            dc.DrawText(f2, new Point(bx + (w - f2.Width) / 2, by + 15));
        }
    }

    public static class OverlayManager
    {
        private static OverlayWindow? _current;

        public static bool IsActive => _current != null;

        private static OverlayWindow Create(OverlayMode mode)
        {
            Close();
            // Весь виртуальный экран одним полотном — зона может лежать на стыке мониторов
            var mon = System.Windows.Forms.SystemInformation.VirtualScreen;
            var frozen = CaptureEngine.Capture(mon, sound: false) ?? new Bitmap(mon.Width, mon.Height);
            List<WindowTarget>? windows = mode == OverlayMode.Smart ? WindowDetector.GetVisibleWindows(null) : null;
            var win = new OverlayWindow(mon, frozen, mode, windows);
            win.Closed += (s, e) => { if (_current == win) _current = null; try { win.Frozen.Dispose(); } catch { } };
            _current = win;
            return win;
        }

        private static BitmapSource CropFrozen(OverlayWindow w, Rectangle phys)
        {
            var r = phys; r.Offset(-w.Monitor.X, -w.Monitor.Y);
            r.Intersect(new Rectangle(0, 0, w.Frozen.Width, w.Frozen.Height));
            using var bmp = w.Frozen.Clone(r, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            CaptureEngine.PlayShutterSound();
            return Tag(BitmapUtil.ToSource(bmp), Win32.ScaleForPoint(phys.X + phys.Width / 2, phys.Y + phys.Height / 2));
        }

        /// <summary>Записываем масштаб монитора в DPI картинки — так редактор знает логический размер (аналог NSImage.size в points).</summary>
        public static BitmapSource Tag(BitmapSource src, double scale)
        {
            if (Math.Abs(scale - 1.0) < 0.001) return src;
            var bytes = BitmapUtil.GetBgra(src, out int w, out int h, out int stride);
            var bs = BitmapSource.Create(w, h, 96 * scale, 96 * scale, PixelFormats.Bgra32, null, bytes, stride);
            bs.Freeze();
            return bs;
        }

        public static void ShowAreaOverlay(Action<BitmapSource> onSelected)
        {
            var w = Create(OverlayMode.Area);
            w.OnRect = r => onSelected(CropFrozen(w, r));
            w.Show();
        }

        public static void ShowScrollOverlay(Action<BitmapSource> onSelected)
        {
            var w = Create(OverlayMode.Scroll);
            w.OnRect = r => ScrollCaptureManager.StartSession(r, w.Scale, onSelected);
            w.Show();
        }

        public static void ShowRecordOverlay(Action<Rectangle> onSelected)
        {
            var w = Create(OverlayMode.Record);
            w.OnRect = onSelected;
            w.Show();
        }

        public static void ShowSmartOverlay(Action<BitmapSource> onSelected)
        {
            var w = Create(OverlayMode.Smart);
            w.OnRect = r => onSelected(CropFrozen(w, r));
            w.OnWindow = t =>
            {
                var bmp = CaptureEngine.CaptureWindow(t, w.Frozen, w.Monitor);
                if (bmp != null) { using (bmp) onSelected(Tag(BitmapUtil.ToSource(bmp), Win32.ScaleForPoint(t.Frame.X + t.Frame.Width / 2, t.Frame.Y + t.Frame.Height / 2))); }
            };
            w.Show();
        }

        public static void Close()
        {
            _current?.Cancel();
            _current = null;
        }
    }
}
