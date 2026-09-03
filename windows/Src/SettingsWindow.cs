using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace QScreen
{
    public sealed class HotkeyRecorderControl : Button
    {
        private readonly HotkeyBinding _binding;
        private readonly Action _onChanged;
        private bool _recording;

        public HotkeyRecorderControl(HotkeyBinding binding, Action onChanged)
        {
            _binding = binding; _onChanged = onChanged;
            Foreground = Brushes.White; BorderThickness = new Thickness(1); Padding = new Thickness(10, 4, 10, 4);
            MinWidth = 180; Height = 28; Cursor = Cursors.Hand; FontWeight = FontWeights.SemiBold; FontSize = 11;
            Template = Ui.FlatTemplate();
            Update();
            Click += (s, e) => { _recording = true; Update(); Focus(); };
            LostFocus += (s, e) => { _recording = false; Update(); };
            PreviewKeyDown += OnKey;
        }

        private void Update()
        {
            Content = _recording ? "Нажмите клавиши..." : _binding.DisplayText;
            Background = _recording ? new SolidColorBrush(Color.FromRgb(36, 120, 220)) : new SolidColorBrush(Color.FromRgb(40, 44, 52));
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (!_recording) return;
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { _recording = false; Update(); return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;

            uint mod = 0; var parts = new List<string>();
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { mod |= Win32.MOD_CONTROL; parts.Add("Ctrl"); }
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { mod |= Win32.MOD_SHIFT; parts.Add("Shift"); }
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) { mod |= Win32.MOD_ALT; parts.Add("Alt"); }
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) { mod |= Win32.MOD_WIN; parts.Add("Win"); }
            bool fkey = key >= Key.F1 && key <= Key.F24;
            if (mod == 0 && !fkey) return; // без модификатора допустимы только F-клавиши

            var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            var name = key.ToString();
            if (name.StartsWith("D") && name.Length == 2 && char.IsDigit(name[1])) name = name.Substring(1);
            parts.Add(name);

            _binding.Modifiers = mod; _binding.Key = vk; _binding.DisplayText = string.Join(" + ", parts);
            _recording = false; Update();
            _onChanged();
        }
    }

    public sealed class SettingsWindow : Window
    {
        private readonly Action _onHotkeysChanged;
        private readonly TextBlock _preview = new();
        private readonly TextBlock _folderLabel = new();
        private readonly StackPanel _qualityRow;

        public SettingsWindow(Action onHotkeysChanged)
        {
            _onHotkeysChanged = onHotkeysChanged;
            Title = "Настройки"; Width = 540; Height = 640; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(28, 30, 36)); Foreground = Brushes.White;
            Icon = AppIconProvider.GetImageSource();
            Win32.ApplyDarkMode(this);

            var stack = new StackPanel { Margin = new Thickness(18) };

            // --- Горячие клавиши ---
            stack.Children.Add(Header("Горячие клавиши"));
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int row = 0;
            void HK(string label, HotkeyBinding b)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var tb = new TextBlock { Text = label, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 16, 3) };
                var rec = new HotkeyRecorderControl(b, () => { AppSettings.Save(); _onHotkeysChanged(); }) { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 3) };
                Grid.SetRow(tb, row); Grid.SetColumn(tb, 0); Grid.SetRow(rec, row); Grid.SetColumn(rec, 1);
                grid.Children.Add(tb); grid.Children.Add(rec); row++;
            }
            HK("Захват области:", AppSettings.HK_Area);
            HK("Умный захват (Окно / Зона):", AppSettings.HK_Smart);
            HK("Скролл-скриншот:", AppSettings.HK_Scroll);
            HK("Весь экран:", AppSettings.HK_Screen);
            HK("Запись видео (Старт / Стоп):", AppSettings.HK_Record);
            HK("Остановить запись видео:", AppSettings.HK_RecordStop);
            HK("Пауза / Продолжить запись:", AppSettings.HK_RecordPause);
            stack.Children.Add(grid);
            stack.Children.Add(Sep());

            // --- Формат и имя ---
            stack.Children.Add(Header("Формат и имя сохраняемых файлов"));
            var fmt = Combo(new[] { ("PNG (Без потерь, HiDPI)", "png"), ("HEIC (Высокая эффективность)", "heic"), ("JPG (Компактный размер)", "jpg"), ("PDF (Документ)", "pdf") }, AppSettings.DefaultFormat,
                v => { AppSettings.DefaultFormat = v; AppSettings.Save(); UpdateQualityVisibility(); UpdatePreview(); });
            stack.Children.Add(Row("Формат по умолчанию:", fmt));

            var slider = new Slider { Minimum = 0.5, Maximum = 1.0, TickFrequency = 0.05, IsSnapToTickEnabled = true, Value = AppSettings.JpegQuality, Width = 160, VerticalAlignment = VerticalAlignment.Center };
            var qLabel = new TextBlock { Text = $"{(int)(AppSettings.JpegQuality * 100)}%", Foreground = Brushes.DodgerBlue, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontWeight = FontWeights.Bold, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            slider.ValueChanged += (s, e) => { AppSettings.JpegQuality = Math.Round(slider.Value, 2); qLabel.Text = $"{(int)(AppSettings.JpegQuality * 100)}%"; AppSettings.Save(); };
            var qPanel = new StackPanel { Orientation = Orientation.Horizontal }; qPanel.Children.Add(slider); qPanel.Children.Add(qLabel);
            _qualityRow = Row("Качество сжатия:", qPanel);
            stack.Children.Add(_qualityRow);

            var prefix = new TextBox { Text = AppSettings.FilenamePrefix, Width = 220 };
            prefix.TextChanged += (s, e) => { AppSettings.FilenamePrefix = prefix.Text; AppSettings.Save(); UpdatePreview(); };
            stack.Children.Add(Row("Префикс:", prefix));

            var date = Combo(new[]
            {
                ("ДД.ММ.ГГГГ_ЧЧ.мм.сс", "dd.MM.yyyy_HH.mm.ss"), ("ГГГГ-ММ-ДД_ЧЧ-мм-сс", "yyyy-MM-dd_HH-mm-ss"),
                ("ГГГГММДД_ЧЧммсс", "yyyyMMdd_HHmmss"), ("ДД-ММ-ГГГГ_ЧЧ-мм-сс", "dd-MM-yyyy_HH-mm-ss"), ("Unix Timestamp", "unix")
            }, AppSettings.DateFormat, v => { AppSettings.DateFormat = v; AppSettings.Save(); UpdatePreview(); });
            stack.Children.Add(Row("Формат даты:", date));

            _preview.Foreground = Brushes.DodgerBlue; _preview.FontFamily = new System.Windows.Media.FontFamily("Consolas"); _preview.FontWeight = FontWeights.SemiBold; _preview.FontSize = 11;
            var pv = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            pv.Children.Add(new TextBlock { Text = "Пример имени: ", Foreground = Brushes.Gray, FontSize = 11 });
            pv.Children.Add(_preview);
            stack.Children.Add(pv);
            stack.Children.Add(Sep());

            // --- Видео ---
            stack.Children.Add(Header("Запись видео"));
            stack.Children.Add(Row("Видеокодек:", Combo(new[] { ("H.264 (Играет везде)", "h264"), ("H.265 / HEVC (Меньше файл; штатный плеер Windows требует платное расширение, VLC/mpv — нет)", "hevc") }, AppSettings.VideoCodec, v => { AppSettings.VideoCodec = v; AppSettings.Save(); })));
            stack.Children.Add(Row("Контейнер:", Combo(new[] { ("MP4", "mp4"), ("MOV", "mov") }, AppSettings.VideoFormat, v => { AppSettings.VideoFormat = v; AppSettings.Save(); })));
            stack.Children.Add(Row("Частота кадров:", Combo(new[] { ("60 кадров/сек (Плавное)", "60"), ("30 кадров/сек (Компактное)", "30") }, AppSettings.VideoFps.ToString(), v => { AppSettings.VideoFps = int.Parse(v); AppSettings.Save(); })));
            stack.Children.Add(Check("Записывать звук с микрофона", AppSettings.RecordAudio, v => { AppSettings.RecordAudio = v; AppSettings.Save(); }));
            stack.Children.Add(Check("Показывать курсор и клики мыши", AppSettings.ShowCursor, v => { AppSettings.ShowCursor = v; AppSettings.Save(); }));
            stack.Children.Add(Sep());

            // --- Папка ---
            stack.Children.Add(Header("Папка для сохранения"));
            _folderLabel.Foreground = Brushes.White; _folderLabel.FontWeight = FontWeights.Medium; _folderLabel.Width = 150; _folderLabel.TextTrimming = TextTrimming.CharacterEllipsis; _folderLabel.VerticalAlignment = VerticalAlignment.Center;
            var fp = new StackPanel { Orientation = Orientation.Horizontal };
            fp.Children.Add(_folderLabel);
            fp.Children.Add(Ui.MakeButton("Выбрать...", Ui.Ghost, SelectFolder));
            stack.Children.Add(Row("Папка:", fp));
            UpdateFolderLabel();
            stack.Children.Add(Check("Сохранять сразу в папку (без диалогового окна)", AppSettings.DirectSave, v => { AppSettings.DirectSave = v; AppSettings.Save(); }));
            stack.Children.Add(Sep());

            // --- Система ---
            stack.Children.Add(Header("Действия и система"));
            stack.Children.Add(Check("Миниатюра в углу вместо редактора (клик по ней открывает редактор)", AppSettings.ShowThumbnail, v => { AppSettings.ShowThumbnail = v; AppSettings.Save(); }));
            stack.Children.Add(Check("Запуск при входе в Windows", AppSettings.IsLaunchAtLogin(), v => { try { AppSettings.SetLaunchAtLogin(v); } catch { } }));
            var upd = Ui.MakeButton("🔄 Проверить обновления...", Ui.Ghost, () => _ = UpdateChecker.CheckForUpdatesAsync(true)); upd.HorizontalAlignment = HorizontalAlignment.Left; upd.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(upd);
            stack.Children.Add(Sep());
            stack.Children.Add(new TextBlock { Text = $"QScreen v{UpdateChecker.CurrentVersion} build {UpdateChecker.BuildTag} (Windows.Graphics.Capture Core)", Foreground = Brushes.Gray, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center });

            Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            UpdateQualityVisibility(); UpdatePreview();
        }

        private static TextBlock Header(string t) => new() { Text = t, Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) };
        private static Border Sep() => new() { Height = 1, Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), Margin = new Thickness(0, 12, 0, 12) };

        private static StackPanel Row(string label, FrameworkElement control)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 7) };
            p.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White, Width = 150, VerticalAlignment = VerticalAlignment.Center });
            control.VerticalAlignment = VerticalAlignment.Center;
            p.Children.Add(control);
            return p;
        }

        private static ComboBox Combo((string label, string value)[] items, string selected, Action<string> onChange)
        {
            var cb = new ComboBox { Width = 250, Height = 26 };
            int idx = 0;
            for (int i = 0; i < items.Length; i++) { cb.Items.Add(items[i].label); if (items[i].value == selected) idx = i; }
            cb.SelectedIndex = idx;
            cb.SelectionChanged += (s, e) => { if (cb.SelectedIndex >= 0) onChange(items[cb.SelectedIndex].value); };
            return cb;
        }

        private static CheckBox Check(string label, bool value, Action<bool> onChange)
        {
            var c = new CheckBox { Content = label, IsChecked = value, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 7) };
            c.Checked += (s, e) => onChange(true); c.Unchecked += (s, e) => onChange(false);
            return c;
        }

        private void UpdateQualityVisibility() => _qualityRow.Visibility = AppSettings.DefaultFormat is "jpg" or "heic" ? Visibility.Visible : Visibility.Collapsed;
        private void UpdatePreview() => _preview.Text = FilenameHelper.GenerateFilename();
        private void UpdateFolderLabel() => _folderLabel.Text = string.IsNullOrEmpty(AppSettings.SaveFolder) ? "Рабочий стол (Desktop)" : Path.GetFileName(AppSettings.SaveFolder.TrimEnd('\\'));

        private void SelectFolder()
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = FilenameHelper.GetDefaultSaveFolder() };
            if (dlg.ShowDialog(this) == true) { AppSettings.SaveFolder = dlg.FolderName; AppSettings.Save(); UpdateFolderLabel(); }
        }
    }
}
