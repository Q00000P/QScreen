using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Cursors = System.Windows.Input.Cursors;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using MessageBox = System.Windows.MessageBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using LinearGradientBrush = System.Windows.Media.LinearGradientBrush;

namespace QScreen
{
    // --- Все 11 инструментов рисования (как в мак-версии) ---
    public enum DrawTool { Arrow, Rectangle, Ellipse, Text, Bubble, Step, Highlighter, Blur, Ruler, Pen, Crop }

    public static class DrawToolInfo
    {
        public static string Glyph(DrawTool t) => t switch
        {
            DrawTool.Arrow => "↗", DrawTool.Rectangle => "▢", DrawTool.Ellipse => "◯", DrawTool.Text => "T", DrawTool.Bubble => "💬",
            DrawTool.Step => "①", DrawTool.Highlighter => "🖍", DrawTool.Blur => "░", DrawTool.Ruler => "📏", DrawTool.Pen => "✏", DrawTool.Crop => "✂", _ => "?"
        };
        public static string Title(DrawTool t) => t switch
        {
            DrawTool.Arrow => "Стрелка", DrawTool.Rectangle => "Рамка", DrawTool.Ellipse => "Эллипс", DrawTool.Text => "Текст", DrawTool.Bubble => "Выноска",
            DrawTool.Step => "Шаг", DrawTool.Highlighter => "Маркер", DrawTool.Blur => "Цензура", DrawTool.Ruler => "Линейка", DrawTool.Pen => "Карандаш", DrawTool.Crop => "Обрезка", _ => ""
        };
    }

    public abstract class CanvasItem { }

    public sealed class DrawShape : CanvasItem
    {
        public DrawTool Tool;
        public List<Point> Points = new();
        public Color Color;
        public double LineWidth;
        public int StepNumber = 1;
        public Rect Bounds => Points.Count >= 2
            ? new Rect(Math.Min(Points[0].X, Points[1].X), Math.Min(Points[0].Y, Points[1].Y), Math.Abs(Points[1].X - Points[0].X), Math.Abs(Points[1].Y - Points[0].Y))
            : Rect.Empty;
    }

    public sealed class TextAnnotation : CanvasItem
    {
        public string Text = "";
        public Point Position;
        public Color Color;
        public double FontSize = 16;
    }

    public enum GradientPreset { Nebula, Sunset, Ocean, Slate, Border }

    public static class GradientPresets
    {
        public static Brush Brush(GradientPreset p)
        {
            (Color a, Color b) = p switch
            {
                GradientPreset.Nebula => (Color.FromRgb(115, 51, 242), Color.FromRgb(217, 64, 166)),
                GradientPreset.Sunset => (Color.FromRgb(250, 102, 64), Color.FromRgb(217, 38, 140)),
                GradientPreset.Ocean => (Color.FromRgb(26, 153, 242), Color.FromRgb(38, 217, 191)),
                GradientPreset.Slate => (Color.FromRgb(41, 46, 56), Color.FromRgb(20, 23, 28)),
                _ => (Color.FromArgb(38, 255, 255, 255), Color.FromArgb(13, 255, 255, 255)),
            };
            return new LinearGradientBrush(a, b, new Point(0, 0), new Point(1, 1));
        }
    }

    /// <summary>Слой аннотаций поверх картинки. Координаты — логические (DIP), как points на маке.</summary>
    internal sealed class AnnotationLayer : FrameworkElement
    {
        public List<CanvasItem> Items = new();
        public DrawShape? Current;
        public BitmapSource? Pixellated;
        public Size ImageSize;

        private static readonly Typeface Bold = new(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        private static readonly Typeface Mono = new(new System.Windows.Media.FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        protected override void OnRender(DrawingContext dc)
        {
            var full = new Rect(0, 0, ImageSize.Width, ImageSize.Height);
            // Цензура: пикселизированная копия под маской
            if (Pixellated != null)
            {
                foreach (var s in Items.OfType<DrawShape>().Where(s => s.Tool == DrawTool.Blur).Concat(Current?.Tool == DrawTool.Blur ? new[] { Current } : Array.Empty<DrawShape>()))
                {
                    var r = s.Bounds; if (r.IsEmpty || r.Width < 1 || r.Height < 1) continue;
                    dc.PushClip(new RectangleGeometry(r));
                    dc.DrawImage(Pixellated, full);
                    dc.Pop();
                    dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1), r, 3, 3);
                }
            }
            foreach (var item in Items)
            {
                if (item is DrawShape s && s.Tool != DrawTool.Blur) RenderShape(dc, s);
                else if (item is TextAnnotation t) DrawText(dc, t.Text, t.Position, t.FontSize, new SolidColorBrush(t.Color), Bold);
            }
            if (Current != null && Current.Tool != DrawTool.Blur) RenderShape(dc, Current);
        }

