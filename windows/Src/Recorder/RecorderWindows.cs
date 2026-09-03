using System;
using System.Drawing;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Cursors = System.Windows.Input.Cursors;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace QScreen.Recorder
{
    /// <summary>Хост для 8-ручечной рамки: работает в физических пикселях (SetWindowPos/GetWindowRect), свободно едет через мониторы с разным DPI.</summary>
    internal abstract class ResizableFrameWindow : Window
    {
        protected const int FrameMargin = 14;
        protected Rectangle Outer;   // окно = зона + FrameMargin со всех сторон, физ. пиксели
        protected readonly FrameView View;
        private readonly int _minW, _minH;

        protected ResizableFrameWindow(Rectangle contentRect, bool bodyDrag, int minW, int minH)
        {
            _minW = minW; _minH = minH;
            Outer = Rectangle.Inflate(contentRect, FrameMargin, FrameMargin);
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
            AllowsTransparency = true; Background = Brushes.Transparent; ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -10000; Top = -10000;
            Win32.MakeNonActivating(this);
            View = new FrameView(this, bodyDrag);
            Content = View;
            SourceInitialized += (s, e) => { HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(Hook); Place(); };
            Loaded += (s, e) => Dispatcher.BeginInvoke(new Action(Place), DispatcherPriority.Loaded);
        }

        private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Win32.WM_DPICHANGED) { handled = true; Dispatcher.BeginInvoke(new Action(Place), DispatcherPriority.Loaded); }
            return IntPtr.Zero;
        }

        private void Place()
        {
            Win32.PlaceWindowPhysical(this, Outer, true, activate: false);
            View.InvalidateVisual();
        }

        public double Scale => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        public Rectangle Zone => Rectangle.Inflate(Outer, -FrameMargin, -FrameMargin);
        public Rectangle OuterRect => Outer;

        public void SetOuter(Rectangle r)
        {
            if (r.Width < _minW + 2 * FrameMargin || r.Height < _minH + 2 * FrameMargin) return;
            Outer = r;
            Place();
            OnZoneChanged(Zone);
        }

        public void Offset(int dx, int dy) { var r = Outer; r.Offset(dx, dy); SetOuter(r); }

        protected abstract void OnZoneChanged(Rectangle zone);
        /// <summary>Рисование содержимого: inner — прямоугольник зоны в DIP окна.</summary>
        protected abstract void Draw(DrawingContext dc, Rect inner, double w, double h);
        /// <summary>Клик по «телу» (не по ручкам) — например, ✕.</summary>
        protected virtual bool HandleBodyClick(Point p) => false;

        internal sealed class FrameView : FrameworkElement
        {
            private enum Handle { None, Top, Bottom, Left, Right, TopLeft, TopRight, BottomLeft, BottomRight, Body }
            private readonly ResizableFrameWindow _w;
            private readonly bool _bodyDrag;
            private Handle _drag = Handle.None;
            private System.Drawing.Point _startMouse;
            private Rectangle _startOuter;

            public FrameView(ResizableFrameWindow w, bool bodyDrag) { _w = w; _bodyDrag = bodyDrag; }

            public double M => FrameMargin / Math.Max(0.5, _w.Scale); // физически всегда FrameMargin px

            private Handle HandleAt(Point p)
            {
                double w = ActualWidth, h = ActualHeight, m = M, hs = 20;
                if (p.X <= m + hs && p.Y <= m + hs) return Handle.TopLeft;
                if (p.X >= w - m - hs && p.Y <= m + hs) return Handle.TopRight;
                if (p.X <= m + hs && p.Y >= h - m - hs) return Handle.BottomLeft;
                if (p.X >= w - m - hs && p.Y >= h - m - hs) return Handle.BottomRight;
                if (!_bodyDrag && new Rect(w / 2 - 80, 0, 160, m + 10).Contains(p)) return Handle.Body; // верхняя планка у рамки записи
                if (p.Y <= m + 8) return Handle.Top;
                if (p.Y >= h - m - 8) return Handle.Bottom;
                if (p.X <= m + 8) return Handle.Left;
                if (p.X >= w - m - 8) return Handle.Right;
                return _bodyDrag ? Handle.Body : Handle.None;
            }

            protected override HitTestResult? HitTestCore(PointHitTestParameters p)
            {
                // У рамки записи внутри — прозрачно для мыши (клики уходят в приложения под ней), как hitTest → nil на маке
                return HandleAt(p.HitPoint) == Handle.None ? null : new PointHitTestResult(this, p.HitPoint);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                var p = e.GetPosition(this);
                if (_drag != Handle.None && e.LeftButton == MouseButtonState.Pressed)
                {
                    var m = Win32.CursorPos();
                    int dx = m.X - _startMouse.X, dy = m.Y - _startMouse.Y;
                    var r = _startOuter;
                    switch (_drag)
                    {
                        case Handle.Body: r.Offset(dx, dy); break;
                        case Handle.TopLeft: r = Rectangle.FromLTRB(r.Left + dx, r.Top + dy, r.Right, r.Bottom); break;
                        case Handle.TopRight: r = Rectangle.FromLTRB(r.Left, r.Top + dy, r.Right + dx, r.Bottom); break;
                        case Handle.BottomLeft: r = Rectangle.FromLTRB(r.Left + dx, r.Top, r.Right, r.Bottom + dy); break;
                        case Handle.BottomRight: r = Rectangle.FromLTRB(r.Left, r.Top, r.Right + dx, r.Bottom + dy); break;
                        case Handle.Top: r = Rectangle.FromLTRB(r.Left, r.Top + dy, r.Right, r.Bottom); break;
                        case Handle.Bottom: r = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom + dy); break;
                        case Handle.Left: r = Rectangle.FromLTRB(r.Left + dx, r.Top, r.Right, r.Bottom); break;
                        case Handle.Right: r = Rectangle.FromLTRB(r.Left, r.Top, r.Right + dx, r.Bottom); break;
                    }
                    _w.SetOuter(r);
                    return;
                }
                Cursor = HandleAt(p) switch
                {
                    Handle.TopLeft or Handle.BottomRight => Cursors.SizeNWSE,
                    Handle.TopRight or Handle.BottomLeft => Cursors.SizeNESW,
                    Handle.Top or Handle.Bottom => Cursors.SizeNS,
                    Handle.Left or Handle.Right => Cursors.SizeWE,
                    Handle.Body => Cursors.SizeAll,
                    _ => Cursors.Arrow
                };
            }

            protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
            {
                var p = e.GetPosition(this);
                if (_w.HandleBodyClick(p)) return;
                _drag = HandleAt(p);
                if (_drag == Handle.None) return;
                _startMouse = Win32.CursorPos();
                _startOuter = _w.OuterRect;
                CaptureMouse();
            }

            protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
            {
                _drag = Handle.None;
                ReleaseMouseCapture();
            }

            protected override void OnRender(DrawingContext dc)
            {
                double w = ActualWidth, h = ActualHeight, m = M;
                var inner = new Rect(m, m, Math.Max(0, w - 2 * m), Math.Max(0, h - 2 * m));
                // Почти прозрачное кольцо по полям: alpha=1 делает область кликабельной, центр (alpha=0) прозрачен для мыши
                var ring = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, w, h)), new RectangleGeometry(Rect.Inflate(inner, -8, -8)));
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, ring);
                _w.Draw(dc, inner, w, h);
            }

            public static void DrawHandles(DrawingContext dc, Rect inner, System.Windows.Media.Brush stroke)
            {
                double hs = 10;
                foreach (var pt in new[]
                {
                    new Point(inner.Left, inner.Top), new Point(inner.Right, inner.Top), new Point(inner.Left, inner.Bottom), new Point(inner.Right, inner.Bottom),
                    new Point(inner.Left + inner.Width / 2, inner.Top), new Point(inner.Left + inner.Width / 2, inner.Bottom),
                    new Point(inner.Left, inner.Top + inner.Height / 2), new Point(inner.Right, inner.Top + inner.Height / 2)
                })
                    dc.DrawRoundedRectangle(Brushes.White, new Pen(stroke, 1.5), new Rect(pt.X - hs / 2, pt.Y - hs / 2, hs, hs), 2, 2);
            }
        }
    }

    /// <summary>Рамка записи: красная, 8 ручек, верхняя планка для смещения, внутри прозрачна для мыши.</summary>
    internal sealed class LiveResizableFrameWindow : ResizableFrameWindow
    {
        private readonly Action<Rectangle> _onChanged;
        private static readonly Typeface Bold = new(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        public LiveResizableFrameWindow(Rectangle contentRect, Action<Rectangle> onChanged) : base(contentRect, bodyDrag: false, 120, 80) { _onChanged = onChanged; }

        protected override void OnZoneChanged(Rectangle zone) => _onChanged(zone);

        protected override void Draw(DrawingContext dc, Rect inner, double w, double h)
        {
            var red = new SolidColorBrush(Color.FromRgb(255, 59, 48));
            dc.DrawRectangle(null, new Pen(red, 2.5), inner);
            FrameView.DrawHandles(dc, inner, red);
            var bar = new Rect(w / 2 - 80, 2, 160, 20);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(217, 0, 0, 0)), new Pen(new SolidColorBrush(Color.FromArgb(51, 255, 255, 255)), 1), bar, 5, 5);
            var ft = new FormattedText("⠿ Зажмите для смещения", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold, 9, Brushes.White, 1.0);
            dc.DrawText(ft, new Point(bar.X + (bar.Width - ft.Width) / 2, bar.Y + (bar.Height - ft.Height) / 2));
        }
    }

    /// <summary>
    /// Live Blur: на экране — полупрозрачная зона с 8 ручками (двигается за тело), в записи — пикселизация этой области (композитор, GPU).
    /// Не зависит от DWM-акрила, который в WPF ведёт себя по-разному на разных билдах.
    /// </summary>
    internal sealed class LiveBlurZoneWindow : ResizableFrameWindow
    {
        private readonly Action<LiveBlurZoneWindow> _onChanged;
        private static readonly Typeface Bold = new(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        public LiveBlurZoneWindow(Rectangle contentRect, Action<LiveBlurZoneWindow> onChanged) : base(contentRect, bodyDrag: true, 60, 40) { _onChanged = onChanged; }

        protected override void OnZoneChanged(Rectangle zone) => _onChanged(this);

        protected override bool HandleBodyClick(Point p)
        {
            // ✕ в левом верхнем углу зоны
            if (new Rect(View.M + 4, View.M + 4, 16, 16).Contains(p)) { Close(); return true; }
            return false;
        }

        protected override void Draw(DrawingContext dc, Rect inner, double w, double h)
        {
            // Ненавязчиво: содержимое под зоной видно, только тонкая пунктирная рамка + маленькие ручки. alpha=1 — чтобы тело ловило мышь
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, inner);
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), 1) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) }, inner);
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 1) { DashStyle = new DashStyle(new double[] { 4, 3 }, 4) }, inner);
            double hs = 6;
            foreach (var pt in new[]
            {
                new Point(inner.Left, inner.Top), new Point(inner.Right, inner.Top), new Point(inner.Left, inner.Bottom), new Point(inner.Right, inner.Bottom),
                new Point(inner.Left + inner.Width / 2, inner.Top), new Point(inner.Left + inner.Width / 2, inner.Bottom),
                new Point(inner.Left, inner.Top + inner.Height / 2), new Point(inner.Right, inner.Top + inner.Height / 2)
            })
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 1), new Rect(pt.X - hs / 2, pt.Y - hs / 2, hs, hs));
            // ✕ — маленький, полупрозрачный
            var x = new Rect(inner.X + 4, inner.Y + 4, 16, 16);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), null, x, 3, 3);
            var ft = new FormattedText("✕", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Bold, 10, Brushes.White, 1.0);
            dc.DrawText(ft, new Point(x.X + (x.Width - ft.Width) / 2, x.Y + (x.Height - ft.Height) / 2));
        }
    }

    /// <summary>Панель управления: прибита к нижнему краю зоны записи и едет вместе с ней. До старта — кнопка «● Запись».</summary>
    internal sealed class RecordingControlBarWindow : Window
    {
        private readonly ScreenRecorder _rec;
        private readonly TextBlock _time;
        private readonly System.Windows.Shapes.Ellipse _dot;
        private readonly Button _pause, _mic, _start, _stop;
        private readonly DispatcherTimer _timer;
        private Rectangle _zone;

        public RecordingControlBarWindow(ScreenRecorder rec)
        {
            _rec = rec;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
            AllowsTransparency = true; Background = Brushes.Transparent; ShowActivated = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -10000; Top = -10000; // до первого PlaceNear — за экраном
            Win32.MakeNonActivating(this);

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            _dot = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = Ui.Red, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            _time = new TextBlock { Text = "00:00", Foreground = Brushes.White, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 13, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            panel.Children.Add(_dot); panel.Children.Add(_time);
            _start = Ui.MakeButton("● Запись", Ui.Red, () => _rec.BeginCapture()); panel.Children.Add(_start);
            _pause = Ui.IconButton("⏸", () => _rec.TogglePause(), "Пауза записи"); panel.Children.Add(_pause);
            _mic = Ui.IconButton("🎤", () => _rec.ToggleMicrophone(), "Отключить микрофон", Ui.Green); panel.Children.Add(_mic);
            panel.Children.Add(Ui.IconButton("▦", () => _rec.AddLiveBlurZone(), "Добавить зону размытия (Live Blur)"));
            _stop = Ui.MakeButton("⏹ Стоп", Ui.Red, () => _rec.StopRecording()); panel.Children.Add(_stop);
            panel.Children.Add(Ui.IconButton("✕", () => _rec.CancelRecording(), "Отменить", Brushes.Gray));

            Content = new Border
            {
                Child = panel, Background = new SolidColorBrush(Color.FromRgb(31, 33, 41)), CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 5, 4, 5),
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Opacity = 0.6 }, Margin = new Thickness(12)
            };

            SourceInitialized += (s, e) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == Win32.WM_DPICHANGED) { handled = true; Dispatcher.BeginInvoke(new Action(() => PlaceNear(_zone)), DispatcherPriority.Loaded); }
                return IntPtr.Zero;
            });
            Loaded += (s, e) => Dispatcher.BeginInvoke(new Action(() => PlaceNear(_zone)), DispatcherPriority.Loaded);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (s, e) => { var t = _rec.Elapsed; _time.Text = $"{(int)t.TotalMinutes:00}:{t.Seconds:00}"; };
            _timer.Start();
            Closed += (s, e) => _timer.Stop();
            Refresh();
        }

        /// <summary>Ставит панель по центру под нижним краем зоны (физ. пиксели); если не влезает — над зоной.</summary>
        public void PlaceNear(Rectangle zone)
        {
            _zone = zone;
            if (zone.IsEmpty) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            UpdateLayout();
            double scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            int w = (int)Math.Ceiling(ActualWidth * scale), h = (int)Math.Ceiling(ActualHeight * scale);
            if (w <= 0 || h <= 0) return;
            int cx = zone.X + zone.Width / 2;
            var wa = Win32.MonitorWorkAreaFromPoint(cx, zone.Bottom);
            int x = Math.Clamp(cx - w / 2, wa.Left, Math.Max(wa.Left, wa.Right - w));
            int y = zone.Bottom + 2;                       // Margin у Border=12dip уже даёт зазор
            if (y + h > wa.Bottom) y = zone.Top - h - 2;  // не влезает снизу — над зоной
            if (y < wa.Top) y = zone.Bottom - h - 8;      // и сверху не влезает — внутри у нижнего края
            Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, x, y, w, h, Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        }

        public void Refresh()
        {
            bool rec = _rec.IsRecording;
            _dot.Visibility = _time.Visibility = _pause.Visibility = _mic.Visibility = _stop.Visibility = rec ? Visibility.Visible : Visibility.Collapsed;
            _start.Visibility = rec ? Visibility.Collapsed : Visibility.Visible;
            _dot.Fill = _rec.IsPaused ? Brushes.Gold : Ui.Red;
            _pause.Content = _rec.IsPaused ? "▶" : "⏸";
            _pause.ToolTip = _rec.IsPaused ? "Возобновить запись" : "Пауза записи";
            _mic.Content = _rec.IsAudioMuted ? "🔇" : "🎤";
            _mic.Foreground = _rec.IsAudioMuted ? Ui.Red : Ui.Green;
            _mic.ToolTip = _rec.IsAudioMuted ? "Включить микрофон" : "Отключить микрофон";
            Dispatcher.BeginInvoke(new Action(() => PlaceNear(_zone)), DispatcherPriority.Loaded);
        }
    }
}
