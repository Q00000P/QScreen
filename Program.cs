using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Rectangle = System.Windows.Shapes.Rectangle;
using MessageBox = System.Windows.MessageBox;

namespace QScreen
{
    public static class AppIconProvider
    {
        private static Icon? _cachedIcon;

        public static Icon GetAppIcon()
        {
            if (_cachedIcon != null) return _cachedIcon;

            try
            {
                var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath) && System.IO.File.Exists(exePath))
                {
                    _cachedIcon = Icon.ExtractAssociatedIcon(exePath);
                }
            }
            catch { }

            if (_cachedIcon == null && System.IO.File.Exists("QScreen.ico"))
            {
                try { _cachedIcon = new Icon("QScreen.ico"); } catch { }
            }

            return _cachedIcon ?? SystemIcons.Application;
        }

        public static ImageSource? GetImageSource()
        {
            try
            {
                var icon = GetAppIcon();
                return Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            catch
            {
                return null;
            }
        }
    }

    public static class UpdateChecker
    {
        public const string CurrentVersion = "9.3.3";
        public static string Repo = "Q00000P/QScreen";

        public static async Task CheckForUpdatesAsync(bool isUserInitiated = false)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "QScreen-App");

                var url = $"https://api.github.com/repos/{Repo}/releases/latest";
                var response = await client.GetStringAsync(url);

                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                var tagName = root.GetProperty("tag_name").GetString() ?? "";
                var remoteVer = tagName.TrimStart('v', 'V');
                var currentVer = new Version(CurrentVersion);
                var latestVer = new Version(remoteVer);

                if (latestVer > currentVer)
                {
                    string downloadUrl = "";
                    if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            var name = asset.GetProperty("name").GetString() ?? "";
                            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            {
                                downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                                break;
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(downloadUrl))
                    {
                        downloadUrl = root.GetProperty("html_url").GetString() ?? "";
                    }

                    var result = MessageBox.Show(
                        $"Доступна новая версия QScreen v{remoteVer}!\n\nТекущая версия: v{CurrentVersion}\n\nСкачать и установить обновление автоматически?",
                        "Обновление QScreen",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information
                    );

                    if (result == MessageBoxResult.Yes)
                    {
                        if (downloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            await PerformSilentUpdate(downloadUrl, remoteVer);
                        }
                        else
                        {
                            Process.Start(new ProcessStartInfo(downloadUrl) { UseShellExecute = true });
                        }
                    }
                }
                else if (isUserInitiated)
                {
                    MessageBox.Show(
                        $"У вас установлена актуальная версия QScreen (v{CurrentVersion}).",
                        "Обновлений нет",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                }
            }
            catch (Exception ex)
            {
                if (isUserInitiated)
                {
                    MessageBox.Show($"Не удалось проверить обновления: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private static async Task PerformSilentUpdate(string zipUrl, string newVer)
        {
            var progressWin = new Window
            {
                Title = "Обновление QScreen",
                Width = 360,
                Height = 120,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = new SolidColorBrush(Color.FromRgb(28, 30, 36)),
                ResizeMode = ResizeMode.NoResize,
                Topmost = true
            };
            Win32.ApplyDarkMode(progressWin);
            progressWin.Icon = AppIconProvider.GetImageSource();

            var panel = new StackPanel { Margin = new Thickness(16) };
            var lbl = new TextBlock { Text = $"Загрузка QScreen v{newVer}...", Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 10), FontWeight = FontWeights.SemiBold };
            var pb = new ProgressBar { Height = 14, IsIndeterminate = true, Background = new SolidColorBrush(Color.FromRgb(40, 44, 52)), Foreground = new SolidColorBrush(Color.FromRgb(50, 180, 255)) };
            panel.Children.Add(lbl);
            panel.Children.Add(pb);
            progressWin.Content = panel;
            progressWin.Show();

            try
            {
                var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "QScreen_Update_" + Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(tempDir);
                var zipFile = System.IO.Path.Combine(tempDir, "update.zip");

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "QScreen-App");
                    var data = await client.GetByteArrayAsync(zipUrl);
                    await System.IO.File.WriteAllBytesAsync(zipFile, data);
                }

                var extractDir = System.IO.Path.Combine(tempDir, "extracted");
                ZipFile.ExtractToDirectory(zipFile, extractDir);

                var currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
                var installDir = System.IO.Path.GetDirectoryName(currentExe)!;

                var updaterScript = System.IO.Path.Combine(tempDir, "apply_update.ps1");
                var psContent = $@"
Start-Sleep -Milliseconds 800
Copy-Item -Path '{extractDir}\*' -Destination '{installDir}' -Recurse -Force
Start-Process '{currentExe}'
Remove-Item -Path '{tempDir}' -Recurse -Force
";
                System.IO.File.WriteAllText(updaterScript, psContent, Encoding.UTF8);

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-ExecutionPolicy Bypass -NoProfile -WindowStyle Hidden -File \"{updaterScript}\"",
                    UseShellExecute = true,
                    CreateNoWindow = true
                };

                Process.Start(psi);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                progressWin.Close();
                MessageBox.Show($"Ошибка при установке обновления: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    public static class Win32
    {
        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        public static readonly IntPtr HWND_TOP = IntPtr.Zero;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const int SW_RESTORE = 9;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;

        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

        public static void ApplyDarkMode(Window window)
        {
            window.SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                int useDark = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref useDark, sizeof(int));
            };
        }
    }

    public struct WindowTarget
    {
        public IntPtr Hwnd;
        public Rect Bounds;
        public string Title;
    }

    public static class WindowDetector
    {
        public static List<WindowTarget> GetVisibleWindows()
        {
            var list = new List<WindowTarget>();
            Win32.EnumWindows((hwnd, lParam) =>
            {
                if (Win32.IsWindowVisible(hwnd))
                {
                    Win32.RECT r;
                    if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(Win32.RECT))) != 0)
                    {
                        Win32.GetWindowRect(hwnd, out r);
                    }

                    int w = r.Right - r.Left;
                    int h = r.Bottom - r.Top;
                    if (w > 80 && h > 80 && r.Left >= -1000 && r.Top >= -1000)
                    {
                        var sb = new StringBuilder(256);
                        Win32.GetWindowText(hwnd, sb, 256);
                        var title = sb.ToString().Trim();
                        if (string.IsNullOrEmpty(title)) title = "Window";
                        if (!title.Contains("QScreen") && !title.Contains("Overlay"))
                        {
                            list.Add(new WindowTarget
                            {
                                Hwnd = hwnd,
                                Bounds = new Rect(r.Left, r.Top, w, h),
                                Title = title
                            });
                        }
                    }
                }
                return true;
            }, IntPtr.Zero);
            return list;
        }

        // Безопасный и быстрый захват окна без зависаний через вычисление DWM-границ
        public static Bitmap CaptureWindowFast(Rect bounds)
        {
            int x = (int)Math.Max(0, bounds.X);
            int y = (int)Math.Max(0, bounds.Y);
            int width = (int)Math.Max(10, bounds.Width);
            int height = (int)Math.Max(10, bounds.Height);

            var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
            }
            return bmp;
        }
    }

    public class HotkeyBinding
    {
        public string Name { get; set; } = "";
        public uint Modifiers { get; set; }
        public uint Key { get; set; }
        public string DisplayText { get; set; } = "";

        public static HotkeyBinding FromString(string name, string str, string defaultStr, uint defMod, uint defKey)
        {
            if (string.IsNullOrWhiteSpace(str)) str = defaultStr;
            var parts = str.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            uint mod = 0;
            uint k = 0;
            foreach (var p in parts)
            {
                if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Control", StringComparison.OrdinalIgnoreCase)) mod |= Win32.MOD_CONTROL;
                else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) mod |= Win32.MOD_SHIFT;
                else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) mod |= Win32.MOD_ALT;
                else if (p.Equals("Win", StringComparison.OrdinalIgnoreCase)) mod |= Win32.MOD_WIN;
                else
                {
                    if (Enum.TryParse<Key>(p, true, out var wpfKey))
                    {
                        k = (uint)KeyInterop.VirtualKeyFromKey(wpfKey);
                    }
                    else if (p.Length == 1)
                    {
                        k = (uint)char.ToUpper(p[0]);
                    }
                }
            }

            if (k == 0) { mod = defMod; k = defKey; str = defaultStr; }
            return new HotkeyBinding { Name = name, Modifiers = mod, Key = k, DisplayText = str };
        }
    }

    public static class AppSettings
    {
        private static string ConfigPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QScreen", "config.ini");

        public static HotkeyBinding HK_Area = new() { Name = "Area", Modifiers = Win32.MOD_CONTROL | Win32.MOD_SHIFT, Key = 0x34, DisplayText = "Ctrl + Shift + 4" };
        public static HotkeyBinding HK_Smart = new() { Name = "Smart", Modifiers = Win32.MOD_CONTROL | Win32.MOD_SHIFT, Key = 0x36, DisplayText = "Ctrl + Shift + 6" };
        public static HotkeyBinding HK_Scroll = new() { Name = "Scroll", Modifiers = Win32.MOD_CONTROL | Win32.MOD_SHIFT, Key = 0x37, DisplayText = "Ctrl + Shift + 7" };
        public static HotkeyBinding HK_Screen = new() { Name = "Screen", Modifiers = Win32.MOD_CONTROL | Win32.MOD_SHIFT, Key = 0x33, DisplayText = "Ctrl + Shift + 3" };
        public static HotkeyBinding HK_Record = new() { Name = "Record", Modifiers = Win32.MOD_CONTROL | Win32.MOD_SHIFT, Key = 0x35, DisplayText = "Ctrl + Shift + 5" };

        public static string DefaultFormat = "png";
        public static double JpegQuality = 0.85;
        public static string FilenamePrefix = "QScreen";
        public static string DateFormat = "dd.MM.yyyy_HH.mm.ss";
        public static string SaveFolder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        public static bool DirectSave = false;
        public static bool ShowThumbnail = true;

        static AppSettings() { Load(); }

        public static void Load()
        {
            try
            {
                if (System.IO.File.Exists(ConfigPath))
                {
                    foreach (var line in System.IO.File.ReadAllLines(ConfigPath))
                    {
                        var parts = line.Split('=', 2);
                        if (parts.Length != 2) continue;
                        var k = parts[0].Trim();
                        var v = parts[1].Trim();
                        if (k == "HK_Area") HK_Area = HotkeyBinding.FromString("Area", v, "Ctrl + Shift + 4", Win32.MOD_CONTROL | Win32.MOD_SHIFT, 0x34);
                        else if (k == "HK_Smart") HK_Smart = HotkeyBinding.FromString("Smart", v, "Ctrl + Shift + 6", Win32.MOD_CONTROL | Win32.MOD_SHIFT, 0x36);
                        else if (k == "HK_Scroll") HK_Scroll = HotkeyBinding.FromString("Scroll", v, "Ctrl + Shift + 7", Win32.MOD_CONTROL | Win32.MOD_SHIFT, 0x37);
                        else if (k == "HK_Screen") HK_Screen = HotkeyBinding.FromString("Screen", v, "Ctrl + Shift + 3", Win32.MOD_CONTROL | Win32.MOD_SHIFT, 0x33);
                        else if (k == "HK_Record") HK_Record = HotkeyBinding.FromString("Record", v, "Ctrl + Shift + 5", Win32.MOD_CONTROL | Win32.MOD_SHIFT, 0x35);
                        else if (k == "DefaultFormat") DefaultFormat = v;
                        else if (k == "JpegQuality" && double.TryParse(v, out var jq)) JpegQuality = jq;
                        else if (k == "FilenamePrefix") FilenamePrefix = v;
                        else if (k == "DateFormat") DateFormat = v;
                        else if (k == "SaveFolder" && System.IO.Directory.Exists(v)) SaveFolder = v;
                        else if (k == "DirectSave" && bool.TryParse(v, out var ds)) DirectSave = ds;
                        else if (k == "ShowThumbnail" && bool.TryParse(v, out var st)) ShowThumbnail = st;
                    }
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
                var lines = new[]
                {
                    $"HK_Area={HK_Area.DisplayText}",
                    $"HK_Smart={HK_Smart.DisplayText}",
                    $"HK_Scroll={HK_Scroll.DisplayText}",
                    $"HK_Screen={HK_Screen.DisplayText}",
                    $"HK_Record={HK_Record.DisplayText}",
                    $"DefaultFormat={DefaultFormat}",
                    $"JpegQuality={JpegQuality}",
                    $"FilenamePrefix={FilenamePrefix}",
                    $"DateFormat={DateFormat}",
                    $"SaveFolder={SaveFolder}",
                    $"DirectSave={DirectSave}",
                    $"ShowThumbnail={ShowThumbnail}"
                };
                System.IO.File.WriteAllLines(ConfigPath, lines);
            }
            catch { }
        }

        public static string GenerateFileName(string? ext = null)
        {
            var chosenExt = (ext ?? DefaultFormat).ToLower();
            if (chosenExt == "jpeg") chosenExt = "jpg";

            string dateStr;
            if (DateFormat == "unix") dateStr = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            else dateStr = DateTime.Now.ToString(DateFormat);

            var prefix = FilenamePrefix.Trim();
            var baseName = string.IsNullOrEmpty(prefix) ? dateStr : $"{prefix}_{dateStr}";
            return $"{baseName}.{chosenExt}";
        }

        public static bool IsStartupEnabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue("QScreen") != null;
        }

        public static void SetStartup(bool enable)
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            var exe = Environment.ProcessPath;
            if (enable && exe != null) key.SetValue("QScreen", $"\"{exe}\"");
            else key.DeleteValue("QScreen", false);
        }
    }

    public class Program
    {
        [STAThread]
        public static void Main()
        {
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var controller = new AppController();
            app.Run();
        }
    }

    public class AppController
    {
        private Forms.NotifyIcon _notifyIcon;
        private HwndSource? _hwndSource;
        private IntPtr _hwnd = IntPtr.Zero;
        private SettingsWindow? _settingsWindow;

        public const int ID_HK_AREA = 9001;
        public const int ID_HK_SMART = 9002;
        public const int ID_HK_SCROLL = 9003;
        public const int ID_HK_SCREEN = 9004;
        public const int ID_HK_RECORD = 9005;

        public AppController()
        {
            _notifyIcon = new Forms.NotifyIcon();
            _notifyIcon.Icon = AppIconProvider.GetAppIcon();
            _notifyIcon.Text = "QScreen Studio";
            _notifyIcon.Visible = true;

            UpdateContextMenu();
            _notifyIcon.DoubleClick += (s, e) => ShowSettings();

            SetupHotkeys();

            Task.Run(async () => await UpdateChecker.CheckForUpdatesAsync(isUserInitiated: false));
        }

        public void UpdateContextMenu()
        {
            var cm = new Forms.ContextMenuStrip();
            cm.Items.Add($"🎯 Захват области ({AppSettings.HK_Area.DisplayText})", null, (s, e) => StartAreaCapture());
            cm.Items.Add($"🔲 Умный захват (Окно / Зона) ({AppSettings.HK_Smart.DisplayText})", null, (s, e) => StartSmartCapture());
            cm.Items.Add($"📜 Скролл-скриншот ({AppSettings.HK_Scroll.DisplayText})", null, (s, e) => StartScrollCapture());
            cm.Items.Add($"🖥 Весь экран ({AppSettings.HK_Screen.DisplayText})", null, (s, e) => StartScreenCapture());
            cm.Items.Add(new Forms.ToolStripSeparator());
            cm.Items.Add("🔄 Проверить обновления...", null, async (s, e) => await UpdateChecker.CheckForUpdatesAsync(isUserInitiated: true));
            cm.Items.Add("⚙ Настройки...", null, (s, e) => ShowSettings());
            cm.Items.Add(new Forms.ToolStripSeparator());
            cm.Items.Add("❌ Выход", null, (s, e) => ExitApp());
            _notifyIcon.ContextMenuStrip = cm;
        }

        public void SetupHotkeys()
        {
            if (_hwnd == IntPtr.Zero)
            {
                var helper = new WindowInteropHelper(new Window());
                _hwnd = helper.EnsureHandle();
                _hwndSource = HwndSource.FromHwnd(_hwnd);
                _hwndSource.AddHook(HwndHook);
            }

            for (int i = 9001; i <= 9005; i++) Win32.UnregisterHotKey(_hwnd, i);

            Win32.RegisterHotKey(_hwnd, ID_HK_AREA, AppSettings.HK_Area.Modifiers, AppSettings.HK_Area.Key);
            Win32.RegisterHotKey(_hwnd, ID_HK_SMART, AppSettings.HK_Smart.Modifiers, AppSettings.HK_Smart.Key);
            Win32.RegisterHotKey(_hwnd, ID_HK_SCROLL, AppSettings.HK_Scroll.Modifiers, AppSettings.HK_Scroll.Key);
            Win32.RegisterHotKey(_hwnd, ID_HK_SCREEN, AppSettings.HK_Screen.Modifiers, AppSettings.HK_Screen.Key);
            Win32.RegisterHotKey(_hwnd, ID_HK_RECORD, AppSettings.HK_Record.Modifiers, AppSettings.HK_Record.Key);

            UpdateContextMenu();
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == ID_HK_AREA) { StartAreaCapture(); handled = true; }
                else if (id == ID_HK_SMART) { StartSmartCapture(); handled = true; }
                else if (id == ID_HK_SCROLL) { StartScrollCapture(); handled = true; }
                else if (id == ID_HK_SCREEN) { StartScreenCapture(); handled = true; }
                else if (id == ID_HK_RECORD) { StartAreaCapture(); handled = true; }
            }
            return IntPtr.Zero;
        }

        public static Bitmap CaptureEntireScreen()
        {
            int w = (int)SystemParameters.VirtualScreenWidth;
            int h = (int)SystemParameters.VirtualScreenHeight;
            int x = (int)SystemParameters.VirtualScreenLeft;
            int y = (int)SystemParameters.VirtualScreenTop;

            var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h), CopyPixelOperation.SourceCopy);
            }
            return bmp;
        }

        public void StartAreaCapture()
        {
            var bmp = CaptureEntireScreen();
            new QScreenOverlayWindow(bmp, isSmartMode: false).Show();
        }

        public void StartSmartCapture()
        {
            var bmp = CaptureEntireScreen();
            new QScreenOverlayWindow(bmp, isSmartMode: true).Show();
        }

        public void StartScrollCapture()
        {
            var bmp = CaptureEntireScreen();
            new QScreenOverlayWindow(bmp, isSmartMode: false).Show();
        }

        public void StartScreenCapture()
        {
            var bmp = CaptureEntireScreen();
            new QScreenEditorWindow(bmp).Show();
        }

        public void ShowSettings()
        {
            if (_settingsWindow == null || !_settingsWindow.IsLoaded)
            {
                _settingsWindow = new SettingsWindow(this);
            }
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }

        private void ExitApp()
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            Application.Current.Shutdown();
        }
    }

    public class HotkeyRecorderControl : Button
    {
        private HotkeyBinding _binding;
        private bool _isRecording = false;
        private Action _onChanged;

        public HotkeyRecorderControl(HotkeyBinding binding, Action onChanged)
        {
            _binding = binding;
            _onChanged = onChanged;

            Background = new SolidColorBrush(Color.FromRgb(40, 44, 52));
            Foreground = Brushes.White;
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));
            BorderThickness = new Thickness(1);
            Padding = new Thickness(10, 4, 10, 4);
            MinWidth = 180;
            Height = 28;
            Cursor = Cursors.Hand;
            FontWeight = FontWeights.SemiBold;
            FontSize = 11;
            HorizontalContentAlignment = HorizontalAlignment.Center;
            VerticalContentAlignment = VerticalAlignment.Center;

            UpdateDisplay();

            Click += (s, e) => StartRecording();
            LostFocus += (s, e) => StopRecording(cancel: true);
            PreviewKeyDown += OnPreviewKeyDown;
        }

        private void UpdateDisplay()
        {
            if (_isRecording)
            {
                Content = "Нажмите клавиши...";
                Background = new SolidColorBrush(Color.FromRgb(36, 120, 220));
                BorderBrush = Brushes.DodgerBlue;
            }
            else
            {
                Content = _binding.DisplayText;
                Background = new SolidColorBrush(Color.FromRgb(40, 44, 52));
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));
            }
        }

        private void StartRecording()
        {
            _isRecording = true;
            UpdateDisplay();
            Focus();
        }

        private void StopRecording(bool cancel)
        {
            _isRecording = false;
            UpdateDisplay();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_isRecording) return;
            e.Handled = true;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.Escape)
            {
                StopRecording(cancel: true);
                return;
            }

            if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LWin || key == Key.RWin)
            {
                return;
            }

            uint mod = 0;
            var parts = new List<string>();

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) { mod |= Win32.MOD_CONTROL; parts.Add("Ctrl"); }
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) { mod |= Win32.MOD_SHIFT; parts.Add("Shift"); }
            if ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt) { mod |= Win32.MOD_ALT; parts.Add("Alt"); }
            if ((Keyboard.Modifiers & ModifierKeys.Windows) == ModifierKeys.Windows) { mod |= Win32.MOD_WIN; parts.Add("Win"); }

            var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            parts.Add(key.ToString());

            _binding.Modifiers = mod;
            _binding.Key = vk;
            _binding.DisplayText = string.Join(" + ", parts);

            StopRecording(cancel: false);
            _onChanged?.Invoke();
        }
    }

    public class SettingsWindow : Window
    {
        private AppController _controller;
        private TextBlock _previewText = new();
        private StackPanel _jpgQualityPanel = new();

        public SettingsWindow(AppController controller)
        {
            _controller = controller;
            Title = "Настройки";
            Width = 510;
            Height = 580;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(28, 30, 36));
            Foreground = Brushes.White;
            ResizeMode = ResizeMode.NoResize;

            Win32.ApplyDarkMode(this);
            Icon = AppIconProvider.GetImageSource();

            BuildUI();
        }

        private void BuildUI()
        {
            var mainStack = new StackPanel { Margin = new Thickness(18) };

            mainStack.Children.Add(new TextBlock { Text = "Горячие клавиши", FontSize = 13, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) });

            var gridHotkeys = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            gridHotkeys.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            gridHotkeys.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int i = 0; i < 5; i++) gridHotkeys.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });

            AddHotkeyRow(gridHotkeys, 0, "Захват области:", AppSettings.HK_Area);
            AddHotkeyRow(gridHotkeys, 1, "Умный захват (Окно / Зона):", AppSettings.HK_Smart);
            AddHotkeyRow(gridHotkeys, 2, "Скролл-скриншот:", AppSettings.HK_Scroll);
            AddHotkeyRow(gridHotkeys, 3, "Весь экран:", AppSettings.HK_Screen);
            AddHotkeyRow(gridHotkeys, 4, "Запись видео (Зона):", AppSettings.HK_Record);

            mainStack.Children.Add(gridHotkeys);
            mainStack.Children.Add(CreateDivider());

            mainStack.Children.Add(new TextBlock { Text = "Формат и имя сохраняемых файлов", FontSize = 13, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 8) });

            var fmtPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            fmtPanel.Children.Add(new TextBlock { Text = "Формат по умолчанию:", Width = 160, Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
            var cbFormat = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
            cbFormat.Items.Add("PNG (Без потерь)");
            cbFormat.Items.Add("JPG (Компактный размер)");
            cbFormat.Items.Add("PDF (Документ)");
            cbFormat.SelectedIndex = AppSettings.DefaultFormat switch { "jpg" => 1, "pdf" => 2, _ => 0 };
            cbFormat.SelectionChanged += (s, e) =>
            {
                AppSettings.DefaultFormat = cbFormat.SelectedIndex switch { 1 => "jpg", 2 => "pdf", _ => "png" };
                _jpgQualityPanel.Visibility = AppSettings.DefaultFormat == "jpg" ? Visibility.Visible : Visibility.Collapsed;
                AppSettings.Save();
                UpdatePreview();
            };
            fmtPanel.Children.Add(cbFormat);
            mainStack.Children.Add(fmtPanel);

            _jpgQualityPanel.Orientation = Orientation.Horizontal;
            _jpgQualityPanel.Margin = new Thickness(0, 0, 0, 6);
            _jpgQualityPanel.Visibility = AppSettings.DefaultFormat == "jpg" ? Visibility.Visible : Visibility.Collapsed;
            _jpgQualityPanel.Children.Add(new TextBlock { Text = "Качество JPG:", Width = 160, Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
            var sliderQ = new Slider { Width = 160, Minimum = 50, Maximum = 100, Value = AppSettings.JpegQuality * 100, VerticalAlignment = VerticalAlignment.Center };
            var lblQ = new TextBlock { Text = $"{(int)sliderQ.Value}%", Width = 50, Margin = new Thickness(10, 0, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(50, 180, 255)), FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
            sliderQ.ValueChanged += (s, e) =>
            {
                AppSettings.JpegQuality = sliderQ.Value / 100.0;
                lblQ.Text = $"{(int)sliderQ.Value}%";
                AppSettings.Save();
            };
            _jpgQualityPanel.Children.Add(sliderQ);
            _jpgQualityPanel.Children.Add(lblQ);
            mainStack.Children.Add(_jpgQualityPanel);

            var pfxPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            pfxPanel.Children.Add(new TextBlock { Text = "Префикс:", Width = 160, Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
            var tbPrefix = new TextBox { Text = AppSettings.FilenamePrefix, Width = 220, HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Color.FromRgb(40, 44, 52)), Foreground = Brushes.White, BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, Padding = new Thickness(4, 2, 4, 2) };
            tbPrefix.TextChanged += (s, e) =>
            {
                AppSettings.FilenamePrefix = tbPrefix.Text;
                AppSettings.Save();
                UpdatePreview();
            };
            pfxPanel.Children.Add(tbPrefix);
            mainStack.Children.Add(pfxPanel);

            var datePanel = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            datePanel.Children.Add(new TextBlock { Text = "Формат даты:", Width = 160, Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
            var cbDate = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
            cbDate.Items.Add("ДД.ММ.ГГГГ_ЧЧ.мм.сс");
            cbDate.Items.Add("ГГГГ-ММ-ДД_ЧЧ-мм-сс");
            cbDate.Items.Add("ГГГГММДД_ЧЧммсс");
            cbDate.Items.Add("ДД-ММ-ГГГГ_ЧЧ-мм-сс");
            cbDate.Items.Add("Unix Timestamp");
            cbDate.SelectedIndex = AppSettings.DateFormat switch
            {
                "yyyy-MM-dd_HH-mm-ss" => 1,
                "yyyyMMdd_HHmmss" => 2,
                "dd-MM-yyyy_HH-mm-ss" => 3,
                "unix" => 4,
                _ => 0
            };
            cbDate.SelectionChanged += (s, e) =>
            {
                AppSettings.DateFormat = cbDate.SelectedIndex switch
                {
                    1 => "yyyy-MM-dd_HH-mm-ss",
                    2 => "yyyyMMdd_HHmmss",
                    3 => "dd-MM-yyyy_HH-mm-ss",
                    4 => "unix",
                    _ => "dd.MM.yyyy_HH.mm.ss"
                };
                AppSettings.Save();
                UpdatePreview();
            };
            datePanel.Children.Add(cbDate);
            mainStack.Children.Add(datePanel);

            var prevPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 8) };
            prevPanel.Children.Add(new TextBlock { Text = "Пример имени: ", FontSize = 11, Foreground = Brushes.Gray });
            _previewText.FontSize = 11;
            _previewText.FontWeight = FontWeights.SemiBold;
            _previewText.Foreground = new SolidColorBrush(Color.FromRgb(60, 150, 255));
            prevPanel.Children.Add(_previewText);
            mainStack.Children.Add(prevPanel);
            UpdatePreview();

            mainStack.Children.Add(CreateDivider());

            mainStack.Children.Add(new TextBlock { Text = "Папка для сохранения", FontSize = 13, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 6) });

            var fldPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            fldPanel.Children.Add(new TextBlock { Text = "Папка:", Width = 160, Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
            var txtFolder = new TextBlock { Text = System.IO.Path.GetFileName(AppSettings.SaveFolder), Width = 140, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            var btnBrowse = new Button { Content = "Выбрать...", Width = 80, Padding = new Thickness(4, 2, 4, 2), Cursor = Cursors.Hand };
            btnBrowse.Click += (s, e) =>
            {
                using var dialog = new Forms.FolderBrowserDialog();
                dialog.SelectedPath = AppSettings.SaveFolder;
                if (dialog.ShowDialog() == Forms.DialogResult.OK)
                {
                    AppSettings.SaveFolder = dialog.SelectedPath;
                    txtFolder.Text = System.IO.Path.GetFileName(dialog.SelectedPath);
                    AppSettings.Save();
                }
            };
            fldPanel.Children.Add(txtFolder);
            fldPanel.Children.Add(btnBrowse);
            mainStack.Children.Add(fldPanel);

            var chkDirect = new CheckBox { Content = "Сохранять сразу в папку (без диалогового окна)", IsChecked = AppSettings.DirectSave, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 8) };
            chkDirect.Checked += (s, e) => { AppSettings.DirectSave = true; AppSettings.Save(); };
            chkDirect.Unchecked += (s, e) => { AppSettings.DirectSave = false; AppSettings.Save(); };
            mainStack.Children.Add(chkDirect);

            mainStack.Children.Add(CreateDivider());

            mainStack.Children.Add(new TextBlock { Text = "Действия и система", FontSize = 13, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 6) });

            var chkThumb = new CheckBox { Content = "Показывать миниатюру в углу экрана", IsChecked = AppSettings.ShowThumbnail, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 6) };
            chkThumb.Checked += (s, e) => { AppSettings.ShowThumbnail = true; AppSettings.Save(); };
            chkThumb.Unchecked += (s, e) => { AppSettings.ShowThumbnail = false; AppSettings.Save(); };
            mainStack.Children.Add(chkThumb);

            var chkStartup = new CheckBox { Content = "Запуск при входе в Windows", IsChecked = AppSettings.IsStartupEnabled(), Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 10) };
            chkStartup.Checked += (s, e) => AppSettings.SetStartup(true);
            chkStartup.Unchecked += (s, e) => AppSettings.SetStartup(false);
            mainStack.Children.Add(chkStartup);

            var btnCheckUpdates = new Button
            {
                Content = "🔄 Проверить обновления...",
                Background = new SolidColorBrush(Color.FromRgb(44, 48, 58)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 4, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 8)
            };
            btnCheckUpdates.Click += async (s, e) => await UpdateChecker.CheckForUpdatesAsync(isUserInitiated: true);
            mainStack.Children.Add(btnCheckUpdates);

            mainStack.Children.Add(CreateDivider());

            mainStack.Children.Add(new TextBlock
            {
                Text = $"QScreen v{UpdateChecker.CurrentVersion} (Build 9.3.3)",
                FontSize = 11,
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0)
            });

            Content = mainStack;
        }

        private void AddHotkeyRow(Grid grid, int row, string label, HotkeyBinding binding)
        {
            var lbl = new TextBlock { Text = label, Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center };
            var ctrl = new HotkeyRecorderControl(binding, () =>
            {
                AppSettings.Save();
                _controller.SetupHotkeys();
            });

            Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0);
            Grid.SetRow(ctrl, row); Grid.SetColumn(ctrl, 1);
            grid.Children.Add(lbl); grid.Children.Add(ctrl);
        }

        private void UpdatePreview()
        {
            _previewText.Text = AppSettings.GenerateFileName();
        }

        private Separator CreateDivider()
        {
            return new Separator { Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), Margin = new Thickness(0, 4, 0, 4) };
        }
    }

    public class QScreenOverlayWindow : Window
    {
        private Bitmap _screenBitmap;
        private bool _isSmartMode;
        private List<WindowTarget> _windows = new();
        private WindowTarget? _hoveredWindow;

        private Point _startPoint;
        private Point _currentPoint;
        private bool _isDragging = false;

        public QScreenOverlayWindow(Bitmap screenBitmap, bool isSmartMode = false)
        {
            _screenBitmap = screenBitmap;
            _isSmartMode = isSmartMode;

            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
            WindowStyle = WindowStyle.None;
            Topmost = true;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Cursor = Cursors.None;

            if (_isSmartMode)
            {
                _windows = WindowDetector.GetVisibleWindows();
            }

            MouseDown += (s, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    _startPoint = e.GetPosition(this);
                    _currentPoint = _startPoint;
                    _isDragging = false;
                    InvalidateVisual();
                }
            };

            MouseMove += (s, e) =>
            {
                _currentPoint = e.GetPosition(this);
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    if (Math.Abs(_currentPoint.X - _startPoint.X) > 5 || Math.Abs(_currentPoint.Y - _startPoint.Y) > 5)
                    {
                        _isDragging = true;
                        _hoveredWindow = null;
                    }
                }
                else if (_isSmartMode && !_isDragging)
                {
                    _hoveredWindow = _windows.Find(w => w.Bounds.Contains(_currentPoint));
                }
                InvalidateVisual();
            };

            MouseUp += (s, e) =>
            {
                var rect = GetSelectionRect();
                if (_isDragging && rect.Width > 5 && rect.Height > 5)
                {
                    Close();
                    var cropped = CropBitmap(_screenBitmap, rect);
                    new QScreenEditorWindow(cropped).Show();
                }
                else if (_isSmartMode && _hoveredWindow.HasValue)
                {
                    Close();
                    var targetWin = _hoveredWindow.Value;
                    var isolatedBmp = WindowDetector.CaptureWindowFast(targetWin.Bounds);
                    new QScreenEditorWindow(isolatedBmp).Show();
                }
                else if (rect.Width > 5 && rect.Height > 5)
                {
                    Close();
                    var cropped = CropBitmap(_screenBitmap, rect);
                    new QScreenEditorWindow(cropped).Show();
                }
            };

            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
        }

        private Rect GetSelectionRect()
        {
            double x = Math.Min(_startPoint.X, _currentPoint.X);
            double y = Math.Min(_startPoint.Y, _currentPoint.Y);
            double w = Math.Abs(_startPoint.X - _currentPoint.X);
            double h = Math.Abs(_startPoint.Y - _currentPoint.Y);
            return new Rect(x, y, w, h);
        }

        private Bitmap CropBitmap(Bitmap src, Rect rect)
        {
            var target = new Bitmap((int)rect.Width, (int)rect.Height);
            using (var g = Graphics.FromImage(target))
            {
                g.DrawImage(src, new System.Drawing.Rectangle(0, 0, (int)rect.Width, (int)rect.Height),
                    (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height, GraphicsUnit.Pixel);
            }
            return target;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, new Rect(0, 0, ActualWidth, ActualHeight));

            if (!_isDragging && _hoveredWindow.HasValue)
            {
                var win = _hoveredWindow.Value;
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(40, 0, 120, 255)), new Pen(Brushes.DodgerBlue, 2.5), win.Bounds, 6, 6);
            }

            if (_isDragging)
            {
                var rect = GetSelectionRect();
                dc.DrawRectangle(Brushes.Transparent, new Pen(Brushes.White, 1.5), rect);
            }

            DrawReticle(dc, _currentPoint);
        }

        private void DrawReticle(DrawingContext dc, Point pt)
        {
            var cyanPen = new Pen(new SolidColorBrush(Color.FromArgb(230, 50, 180, 255)), 1.5);
            var whitePen = new Pen(Brushes.White, 1.5);

            dc.DrawEllipse(null, cyanPen, pt, 10, 10);
            dc.DrawLine(whitePen, new Point(pt.X - 20, pt.Y), new Point(pt.X - 4, pt.Y));
            dc.DrawLine(whitePen, new Point(pt.X + 4, pt.Y), new Point(pt.X + 20, pt.Y));
            dc.DrawLine(whitePen, new Point(pt.X, pt.Y - 20), new Point(pt.X, pt.Y - 4));
            dc.DrawLine(whitePen, new Point(pt.X, pt.Y + 4), new Point(pt.X, pt.Y + 20));
            dc.DrawEllipse(Brushes.Crimson, null, pt, 2, 2);

            string coordText = _isDragging 
                ? $"W: {(int)GetSelectionRect().Width}\nH: {(int)GetSelectionRect().Height}"
                : $"{(int)pt.X}\n{(int)pt.Y}";

            var ft = new FormattedText(
                coordText,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Consolas"),
                10,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip
            );

            var badgeRect = new Rect(pt.X + 14, pt.Y - 28, ft.Width + 12, 30);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(200, 20, 22, 26)), new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), 1), badgeRect, 4, 4);
            dc.DrawText(ft, new Point(badgeRect.X + 6, badgeRect.Y + 2));
        }
    }

    public class QScreenEditorWindow : Window
    {
        private Bitmap _baseBitmap;
        private Canvas _canvas = new();
        private Grid _rootGrid = new();
        private Border _canvasContainer = new();
        private StackPanel _mainToolBar = new();
        private string _selectedTool = "arrow";
        private Color _selectedColor = Color.FromRgb(255, 45, 85);
        private double _strokeWidth = 4.0;
        private int _stepCounter = 1;
        private string _exportFormat = AppSettings.DefaultFormat;

        private bool _beautifyEnabled = false;
        private int _gradientIndex = 0;
        private readonly LinearGradientBrush[] _gradientPresets = new[]
        {
            new LinearGradientBrush(Color.FromRgb(115, 51, 242), Color.FromRgb(217, 64, 166), new Point(0,0), new Point(1,1)),
            new LinearGradientBrush(Color.FromRgb(250, 102, 64), Color.FromRgb(217, 38, 140), new Point(0,0), new Point(1,1)),
            new LinearGradientBrush(Color.FromRgb(26, 153, 242), Color.FromRgb(38, 217, 191), new Point(0,0), new Point(1,1)),
            new LinearGradientBrush(Color.FromRgb(41, 46, 56), Color.FromRgb(20, 23, 28), new Point(0,0), new Point(1,1)),
            new LinearGradientBrush(Color.FromArgb(40, 255, 255, 255), Color.FromArgb(15, 255, 255, 255), new Point(0,0), new Point(1,1))
        };

        private Point? _dragStart;
        private Point _dragButtonStart;
        private bool _isDraggingFile = false;
        private UIElement? _previewElement;
        private List<UIElement> _undoStack = new();

        public QScreenEditorWindow(Bitmap bitmap)
        {
            _baseBitmap = bitmap;
            Title = "QScreen Studio";
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(24, 26, 32));
            Width = Math.Max(bitmap.Width + 120, 920);
            Height = Math.Max(bitmap.Height + 180, 560);

            Win32.ApplyDarkMode(this);
            Icon = AppIconProvider.GetImageSource();

            InjectStyles();
            BuildUI();
        }

        private void InjectStyles()
        {
            var scrollStyle = new Style(typeof(ScrollViewer));
            scrollStyle.Setters.Add(new Setter(ScrollViewer.BackgroundProperty, new SolidColorBrush(Color.FromRgb(45, 50, 58))));
            Resources.Add(typeof(ScrollViewer), scrollStyle);
        }

        private void BuildUI()
        {
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(60) });

            var topCard = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 34, 42)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(12, 8, 12, 4),
                Padding = new Thickness(8, 4, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            _mainToolBar.Orientation = Orientation.Horizontal;

            _mainToolBar.Children.Add(CreateFluentToolButton("↗", "arrow", "Стрелка"));
            _mainToolBar.Children.Add(CreateFluentToolButton("▢", "rect", "Прямоугольник / Рамка"));
            _mainToolBar.Children.Add(CreateFluentToolButton("◯", "ellipse", "Эллипс / Круг"));
            _mainToolBar.Children.Add(CreateFluentToolButton("T", "text", "Текст"));
            _mainToolBar.Children.Add(CreateFluentToolButton("💬", "bubble", "Выноска / Speech Bubble"));
            _mainToolBar.Children.Add(CreateFluentToolButton("①", "step", "Нумерованные шаги"));
            _mainToolBar.Children.Add(CreateFluentToolButton("🖍", "highlighter", "Маркер-хайлайтер"));
            _mainToolBar.Children.Add(CreateFluentToolButton("░", "blur", "Цензура / Размытие"));
            _mainToolBar.Children.Add(CreateFluentToolButton("📏", "ruler", "Линейка с замером px"));
            _mainToolBar.Children.Add(CreateFluentToolButton("✏", "pen", "Карандаш"));

            _mainToolBar.Children.Add(CreateSeparator());

            _mainToolBar.Children.Add(CreateWidthButton(2.0));
            _mainToolBar.Children.Add(CreateWidthButton(4.0));
            _mainToolBar.Children.Add(CreateWidthButton(8.0));

            _mainToolBar.Children.Add(CreateSeparator());

            _mainToolBar.Children.Add(CreateColorSwatch(Color.FromRgb(255, 45, 85)));
            _mainToolBar.Children.Add(CreateColorSwatch(Color.FromRgb(50, 180, 255)));
            _mainToolBar.Children.Add(CreateColorSwatch(Color.FromRgb(52, 199, 89)));
            _mainToolBar.Children.Add(CreateColorSwatch(Color.FromRgb(255, 204, 0)));
            _mainToolBar.Children.Add(CreateColorSwatch(Color.FromRgb(255, 255, 255)));

            _mainToolBar.Children.Add(CreateSeparator());

            _mainToolBar.Children.Add(CreateActionButton("✨ Фон", () => ToggleBeautify(), "Beautify / Градиент"));

            topCard.Child = _mainToolBar;
            Grid.SetRow(topCard, 0);
            _rootGrid.Children.Add(topCard);

            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(45, 50, 58))
            };

            _canvas.Width = _baseBitmap.Width;
            _canvas.Height = _baseBitmap.Height;
            _canvas.Background = new ImageBrush(BitmapToImageSource(_baseBitmap));
            _canvas.MouseDown += Canvas_MouseDown;
            _canvas.MouseMove += Canvas_MouseMove;
            _canvas.MouseUp += Canvas_MouseUp;

            _canvasContainer.Child = _canvas;
            _canvasContainer.HorizontalAlignment = HorizontalAlignment.Center;
            _canvasContainer.VerticalAlignment = VerticalAlignment.Center;
            _canvasContainer.Margin = new Thickness(24);
            _canvasContainer.Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Opacity = 0.35 };

            scroll.Content = _canvasContainer;
            Grid.SetRow(scroll, 1);
            _rootGrid.Children.Add(scroll);

            var bottomCard = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 34, 42)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(12, 6, 12, 10),
                Padding = new Thickness(12, 4, 12, 4),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var bottomStack = new StackPanel { Orientation = Orientation.Horizontal };

            var btnConfirm = new Button
            {
                Content = "✔ Готово (Ctrl+C)",
                Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Padding = new Thickness(16, 6, 16, 6),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand
            };
            btnConfirm.Click += (s, e) => CopyToClipboard();
            bottomStack.Children.Add(btnConfirm);

            var btnCancel = new Button
            {
                Content = "✖",
                Background = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                Padding = new Thickness(10, 6, 10, 6),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 12, 0),
                Cursor = Cursors.Hand
            };
            btnCancel.Click += (s, e) => Close();
            bottomStack.Children.Add(btnCancel);

            bottomStack.Children.Add(CreateActionButton("💾 Сохранить", () => HandleSave(), "Сохранить файл (Ctrl+S)"));
            bottomStack.Children.Add(CreateFormatButton());
            bottomStack.Children.Add(CreateDragDropButton());
            bottomStack.Children.Add(CreateActionButton("📌 Pin", () => PinScreenshot(), "Закрепить поверх окон"));
            bottomStack.Children.Add(CreateActionButton("🔍 OCR", () => RunWindowsOCR(), "Распознать текст"));
            bottomStack.Children.Add(CreateActionButton("↩ Undo", () => Undo(), "Отменить (Ctrl+Z)"));

            bottomCard.Child = bottomStack;
            Grid.SetRow(bottomCard, 2);
            _rootGrid.Children.Add(bottomCard);

            Content = _rootGrid;

            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) Undo();
                if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) CopyToClipboard();
                if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) HandleSave();
                if (e.Key == Key.Escape) Close();
            };
        }

        private void ToggleBeautify()
        {
            _beautifyEnabled = !_beautifyEnabled;
            if (_beautifyEnabled)
            {
                _canvasContainer.Padding = new Thickness(36);
                _canvasContainer.Background = _gradientPresets[_gradientIndex];
                _canvasContainer.CornerRadius = new CornerRadius(12);
                _canvasContainer.Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 10, Opacity = 0.55 };
                _gradientIndex = (_gradientIndex + 1) % _gradientPresets.Length;
            }
            else
            {
                _canvasContainer.Padding = new Thickness(0);
                _canvasContainer.Background = Brushes.Transparent;
                _canvasContainer.CornerRadius = new CornerRadius(0);
                _canvasContainer.Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Opacity = 0.35 };
            }
        }

        private Button CreateFluentToolButton(string icon, string toolName, string tip)
        {
            var btn = new Button
            {
                Content = icon,
                ToolTip = tip,
                Foreground = Brushes.White,
                Background = _selectedTool == toolName ? new SolidColorBrush(Color.FromRgb(36, 120, 220)) : Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand
            };
            btn.Click += (s, e) =>
            {
                _selectedTool = toolName;
                foreach (var child in _mainToolBar.Children)
                {
                    if (child is Button b && b.Tag?.ToString() == "Tool")
                    {
                        b.Background = Brushes.Transparent;
                    }
                }
                btn.Background = new SolidColorBrush(Color.FromRgb(36, 120, 220));
            };
            btn.Tag = "Tool";
            return btn;
        }

        private Button CreateActionButton(string text, Action action, string tip)
        {
            var btn = new Button
            {
                Content = text,
                ToolTip = tip,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(44, 48, 58)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(3, 0, 3, 0),
                Cursor = Cursors.Hand,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            };
            btn.Click += (s, e) => action();
            return btn;
        }

        private Button CreateDragDropButton()
        {
            var btn = new Button
            {
                Content = "🖐 Drag",
                ToolTip = "Перетащить файл в чат/папку",
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(44, 48, 58)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(3, 0, 3, 0),
                Cursor = Cursors.Hand,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            };

            btn.PreviewMouseLeftButtonDown += (s, e) =>
            {
                _dragButtonStart = e.GetPosition(null);
            };

            btn.PreviewMouseMove += (s, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed && !_isDraggingFile)
                {
                    Point current = e.GetPosition(null);
                    Vector diff = _dragButtonStart - current;
                    if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                        Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                    {
                        _isDraggingFile = true;
                        try
                        {
                            var bmp = RenderFinalBitmap();
                            var fileName = AppSettings.GenerateFileName(_exportFormat);
                            var tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), fileName);
                            bmp.Save(tempFile);

                            var data = new DataObject();
                            data.SetData(DataFormats.FileDrop, new string[] { tempFile });
                            data.SetData("FileDrop", new string[] { tempFile });

                            DragDrop.DoDragDrop(btn, data, DragDropEffects.Copy);
                        }
                        catch { }
                        finally
                        {
                            _isDraggingFile = false;
                        }
                    }
                }
            };
            return btn;
        }

        private Button CreateFormatButton()
        {
            var btn = CreateActionButton(_exportFormat.ToUpper(), () => { }, "Выбрать формат файла");
            var menu = new ContextMenu();
            foreach (var fmt in new[] { "png", "jpg", "pdf" })
            {
                var item = new MenuItem { Header = fmt.ToUpper() };
                item.Click += (s, e) => { _exportFormat = fmt; btn.Content = fmt.ToUpper(); };
                menu.Items.Add(item);
            }
            btn.Click += (s, e) => menu.IsOpen = true;
            return btn;
        }

        private Button CreateWidthButton(double w)
        {
            var btn = new Button
            {
                Content = new Ellipse { Width = w + 3, Height = w + 3, Fill = Brushes.White },
                ToolTip = $"Толщина {w}px",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand
            };
            btn.Click += (s, e) => _strokeWidth = w;
            return btn;
        }

        private Button CreateColorSwatch(Color c)
        {
            var btn = new Button
            {
                Content = new Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(c) },
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand
            };
            btn.Click += (s, e) => _selectedColor = c;
            return btn;
        }

        private Border CreateSeparator()
        {
            return new Border { Width = 1, Height = 20, Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), Margin = new Thickness(6, 0, 6, 0) };
        }

        private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _dragStart = e.GetPosition(_canvas);
                var pt = _dragStart.Value;

                if (_selectedTool == "step")
                {
                    var badge = new Border
                    {
                        Width = 26, Height = 26,
                        CornerRadius = new CornerRadius(13),
                        Background = new SolidColorBrush(_selectedColor),
                        Child = new TextBlock
                        {
                            Text = $"{_stepCounter++}",
                            Foreground = Brushes.White,
                            FontWeight = FontWeights.Bold,
                            FontSize = 13,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    };
                    Canvas.SetLeft(badge, pt.X - 13);
                    Canvas.SetTop(badge, pt.Y - 13);
                    AddElement(badge);
                    _dragStart = null;
                }
                else if (_selectedTool == "text")
                {
                    var tb = new TextBox
                    {
                        Background = new SolidColorBrush(Color.FromArgb(220, 0, 0, 0)),
                        Foreground = new SolidColorBrush(_selectedColor),
                        FontWeight = FontWeights.Bold,
                        FontSize = 16,
                        BorderThickness = new Thickness(1.5),
                        BorderBrush = new SolidColorBrush(_selectedColor),
                        Padding = new Thickness(6, 2, 6, 2),
                        MinWidth = 60
                    };
                    Canvas.SetLeft(tb, pt.X);
                    Canvas.SetTop(tb, pt.Y);
                    AddElement(tb);
                    tb.Focus();
                    _dragStart = null;
                }
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragStart.HasValue && e.LeftButton == MouseButtonState.Pressed)
            {
                var start = _dragStart.Value;
                var curr = e.GetPosition(_canvas);

                if (_selectedTool == "pen")
                {
                    var line = new Line { X1 = start.X, Y1 = start.Y, X2 = curr.X, Y2 = curr.Y, Stroke = new SolidColorBrush(_selectedColor), StrokeThickness = _strokeWidth, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                    AddElement(line);
                    _dragStart = curr;
                }
                else if (_selectedTool == "highlighter")
                {
                    var color = Color.FromArgb(90, _selectedColor.R, _selectedColor.G, _selectedColor.B);
                    var line = new Line { X1 = start.X, Y1 = start.Y, X2 = curr.X, Y2 = curr.Y, Stroke = new SolidColorBrush(color), StrokeThickness = 16, StrokeStartLineCap = PenLineCap.Square, StrokeEndLineCap = PenLineCap.Square };
                    AddElement(line);
                    _dragStart = curr;
                }
                else
                {
                    UpdateLiveShapePreview(start, curr);
                }
            }
        }

        private void UpdateLiveShapePreview(Point start, Point end)
        {
            if (_previewElement != null)
            {
                _canvas.Children.Remove(_previewElement);
                _previewElement = null;
            }

            double x = Math.Min(start.X, end.X);
            double y = Math.Min(start.Y, end.Y);
            double w = Math.Abs(start.X - end.X);
            double h = Math.Abs(start.Y - end.Y);

            if (_selectedTool == "rect")
            {
                var rect = new Rectangle { Width = w, Height = h, Stroke = new SolidColorBrush(_selectedColor), StrokeThickness = _strokeWidth, RadiusX = 4, RadiusY = 4 };
                Canvas.SetLeft(rect, x); Canvas.SetTop(rect, y);
                _canvas.Children.Add(rect);
                _previewElement = rect;
            }
            else if (_selectedTool == "ellipse")
            {
                var el = new Ellipse { Width = w, Height = h, Stroke = new SolidColorBrush(_selectedColor), StrokeThickness = _strokeWidth };
                Canvas.SetLeft(el, x); Canvas.SetTop(el, y);
                _canvas.Children.Add(el);
                _previewElement = el;
            }
            else if (_selectedTool == "arrow")
            {
                var group = new Canvas();
                var line = new Line { X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y, Stroke = new SolidColorBrush(_selectedColor), StrokeThickness = _strokeWidth };
                group.Children.Add(line);

                double angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
                double len = 16;
                var p1 = new Point(end.X - len * Math.Cos(angle - Math.PI / 6), end.Y - len * Math.Sin(angle - Math.PI / 6));
                var p2 = new Point(end.X - len * Math.Cos(angle + Math.PI / 6), end.Y - len * Math.Sin(angle + Math.PI / 6));

                group.Children.Add(new Line { X1 = end.X, Y1 = end.Y, X2 = p1.X, Y2 = p1.Y, Stroke = new SolidColorBrush(_selectedColor), StrokeThickness = _strokeWidth });
                group.Children.Add(new Line { X1 = end.X, Y1 = end.Y, X2 = p2.X, Y2 = p2.Y, Stroke = new SolidColorBrush(_selectedColor), StrokeThickness = _strokeWidth });

                _canvas.Children.Add(group);
                _previewElement = group;
            }
            else if (_selectedTool == "ruler")
            {
                var group = new Canvas();
                double dist = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));
                group.Children.Add(new Line { X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y, Stroke = new SolidColorBrush(_selectedColor), StrokeThickness = _strokeWidth });

                var badge = new TextBlock
                {
                    Text = $"{(int)dist}px (W:{(int)w} H:{(int)h})",
                    Foreground = Brushes.White,
                    Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
                    Padding = new Thickness(5, 2, 5, 2),
                    FontSize = 10,
                    FontWeight = FontWeights.Bold
                };
                Canvas.SetLeft(badge, (start.X + end.X) / 2 - 30);
                Canvas.SetTop(badge, (start.Y + end.Y) / 2 - 12);
                group.Children.Add(badge);

                _canvas.Children.Add(group);
                _previewElement = group;
            }
            else if (_selectedTool == "blur")
            {
                var rect = new Rectangle { Width = w, Height = h, Stroke = Brushes.White, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 3, 3 }, Fill = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)) };
                Canvas.SetLeft(rect, x); Canvas.SetTop(rect, y);
                _canvas.Children.Add(rect);
                _previewElement = rect;
            }
            else if (_selectedTool == "bubble")
            {
                var border = new Border { Width = Math.Max(w, 80), Height = Math.Max(h, 40), CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(220, 20, 22, 28)), BorderBrush = new SolidColorBrush(_selectedColor), BorderThickness = new Thickness(2), Padding = new Thickness(8) };
                Canvas.SetLeft(border, x); Canvas.SetTop(border, y);
                _canvas.Children.Add(border);
                _previewElement = border;
            }
        }

        private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragStart.HasValue)
            {
                var start = _dragStart.Value;
                var end = e.GetPosition(_canvas);
                _dragStart = null;

                if (_previewElement != null)
                {
                    _canvas.Children.Remove(_previewElement);
                    _previewElement = null;
                }

                double x = Math.Min(start.X, end.X);
                double y = Math.Min(start.Y, end.Y);
                double w = Math.Abs(start.X - end.X);
                double h = Math.Abs(start.Y - end.Y);

                if (_selectedTool == "rect" && w > 3 && h > 3)
                {
                    var rect = new Rectangle { Width = w, Height = h, Stroke = new SolidColorBrush(_selectedColor), StrokeThickness = _strokeWidth, RadiusX = 4, RadiusY = 4 };
                    Canvas.SetLeft(rect, x); Canvas.SetTop(rect, y);
                    AddElement(rect);
                }
                else if (_selectedTool == "ellipse" && w > 3 && h > 3)
                {
                    var el = new Ellipse { Width = w, Height = h, Stroke = new SolidColorBrush(_selectedColor), StrokeThickness = _strokeWidth };
                    Canvas.SetLeft(el, x); Canvas.SetTop(el, y);
                    AddElement(el);
                }
                else if (_selectedTool == "arrow")
                {
                    var group = new Canvas();
                    group.Children.Add(new Line { X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y, Stroke = new SolidColorBrush(_selectedColor), StrokeThickness = _strokeWidth });

                    double angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
                    double len = 16;
                    var p1 = new Point(end.X - len * Math.Cos(angle - Math.PI / 6), end.Y - len * Math.Sin(angle - Math.PI / 6));
                    var p2 = new Point(end.X - len * Math.Cos(angle + Math.PI / 6), end.Y - len * Math.Sin(angle + Math.PI / 6));

                    group.Children.Add(new Line { X1 = end.X, Y1 = end.Y, X2 = p1.X, Y2 = p1.Y, Stroke = new SolidColorBrush(_selectedColor), StrokeThickness = _strokeWidth });
                    group.Children.Add(new Line { X1 = end.X, Y1 = end.Y, X2 = p2.X, Y2 = p2.Y, Stroke = new SolidColorBrush(_selectedColor), StrokeThickness = _strokeWidth });

                    AddElement(group);
                }
                else if (_selectedTool == "ruler")
                {
                    var group = new Canvas();
                    double dist = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));
                    group.Children.Add(new Line { X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y, Stroke = new SolidColorBrush(_selectedColor), StrokeThickness = _strokeWidth });

                    var badge = new TextBlock
                    {
                        Text = $"{(int)dist}px (W:{(int)w} H:{(int)h})",
                        Foreground = Brushes.White,
                        Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
                        Padding = new Thickness(5, 2, 5, 2),
                        FontSize = 10,
                        FontWeight = FontWeights.Bold
                    };
                    Canvas.SetLeft(badge, (start.X + end.X) / 2 - 30);
                    Canvas.SetTop(badge, (start.Y + end.Y) / 2 - 12);
                    group.Children.Add(badge);

                    AddElement(group);
                }
                else if (_selectedTool == "blur" && w > 5 && h > 5)
                {
                    var pixelated = PixelateRegion(_baseBitmap, new Rect(x, y, w, h), 12);
                    var img = new System.Windows.Controls.Image { Source = BitmapToImageSource(pixelated), Width = w, Height = h };
                    Canvas.SetLeft(img, x); Canvas.SetTop(img, y);
                    AddElement(img);
                }
                else if (_selectedTool == "bubble")
                {
                    var border = new Border { Width = Math.Max(w, 100), Height = Math.Max(h, 45), CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(220, 20, 22, 28)), BorderBrush = new SolidColorBrush(_selectedColor), BorderThickness = new Thickness(2), Padding = new Thickness(6) };
                    var tb = new TextBox { Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0), FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Text = "Текст..." };
                    border.Child = tb;
                    Canvas.SetLeft(border, x); Canvas.SetTop(border, y);
                    AddElement(border);
                    tb.Focus();
                }
            }
        }

        private Bitmap PixelateRegion(Bitmap src, Rect rect, int pixelSize)
        {
            var cropped = new Bitmap((int)rect.Width, (int)rect.Height);
            using (var g = Graphics.FromImage(cropped))
            {
                g.DrawImage(src, new System.Drawing.Rectangle(0, 0, (int)rect.Width, (int)rect.Height), (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height, GraphicsUnit.Pixel);
            }

            var small = new Bitmap(Math.Max(1, (int)rect.Width / pixelSize), Math.Max(1, (int)rect.Height / pixelSize));
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.DrawImage(cropped, 0, 0, small.Width, small.Height);
            }

            var result = new Bitmap((int)rect.Width, (int)rect.Height);
            using (var g = Graphics.FromImage(result))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.DrawImage(small, 0, 0, result.Width, result.Height);
            }
            return result;
        }

        private void AddElement(UIElement elem)
        {
            _canvas.Children.Add(elem);
            _undoStack.Add(elem);
        }

        private void Undo()
        {
            if (_undoStack.Count > 0)
            {
                var last = _undoStack[^1];
                _canvas.Children.Remove(last);
                _undoStack.RemoveAt(_undoStack.Count - 1);
            }
        }

        private Bitmap RenderFinalBitmap()
        {
            FrameworkElement target = _beautifyEnabled ? _canvasContainer : _canvas;
            var rtb = new RenderTargetBitmap((int)target.ActualWidth, (int)target.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(target);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return new Bitmap(ms);
        }

        private void CopyToClipboard()
        {
            var bmp = RenderFinalBitmap();
            Clipboard.SetImage(BitmapToImageSource(bmp));
            Close();
        }

        private void PinScreenshot()
        {
            var bmp = RenderFinalBitmap();
            var pinWin = new Window
            {
                Width = bmp.Width,
                Height = bmp.Height,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false
            };
            var img = new System.Windows.Controls.Image { Source = BitmapToImageSource(bmp) };
            pinWin.Content = img;
            pinWin.MouseDown += (s, e) => { if (e.LeftButton == MouseButtonState.Pressed) pinWin.DragMove(); };
            pinWin.MouseDoubleClick += (s, e) => pinWin.Close();
            pinWin.Show();
            Close();
        }

        private void HandleSave()
        {
            var bmp = RenderFinalBitmap();
            var fileName = AppSettings.GenerateFileName(_exportFormat);

            if (AppSettings.DirectSave)
            {
                var targetPath = System.IO.Path.Combine(AppSettings.SaveFolder, fileName);
                bmp.Save(targetPath);
                Close();
            }
            else
            {
                var sfd = new SaveFileDialog
                {
                    Filter = "PNG Image|*.png|JPEG Image|*.jpg",
                    FileName = fileName,
                    InitialDirectory = AppSettings.SaveFolder
                };
                if (sfd.ShowDialog() == true)
                {
                    bmp.Save(sfd.FileName);
                    Close();
                }
            }
        }

        private async void RunWindowsOCR()
        {
            try
            {
                var bmp = RenderFinalBitmap();
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                ms.Position = 0;

                var randomAccessStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                var outputStream = randomAccessStream.GetOutputStreamAt(0);
                var dw = new Windows.Storage.Streams.DataWriter(outputStream);
                dw.WriteBytes(ms.ToArray());
                await dw.StoreAsync();
                await outputStream.FlushAsync();

                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(randomAccessStream);
                var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

                var ocrEngine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
                if (ocrEngine != null)
                {
                    var ocrResult = await ocrEngine.RecognizeAsync(softwareBitmap);
                    Clipboard.SetText(ocrResult.Text);
                    MessageBox.Show("Текст успешно распознан и скопирован в буфер обмена!", "QScreen OCR", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка OCR: {ex.Message}");
            }
        }

        private BitmapSource BitmapToImageSource(Bitmap bmp)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            return bi;
        }
    }
}