        private static void DrawText(DrawingContext dc, string text, Point at, double size, Brush brush, Typeface tf, bool centered = false)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, size, brush, 1.0);
            dc.DrawText(ft, centered ? new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2) : at);
        }

        private static void RenderShape(DrawingContext dc, DrawShape s)
        {
            var brush = new SolidColorBrush(s.Color);
            var pen = new Pen(brush, s.LineWidth) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            switch (s.Tool)
            {
                case DrawTool.Rectangle:
                    if (s.Points.Count >= 2) dc.DrawRoundedRectangle(null, pen, s.Bounds, 4, 4);
                    break;
                case DrawTool.Ellipse:
                    if (s.Points.Count >= 2) { var r = s.Bounds; dc.DrawEllipse(null, pen, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2); }
                    break;
                case DrawTool.Arrow:
                    if (s.Points.Count >= 2)
                    {
                        var a = s.Points[0]; var b = s.Points[1];
                        dc.DrawLine(pen, a, b);
                        double ang = Math.Atan2(b.Y - a.Y, b.X - a.X), len = 16;
                        dc.DrawLine(pen, b, new Point(b.X - len * Math.Cos(ang - Math.PI / 6), b.Y - len * Math.Sin(ang - Math.PI / 6)));
                        dc.DrawLine(pen, b, new Point(b.X - len * Math.Cos(ang + Math.PI / 6), b.Y - len * Math.Sin(ang + Math.PI / 6)));
                    }
                    break;
                case DrawTool.Bubble:
                    if (s.Points.Count >= 2)
                    {
                        var r = s.Bounds; r = new Rect(r.X, r.Y, Math.Max(80, r.Width), Math.Max(40, r.Height));
                        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(217, 0, 0, 0)), pen, r, 10, 10);
                    }
                    break;
                case DrawTool.Highlighter:
                    DrawPolyline(dc, s.Points, new Pen(new SolidColorBrush(Color.FromArgb(102, s.Color.R, s.Color.G, s.Color.B)), 18) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square });
                    break;
                case DrawTool.Pen:
                    DrawPolyline(dc, s.Points, pen);
                    break;
                case DrawTool.Ruler:
                    if (s.Points.Count >= 2)
                    {
                        var a = s.Points[0]; var b = s.Points[1];
                        double dx = Math.Abs(b.X - a.X), dy = Math.Abs(b.Y - a.Y), dist = Math.Sqrt(dx * dx + dy * dy);
                        dc.DrawLine(pen, a, b);
                        double ang = Math.Atan2(b.Y - a.Y, b.X - a.X), cap = 8, perp = ang + Math.PI / 2;
                        dc.DrawLine(pen, new Point(a.X + cap * Math.Cos(perp), a.Y + cap * Math.Sin(perp)), new Point(a.X - cap * Math.Cos(perp), a.Y - cap * Math.Sin(perp)));
                        dc.DrawLine(pen, new Point(b.X + cap * Math.Cos(perp), b.Y + cap * Math.Sin(perp)), new Point(b.X - cap * Math.Cos(perp), b.Y - cap * Math.Sin(perp)));
                        if (dx > 12 && dy > 12)
                        {
                            var dash = new Pen(new SolidColorBrush(Color.FromArgb(102, s.Color.R, s.Color.G, s.Color.B)), 1) { DashStyle = new DashStyle(new double[] { 4, 4 }, 0) };
                            dc.DrawLine(dash, a, new Point(b.X, a.Y));
                            dc.DrawLine(dash, new Point(b.X, a.Y), b);
                        }
                        string label = dx > 8 && dy > 8 ? $"{(int)dist}px (W:{(int)dx} H:{(int)dy})" : $"{(int)dist}px";
                        DrawText(dc, label, new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2 - 12), 10, Brushes.White, Mono, true);
                    }
                    break;
                case DrawTool.Step:
                    if (s.Points.Count >= 1)
                    {
                        var c = s.Points[0];
                        dc.DrawEllipse(brush, null, c, 13, 13);
                        DrawText(dc, s.StepNumber.ToString(), c, 13, Brushes.White, Bold, true);
                    }
                    break;
            }
        }

        private static void DrawPolyline(DrawingContext dc, List<Point> pts, Pen pen)
        {
            if (pts.Count < 2) return;
            var g = new StreamGeometry();
            using (var ctx = g.Open()) { ctx.BeginFigure(pts[0], false, false); ctx.PolyLineTo(pts.Skip(1).ToList(), true, true); }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }
    }

    public sealed class EditorWindow : Window
    {
        public Action<BitmapSource>? OnPin;

        private BitmapSource _image;
        private BitmapSource _pixellated;
        private double _scale;                 // физ. пикселей на DIP
        private Size _logical;                 // логический размер (points)

        private readonly List<CanvasItem> _items = new();
        private DrawShape? _current;
        private DrawTool _tool = DrawTool.Arrow;
        private Color _color = Color.FromRgb(255, 46, 84);
        private double _stroke = 4.0;
        private int _stepCounter = 1;
        private string _exportFormat;
        private bool _beautify;
        private GradientPreset _gradient = GradientPreset.Nebula;
        private Point? _cropStart, _cropCurrent;
        private Point? _activeTextPos;

        private readonly Border _renderRoot;
        private readonly Border _innerBorder;
        private readonly Grid _inner;
        private readonly Image _imageView;
        private readonly AnnotationLayer _layer;
        private readonly Canvas _overlay;
        private readonly System.Windows.Shapes.Rectangle _cropRect;
        private readonly Button _cropButton;
        private readonly TextBox _textBox;
        private readonly TextBlock _toast;
        private readonly Border _toastBorder;
        private readonly TextBlock _formatLabel;
        private readonly ComboBox _gradientBox;
        private readonly Button _beautifyBtn;
        private readonly Button _undoBtn;
        private readonly Dictionary<DrawTool, Button> _toolButtons = new();
        private readonly List<(double w, Button b)> _strokeButtons = new();
        private readonly List<(Color c, Border b)> _paletteButtons = new();
        private readonly Border _customColor;

        private static readonly Color[] Palette =
        {
            Color.FromRgb(255, 46, 84), Color.FromRgb(51, 179, 255), Color.FromRgb(51, 204, 102), Color.FromRgb(255, 204, 0), Colors.White
        };

        public EditorWindow(BitmapSource image)
        {
            _image = image;
            _scale = Math.Max(1.0, image.DpiX / 96.0);
            _logical = new Size(image.PixelWidth / _scale, image.PixelHeight / _scale);
            _pixellated = OverlayManager.Tag(BitmapUtil.Pixellate(image), _scale);
            _exportFormat = FilenameHelper.GetDefaultFormat();

            Title = "QScreen";
            Icon = AppIconProvider.GetImageSource();
            Background = Ui.Panel;
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false, CornerRadius = new CornerRadius(0) });
            MinWidth = 800; MinHeight = 350;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Win32.ApplyDarkMode(this);

            var p = Win32.CursorPos();
            var wa = Win32.MonitorWorkAreaFromPoint(p.X, p.Y);
            var s = Win32.ScaleForPoint(p.X, p.Y);
            Width = Math.Min(wa.Width / s - 40, Math.Max(_logical.Width + 40, 800));
            Height = Math.Min(wa.Height / s - 40, _logical.Height + 60 + 20);

            // ---------- Тулбар ----------
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Height = 44, Background = Ui.Panel };
            toolbar.Children.Add(DragHandle(66));

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(Ui.MakeButton("✓ Готово", Ui.Green, CopyToClipboard, tooltip: "Скопировать (Ctrl+C)"));
            actions.Children.Add(Ui.IconButton("💾", () => HandleSave(_exportFormat), "Сохранить (Ctrl+S)"));

            _formatLabel = new TextBlock { Foreground = Brushes.White, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 10, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
            var fmtBtn = Ui.MakeButton("", Ui.Ghost, () => { }, tooltip: "Формат экспорта");
            var fmtPanel = new StackPanel { Orientation = Orientation.Horizontal };
            fmtPanel.Children.Add(_formatLabel);
            fmtPanel.Children.Add(new TextBlock { Text = " ▾", Foreground = Brushes.White, FontSize = 9, VerticalAlignment = VerticalAlignment.Center });
            fmtBtn.Content = fmtPanel;
            var fmtMenu = new ContextMenu();
            foreach (var (label, f) in new[] { ("PNG (Без потерь, HiDPI)", "png"), ("HEIC (Высокая эффективность)", "heic"), ("JPG (Компактный)", "jpg"), ("PDF (Документ)", "pdf") })
            {
                var mi = new MenuItem { Header = label };
                mi.Click += (o, e) => { _exportFormat = f; UpdateFormatLabel(); };
                fmtMenu.Items.Add(mi);
            }
            fmtBtn.Click += (o, e) => { fmtMenu.PlacementTarget = fmtBtn; fmtMenu.IsOpen = true; };
            actions.Children.Add(fmtBtn);
            UpdateFormatLabel();

            var dragBtn = Ui.IconButton("✋", () => { }, "Drag & Drop в Telegram / Discord / Проводник");
            SetupDragSource(dragBtn);
            actions.Children.Add(dragBtn);
            actions.Children.Add(Ui.IconButton("📌", PinScreenshot, "Закрепить поверх окон (Pin)"));
            actions.Children.Add(Ui.IconButton("🔍", RunOcr, "OCR Распознавание текста"));
            _undoBtn = Ui.IconButton("↶", Undo, "Отменить (Ctrl+Z)");
            actions.Children.Add(_undoBtn);
            toolbar.Children.Add(actions);
            toolbar.Children.Add(Divider());

            // Beautify
            _beautifyBtn = Ui.IconButton("✨", () => { _beautify = !_beautify; ApplyBeautify(); }, "Красивый фон (Beautify)");
            toolbar.Children.Add(_beautifyBtn);
            _gradientBox = new ComboBox { Width = 85, Height = 26, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), FontSize = 11 };
            foreach (var g in Enum.GetValues<GradientPreset>()) _gradientBox.Items.Add(g.ToString());
            _gradientBox.SelectedIndex = 0;
            _gradientBox.SelectionChanged += (o, e) => { _gradient = (GradientPreset)_gradientBox.SelectedIndex; ApplyBeautify(); };
            toolbar.Children.Add(_gradientBox);
            toolbar.Children.Add(Divider());

            // Инструменты
            foreach (var t in Enum.GetValues<DrawTool>())
            {
                var tool = t;
                var b = Ui.IconButton(DrawToolInfo.Glyph(tool), () => SelectTool(tool), DrawToolInfo.Title(tool), Brushes.Gray);
                b.Width = 26; b.Margin = new Thickness(0, 0, 2, 0);
                _toolButtons[tool] = b;
                toolbar.Children.Add(b);
            }
            toolbar.Children.Add(Divider());

            // Толщина
            foreach (var w in new[] { 2.0, 4.0, 8.0 })
            {
                double sw = w;
                var dot = new System.Windows.Shapes.Ellipse { Width = w + 3, Height = w + 3, Fill = Brushes.Gray };
                var b = new Button { Content = dot, Width = 16, Height = 24, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Focusable = false, Padding = new Thickness(0) };
                b.Template = Ui.FlatTemplate();
                b.Click += (o, e) => { _stroke = sw; UpdateStrokeButtons(); };
                _strokeButtons.Add((w, b));
                toolbar.Children.Add(b);
            }
            toolbar.Children.Add(Divider());

            // Палитра
            foreach (var c in Palette)
            {
                var col = c;
                var dot = new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(col), BorderThickness = new Thickness(1.5), BorderBrush = Brushes.Transparent, Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
                dot.MouseLeftButtonDown += (o, e) => { _color = col; UpdatePalette(); };
                _paletteButtons.Add((col, dot));
                toolbar.Children.Add(dot);
            }
            _customColor = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = "Свой цвет",
                Background = new LinearGradientBrush(Colors.Red, Colors.Blue, 45) };
            _customColor.MouseLeftButtonDown += (o, e) => PickCustomColor();
            toolbar.Children.Add(_customColor);

            // ---------- Холст ----------
            _imageView = new Image { Source = _image, Width = _logical.Width, Height = _logical.Height, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(_imageView, BitmapScalingMode.HighQuality);
            _layer = new AnnotationLayer { Items = _items, Pixellated = _pixellated, ImageSize = _logical, Width = _logical.Width, Height = _logical.Height };

            _overlay = new Canvas { Width = _logical.Width, Height = _logical.Height, Background = Brushes.Transparent };
            _cropRect = new System.Windows.Shapes.Rectangle { Stroke = Brushes.White, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 5, 5 }, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            _cropButton = Ui.MakeButton("✂ Обрезать", Ui.Blue, () => { if (CropRect is Rect r) ApplyCrop(r); });
            _cropButton.Visibility = Visibility.Collapsed;
            _textBox = new TextBox
            {
                Visibility = Visibility.Collapsed, FontSize = 18, FontWeight = FontWeights.Bold, Background = new SolidColorBrush(Color.FromArgb(217, 0, 0, 0)),
                Foreground = new SolidColorBrush(_color), BorderBrush = new SolidColorBrush(_color), BorderThickness = new Thickness(1.5), Padding = new Thickness(6, 4, 6, 4), MinWidth = 60, CaretBrush = Brushes.White
            };
            _textBox.KeyDown += (o, e) => { if (e.Key == Key.Enter) { CommitActiveText(); e.Handled = true; } else if (e.Key == Key.Escape) { CancelActiveText(); e.Handled = true; } };
            _overlay.Children.Add(_cropRect); _overlay.Children.Add(_cropButton); _overlay.Children.Add(_textBox);

            _inner = new Grid { Width = _logical.Width, Height = _logical.Height };
            _inner.Children.Add(_imageView); _inner.Children.Add(_layer); _inner.Children.Add(_overlay);
            _innerBorder = new Border { Child = _inner };
            _renderRoot = new Border { Child = _innerBorder, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

            _overlay.MouseLeftButtonDown += Canvas_Down;
            _overlay.MouseMove += Canvas_Move;
            _overlay.MouseLeftButtonUp += Canvas_Up;

            var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Ui.PanelDark, Content = _renderRoot, Focusable = false };

            _toast = new TextBlock { Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold };
            _toastBorder = new Border { Child = _toast, Background = new SolidColorBrush(Color.FromArgb(217, 0, 0, 0)), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 8, 14, 8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 20), Visibility = Visibility.Collapsed, IsHitTestVisible = false };

            var stage = new Grid();
            stage.Children.Add(scroll); stage.Children.Add(_toastBorder);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var topRow = new DockPanel { Background = Ui.Panel };
            var closeBtn = Ui.IconButton("✕", Close, "Закрыть (Esc)", Brushes.Gray); closeBtn.Margin = new Thickness(6, 0, 8, 0);
            DockPanel.SetDock(closeBtn, Dock.Right);
            topRow.Children.Add(closeBtn);
            var rightDrag = DragHandle(40); DockPanel.SetDock(rightDrag, Dock.Right); topRow.Children.Add(rightDrag);
            topRow.Children.Add(toolbar);
            Grid.SetRow(topRow, 0); Grid.SetRow(stage, 1);
            root.Children.Add(topRow); root.Children.Add(stage);
            Content = root;

            SelectTool(DrawTool.Arrow); UpdateStrokeButtons(); UpdatePalette(); UpdateUndo();

            PreviewKeyDown += OnKey;
        }

        // ---------- Вспомогательное UI ----------
        private FrameworkElement DragHandle(double w)
        {
            var b = new Border { Width = w, Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
            b.MouseLeftButtonDown += (o, e) => { if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; else DragMove(); };
            return b;
        }
        private static FrameworkElement Divider() => new Border { Width = 1, Height = 18, Background = new SolidColorBrush(Color.FromArgb(51, 255, 255, 255)), Margin = new Thickness(2, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private void UpdateFormatLabel() => _formatLabel.Text = _exportFormat.ToUpperInvariant();
        private void UpdateUndo() => _undoBtn.IsEnabled = _items.Count > 0 || _activeTextPos != null;

        private void SelectTool(DrawTool t)
        {
            CommitActiveText();
            _tool = t;
            foreach (var kv in _toolButtons)
            {
                kv.Value.Background = kv.Key == t ? Ui.Blue : Brushes.Transparent;
                kv.Value.Foreground = kv.Key == t ? Brushes.White : Brushes.Gray;
            }
            if (t != DrawTool.Crop) { _cropStart = _cropCurrent = null; UpdateCropUi(); }
            _overlay.Cursor = t == DrawTool.Text ? Cursors.IBeam : Cursors.Cross;
        }

        private void UpdateStrokeButtons()
        {
            foreach (var (w, b) in _strokeButtons) ((System.Windows.Shapes.Ellipse)b.Content).Fill = Math.Abs(w - _stroke) < 0.1 ? Brushes.White : new SolidColorBrush(Color.FromArgb(128, 128, 128, 128));
        }

        private void UpdatePalette()
        {
            foreach (var (c, b) in _paletteButtons) b.BorderBrush = c == _color ? Brushes.White : Brushes.Transparent;
            _textBox.Foreground = new SolidColorBrush(_color); _textBox.BorderBrush = new SolidColorBrush(_color);
        }

        private void PickCustomColor()
        {
            using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(_color.R, _color.G, _color.B) };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _color = Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
                _customColor.Background = new SolidColorBrush(_color);
                UpdatePalette();
            }
        }

        private void ApplyBeautify()
        {
            _beautifyBtn.Foreground = _beautify ? Brushes.Gold : Brushes.Gray;
            _beautifyBtn.Background = _beautify ? new SolidColorBrush(Color.FromArgb(51, 255, 255, 255)) : Brushes.Transparent;
            _gradientBox.Visibility = _beautify ? Visibility.Visible : Visibility.Collapsed;
            if (_beautify)
            {
                _renderRoot.Background = GradientPresets.Brush(_gradient);
                _renderRoot.Padding = new Thickness(36);
                _inner.Clip = new RectangleGeometry(new Rect(0, 0, _logical.Width, _logical.Height), 12, 12);
                _innerBorder.Effect = new DropShadowEffect { BlurRadius = 36, ShadowDepth = 10, Direction = 270, Opacity = 0.4 };
            }
            else
            {
                _renderRoot.Background = null; _renderRoot.Padding = new Thickness(0);
                _inner.Clip = null; _innerBorder.Effect = null;
            }
        }

        private void ShowToast(string text)
        {
            _toast.Text = text; _toastBorder.Visibility = Visibility.Visible;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            t.Tick += (o, e) => { t.Stop(); _toastBorder.Visibility = Visibility.Collapsed; };
            t.Start();
        }

        // ---------- Ввод ----------
        private void OnKey(object sender, KeyEventArgs e)
        {
            if (_textBox.IsKeyboardFocusWithin) return;
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            if (e.Key == Key.Escape) { if (_cropStart != null) { _cropStart = _cropCurrent = null; UpdateCropUi(); } else Close(); e.Handled = true; }
            else if (ctrl && e.Key == Key.C) { CopyToClipboard(); e.Handled = true; }
            else if (ctrl && e.Key == Key.S) { HandleSave(_exportFormat); e.Handled = true; }
            else if (ctrl && e.Key == Key.Z) { Undo(); e.Handled = true; }
        }

        private Rect? CropRect => _cropStart is Point s && _cropCurrent is Point c
            ? new Rect(Math.Min(s.X, c.X), Math.Min(s.Y, c.Y), Math.Abs(s.X - c.X), Math.Abs(s.Y - c.Y)) : null;

        private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, _logical.Width), Math.Clamp(p.Y, 0, _logical.Height));

        private void Canvas_Down(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject src && (src == _textBox || _textBox.IsAncestorOf(src) || src == _cropButton || _cropButton.IsAncestorOf(src))) return;
            var p = Clamp(e.GetPosition(_overlay));
            _overlay.CaptureMouse();
            if (_tool == DrawTool.Text) { CommitActiveText(); StartText(p); return; }
            if (_tool == DrawTool.Crop) { _cropStart = p; _cropCurrent = p; UpdateCropUi(); return; }
            var shape = new DrawShape { Tool = _tool, Points = new List<Point> { p, p }, Color = _color, LineWidth = _stroke };
            if (_tool == DrawTool.Step) shape.StepNumber = _stepCounter++;
            _current = shape; _layer.Current = shape; _layer.InvalidateVisual();
        }

        private void Canvas_Move(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            var p = Clamp(e.GetPosition(_overlay));
            if (_tool == DrawTool.Crop && _cropStart != null) { _cropCurrent = p; UpdateCropUi(); return; }
            if (_current == null) return;
            if (_tool == DrawTool.Pen || _tool == DrawTool.Highlighter)
            {
                var last = _current.Points[^1];
                if (Math.Sqrt(Math.Pow(p.X - last.X, 2) + Math.Pow(p.Y - last.Y, 2)) > 3) _current.Points.Add(p);
            }
            else _current.Points[1] = p;
            _layer.InvalidateVisual();
        }

        private void Canvas_Up(object sender, MouseButtonEventArgs e)
        {
            _overlay.ReleaseMouseCapture();
            if (_tool == DrawTool.Crop || _tool == DrawTool.Text) return;
            if (_current != null)
            {
                if (_current.Tool == DrawTool.Step || _current.Points.Count > 2 || (_current.Points[0] - _current.Points[1]).Length > 2) _items.Add(_current);
                _current = null; _layer.Current = null; _layer.InvalidateVisual(); UpdateUndo();
            }
        }

        private void UpdateCropUi()
        {
            if (CropRect is Rect r && _tool == DrawTool.Crop && r.Width > 1 && r.Height > 1)
            {
                Canvas.SetLeft(_cropRect, r.X); Canvas.SetTop(_cropRect, r.Y); _cropRect.Width = r.Width; _cropRect.Height = r.Height; _cropRect.Visibility = Visibility.Visible;
                Canvas.SetLeft(_cropButton, r.X + r.Width / 2 - 45); Canvas.SetTop(_cropButton, Math.Max(0, r.Y - 30)); _cropButton.Visibility = Visibility.Visible;
            }
            else { _cropRect.Visibility = Visibility.Collapsed; _cropButton.Visibility = Visibility.Collapsed; }
        }

        private void ApplyCrop(Rect r)
        {
            var final = RenderFinalImage(ignoreBeautify: true);
            var px = new Int32Rect((int)Math.Round(r.X * _scale), (int)Math.Round(r.Y * _scale), (int)Math.Round(r.Width * _scale), (int)Math.Round(r.Height * _scale));
            if (px.Width < 2 || px.Height < 2) return;
            SetImage(OverlayManager.Tag(BitmapUtil.Crop(final, px), _scale));
            _items.Clear(); _cropStart = _cropCurrent = null; SelectTool(DrawTool.Arrow); UpdateUndo();
        }

        private void SetImage(BitmapSource img)
        {
            _image = img;
            _logical = new Size(img.PixelWidth / _scale, img.PixelHeight / _scale);
            _pixellated = OverlayManager.Tag(BitmapUtil.Pixellate(img), _scale);
            _imageView.Source = img; _imageView.Width = _logical.Width; _imageView.Height = _logical.Height;
            _layer.Pixellated = _pixellated; _layer.ImageSize = _logical; _layer.Width = _logical.Width; _layer.Height = _logical.Height;
            _overlay.Width = _logical.Width; _overlay.Height = _logical.Height;
            _inner.Width = _logical.Width; _inner.Height = _logical.Height;
            ApplyBeautify();
            _layer.InvalidateVisual();
        }

        // ---------- Текст ----------
        private void StartText(Point p)
        {
            _activeTextPos = p;
            _textBox.Text = "";
            Canvas.SetLeft(_textBox, Math.Max(0, Math.Min(p.X, _logical.Width - 60)));
            Canvas.SetTop(_textBox, Math.Max(0, Math.Min(p.Y, _logical.Height - 30)));
            _textBox.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(new Action(() => { _textBox.Focus(); Keyboard.Focus(_textBox); }), DispatcherPriority.Input);
            UpdateUndo();
        }

        private void CommitActiveText()
        {
            if (_activeTextPos is Point pos)
            {
                var clean = _textBox.Text.Trim();
                if (clean.Length > 0) _items.Add(new TextAnnotation { Text = clean, Position = new Point(pos.X + 6, pos.Y + 4), Color = _color });
                _activeTextPos = null; _textBox.Text = ""; _textBox.Visibility = Visibility.Collapsed;
                _layer.InvalidateVisual(); UpdateUndo();
                Focus();
            }
        }

        private void CancelActiveText()
        {
            _activeTextPos = null; _textBox.Text = ""; _textBox.Visibility = Visibility.Collapsed; UpdateUndo(); Focus();
        }

        private void Undo()
        {
            if (_activeTextPos != null) { CancelActiveText(); return; }
            if (_items.Count > 0) { _items.RemoveAt(_items.Count - 1); _layer.InvalidateVisual(); }
            UpdateUndo();
        }

        // ---------- Рендер / экспорт ----------
        public BitmapSource RenderFinalImage(bool ignoreBeautify = false)
        {
            CommitActiveText();
            _overlay.Visibility = Visibility.Collapsed;
            _renderRoot.UpdateLayout();
            FrameworkElement target = ignoreBeautify ? _inner : _renderRoot;
            var effect = _innerBorder.Effect; var clip = _inner.Clip;
            if (ignoreBeautify) { _innerBorder.Effect = null; _inner.Clip = null; }
            try
            {
                double w = target.ActualWidth, h = target.ActualHeight;
                var rtb = new RenderTargetBitmap((int)Math.Round(w * _scale), (int)Math.Round(h * _scale), 96 * _scale, 96 * _scale, PixelFormats.Pbgra32);
                // Рендерим через DrawingVisual с VisualBrush — снимает смещение элемента внутри окна
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(target) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, w, h));
                rtb.Render(dv);
                rtb.Freeze();
                return rtb;
            }
            finally
            {
                if (ignoreBeautify) { _innerBorder.Effect = effect; _inner.Clip = clip; }
                _overlay.Visibility = Visibility.Visible;
            }
        }

        private void CopyToClipboard()
        {
            var img = RenderFinalImage();
            var d = new DataObject();
            d.SetImage(img);
            d.SetData("PNG", new MemoryStream(ImageExportHelper.EncodePng(img)));
            try { Clipboard.SetDataObject(d, true); } catch { }
            Close();
        }

        private void PinScreenshot() => OnPin?.Invoke(RenderFinalImage());

        private void RunOcr()
        {
            var text = OcrEngine.ExtractText(RenderFinalImage(ignoreBeautify: true));
            if (text == null) { ShowToast("OCR недоступен: установите языковой пакет Windows"); return; }
            if (text.Length == 0) { ShowToast("Текст не найден"); return; }
            try { Clipboard.SetText(text); } catch { }
            ShowToast("Текст скопирован!");
        }

        private void HandleSave(string fmt)
        {
            if (AppSettings.DirectSave) SaveDirectly(fmt); else SaveWithDialog(fmt);
        }

        private void SaveDirectly(string fmt)
        {
            var res = ImageExportHelper.ExportData(RenderFinalImage(), fmt);
            if (res == null) { ShowToast("Ошибка экспорта"); return; }
            var folder = FilenameHelper.GetDefaultSaveFolder();
            var name = FilenameHelper.GenerateFilename(res.Value.Ext);
            try
            {
                File.WriteAllBytes(Path.Combine(folder, name), res.Value.Data);
                ShowToast($"Сохранено ({res.Value.Ext.ToUpperInvariant()}): {Path.GetFileName(folder)}/{name}");
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                t.Tick += (o, e) => { t.Stop(); Close(); };
                t.Start();
            }
            catch { SaveWithDialog(fmt); }
        }

        private void SaveWithDialog(string fmt)
        {
            var img = RenderFinalImage();
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG|*.png|HEIC|*.heic|JPG|*.jpg|PDF|*.pdf",
                FilterIndex = fmt switch { "heic" => 2, "jpg" => 3, "pdf" => 4, _ => 1 },
                FileName = FilenameHelper.GenerateFilename(fmt),
                InitialDirectory = FilenameHelper.GetDefaultSaveFolder()
            };
            if (dlg.ShowDialog(this) != true) return;
            var ext = Path.GetExtension(dlg.FileName).TrimStart('.').ToLowerInvariant();
            var res = ImageExportHelper.ExportData(img, ext);
            if (res == null) { ShowToast("Ошибка экспорта"); return; }
            var path = dlg.FileName;
            if (ext != res.Value.Ext) path = Path.ChangeExtension(path, res.Value.Ext); // heic без кодека → jpg
            File.WriteAllBytes(path, res.Value.Data);
            Close();
        }

        // ---------- Drag & Drop ----------
        private void SetupDragSource(Button btn)
        {
            Point? start = null;
            btn.PreviewMouseLeftButtonDown += (o, e) => { start = e.GetPosition(btn); };
            btn.PreviewMouseLeftButtonUp += (o, e) => start = null;
            btn.PreviewMouseMove += (o, e) =>
            {
                if (start == null || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(btn);
                if ((p - start.Value).Length < 3) return;
                start = null;
                var img = RenderFinalImage();
                var res = ImageExportHelper.ExportData(img, _exportFormat);
                if (res == null) return;
                var dir = Path.Combine(Path.GetTempPath(), "QScreenDrag");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, FilenameHelper.GenerateFilename(res.Value.Ext));
                File.WriteAllBytes(file, res.Value.Data);

                var d = new DataObject();
                d.SetData(DataFormats.FileDrop, new[] { file });
                d.SetImage(img);
                d.SetData("PNG", new MemoryStream(ImageExportHelper.EncodePng(img)));
                d.SetText(file);
                DragDrop.DoDragDrop(btn, d, DragDropEffects.Copy | DragDropEffects.Move);
            };
        }
    }

    /// <summary>Закреплённый поверх окон скриншот. Двойной клик — закрыть, колесо — масштаб.</summary>
    public sealed class PinnedWindow : Window
    {
        public PinnedWindow(BitmapSource image)
        {
            double scale = Math.Max(1.0, image.DpiX / 96.0);
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
            AllowsTransparency = true; Background = Brushes.Transparent;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var img = new Image { Source = image, Width = image.PixelWidth / scale, Height = image.PixelHeight / scale, Stretch = Stretch.Fill };
            var border = new Border { Child = img, Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 2, Opacity = 0.7 }, Margin = new Thickness(10) };
            Content = border;
            MouseLeftButtonDown += (o, e) => { if (e.ClickCount == 2) Close(); else DragMove(); };
            KeyDown += (o, e) => { if (e.Key == Key.Escape) Close(); };
            MouseWheel += (o, e) => { double k = e.Delta > 0 ? 1.1 : 1 / 1.1; img.Width = Math.Max(40, img.Width * k); img.Height = Math.Max(30, img.Height * k); };
        }
    }
}
