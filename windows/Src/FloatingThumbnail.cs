using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace QScreen
{
    public static class FloatingThumbnailManager
    {
        private static ThumbnailWindow? _win;
        private static DispatcherTimer? _timer;

        public static void Show(BitmapSource image, Action onOpenEditor)
        {
            Dismiss();
            _win = new ThumbnailWindow(image, () => { Dismiss(); onOpenEditor(); }, Dismiss);
            _win.Show();
            ResetTimer();
        }

        public static void ResetTimer()
        {
            _timer?.Stop();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _timer.Tick += (s, e) => Dismiss();
            _timer.Start();
        }

        public static void PauseTimer() => _timer?.Stop();

        public static void Dismiss()
        {
            _timer?.Stop(); _timer = null;
            _win?.Close(); _win = null;
        }
    }

    internal sealed class ThumbnailWindow : Window
    {
        public ThumbnailWindow(BitmapSource image, Action onOpen, Action onClose)
        {
            const int W = 200, H = 130;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
            AllowsTransparency = true; Background = Brushes.Transparent;
            Width = W; Height = H;
            Win32.MakeNonActivating(this);

            var p = Win32.CursorPos();
            var wa = Win32.MonitorWorkAreaFromPoint(p.X, p.Y);
            var scale = Win32.ScaleForPoint(p.X, p.Y);
            int px = (int)(wa.Right - W * scale - 20), py = (int)(wa.Bottom - H * scale - 20);
            WindowStartupLocation = WindowStartupLocation.Manual;
            SourceInitialized += (s, e) => Win32.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(this).Handle, Win32.HWND_TOPMOST, px, py, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);

            var root = new Border
            {
                Background = Ui.Panel, CornerRadius = new CornerRadius(10), Padding = new Thickness(8),
                BorderBrush = new SolidColorBrush(Color.FromArgb(51, 255, 255, 255)), BorderThickness = new Thickness(1),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Opacity = 0.6 }
            };
            var grid = new Grid();
            var img = new Image { Source = image, Stretch = Stretch.Uniform };
            img.Clip = null;
            var imgBorder = new Border { CornerRadius = new CornerRadius(6), Child = img, ClipToBounds = true };
            grid.Children.Add(imgBorder);

            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed, Margin = new Thickness(4) };
            btns.Children.Add(Ui.IconButton("✎", onOpen, "Открыть в редакторе"));
            btns.Children.Add(Ui.IconButton("✕", onClose, "Закрыть", Brushes.Gray));
            grid.Children.Add(btns);
            root.Child = grid;
            Content = root;

            MouseEnter += (s, e) => { btns.Visibility = Visibility.Visible; FloatingThumbnailManager.PauseTimer(); };
            MouseLeave += (s, e) => { btns.Visibility = Visibility.Collapsed; FloatingThumbnailManager.ResetTimer(); };
            imgBorder.MouseLeftButtonUp += (s, e) => onOpen();
        }
    }
}
