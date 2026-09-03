using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
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
    public static class ScrollStitcher
    {
        public static Bitmap? Stitch(List<Bitmap> frames)
        {
            if (frames.Count == 0) return null;
            if (frames.Count == 1) return (Bitmap)frames[0].Clone();

            Bitmap stitched = (Bitmap)frames[0].Clone();
            int width = stitched.Width;
            for (int i = 1; i < frames.Count; i++)
            {
                var next = frames[i];
                int overlap = FindVerticalOverlap(stitched, next);
                var combined = Combine(stitched, next, overlap, width);
                stitched.Dispose();
                stitched = combined;
            }
            return stitched;
        }

        private static unsafe int FindVerticalOverlap(Bitmap top, Bitmap bottom)
        {
            int maxSearch = Math.Min(Math.Min(top.Height / 2, bottom.Height / 2), 600);
            if (maxSearch <= 20) return 0;
            int checkWidth = Math.Min(top.Width, bottom.Width);

            var td = top.LockBits(new Rectangle(0, 0, top.Width, top.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var bd = bottom.LockBits(new Rectangle(0, 0, bottom.Width, bottom.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte* tp = (byte*)td.Scan0; byte* bp = (byte*)bd.Scan0;
                int bestOverlap = 0; long minDiff = long.MaxValue;
                for (int overlap = 20; overlap < maxSearch; overlap += 2)
                {
                    long diff = 0;
                    int sampleRows = Math.Min(15, overlap);
                    for (int r = 0; r < sampleRows; r++)
                    {
                        byte* trow = tp + (top.Height - overlap + r) * td.Stride;
                        byte* brow = bp + r * bd.Stride;
                        for (int x = 0; x < checkWidth; x += 4)
                            diff += Math.Abs(trow[x * 4] - brow[x * 4]);
                    }
                    if (diff < minDiff) { minDiff = diff; bestOverlap = overlap; }
                }
                return minDiff < (long)(checkWidth / 4) * 15 * 18 ? bestOverlap : 0;
            }
            finally { top.UnlockBits(td); bottom.UnlockBits(bd); }
        }

        private static Bitmap Combine(Bitmap top, Bitmap bottom, int overlap, int width)
        {
            int newH = top.Height + bottom.Height - overlap;
            var res = new Bitmap(width, newH, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(res);
            g.DrawImageUnscaled(top, 0, 0);
            g.DrawImageUnscaled(bottom, 0, top.Height - overlap);
            return res;
        }
    }

    public static class ScrollCaptureManager
    {
        private static ScrollPanelWindow? _panel;
        private static Rectangle _target;
        private static double _scale = 1.0;
        private static readonly List<Bitmap> _frames = new();
        private static Action<BitmapSource>? _onFinished;

        public static bool IsActive => _panel != null;

        public static void StartSession(Rectangle target, double scale, Action<BitmapSource> onComplete)
        {
            Cancel();
            _target = target; _scale = scale; _onFinished = onComplete;
            CaptureCurrentFrame();
            _panel = new ScrollPanelWindow(target);
            _panel.Show();
            _panel.SetCount(_frames.Count);
        }

        public static void CaptureCurrentFrame()
        {
            var bmp = CaptureEngine.Capture(_target);
            if (bmp != null) { _frames.Add(bmp); _panel?.SetCount(_frames.Count); }
        }

        public static void Finish()
        {
            var cb = _onFinished;
            ClosePanel();
            var stitched = ScrollStitcher.Stitch(_frames);
            ClearFrames();
            if (stitched != null)
            {
                using (stitched) cb?.Invoke(OverlayManager.Tag(BitmapUtil.ToSource(stitched), _scale));
            }
        }

        public static void Cancel()
        {
            ClosePanel();
            ClearFrames();
            _onFinished = null;
        }

        private static void ClearFrames() { foreach (var f in _frames) f.Dispose(); _frames.Clear(); }
        private static void ClosePanel() { _panel?.Close(); _panel = null; }
    }

    internal sealed class ScrollPanelWindow : Window
    {
        private readonly TextBlock _count;

        public ScrollPanelWindow(Rectangle target)
        {
            const int W = 280, H = 110;
            Title = "Скролл-скриншот";
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
            AllowsTransparency = true; Background = Brushes.Transparent;
            SizeToContent = SizeToContent.WidthAndHeight;
            Win32.MakeNonActivating(this);

            var scale = Win32.ScaleForPoint(target.X, target.Y);
            var wa = Win32.MonitorWorkAreaFromPoint(target.X, target.Y);
            int px = (int)Math.Min(wa.Right - W * scale - 20, Math.Max(wa.Left + 20, target.Right + 15));
            int py = (int)Math.Max(wa.Top + 20, Math.Min(wa.Bottom - H * scale - 20, target.Y + target.Height / 2));
            WindowStartupLocation = WindowStartupLocation.Manual;
            SourceInitialized += (s, e) => Win32.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(this).Handle, Win32.HWND_TOPMOST, px, py, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);

            var root = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(31, 33, 38)), CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Width = W,
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), BorderThickness = new Thickness(1)
            };
            var stack = new StackPanel();
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = Brushes.LimeGreen, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            _count = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Bold };
            head.Children.Add(_count);
            stack.Children.Add(head);
            stack.Children.Add(new TextBlock { Text = "Прокрутите страницу вниз и нажмите «+ Кадр»", Foreground = Brushes.Gray, FontSize = 10, Margin = new Thickness(0, 6, 0, 8) });

            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            btns.Children.Add(Ui.MakeButton("＋ Кадр", Ui.Blue, () => ScrollCaptureManager.CaptureCurrentFrame()));
            btns.Children.Add(Ui.MakeButton("✓ Готово", Ui.Green, () => ScrollCaptureManager.Finish()));
            btns.Children.Add(Ui.MakeButton("Отмена", Brushes.Transparent, () => ScrollCaptureManager.Cancel(), Brushes.Gray));
            stack.Children.Add(btns);
            root.Child = stack;
            Content = root;
        }

        public void SetCount(int n) => _count.Text = $"Кадров добавлено: {n}";
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
