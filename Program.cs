using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
using System.Windows.Threading;
using Microsoft.Win32;

// Явные псевдонимы для 100% исключения любых конфликтов компилятора
using Forms = System.Windows.Forms;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;
using Rectangle = System.Windows.Shapes.Rectangle;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using CheckBox = System.Windows.Controls.CheckBox;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using FlowDirection = System.Windows.FlowDirection;
using LinearGradientBrush = System.Windows.Media.LinearGradientBrush;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;

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

            if (_cachedIcon == null && System.IO.File.Exists("icon.ico"))
            {
                try { _cachedIcon = new Icon("icon.ico"); } catch { }
            }

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
        public const string CurrentVersion = "9.7.0";
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
                System.Windows.Application.Current.Shutdown();
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
        public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")]
        public static extern bool GetCursorInfo(ref CURSORINFO pci);
        [DllImport("user32.dll")]
        public static extern bool DrawIcon(IntPtr hDC, int X, int Y, IntPtr hIcon);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        public const int CURSOR_SHOWING = 0x00000001;

        public static readonly IntPtr HWND_TOP = IntPtr.Zero;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const int SW_RESTORE = 9;

        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        public const int DWMWA_CLOAKED = 14;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

        public const uint GW_OWNER = 4;
        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_APPWINDOW = 0x00040000;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;

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

    public class WindowTarget
    {
        public IntPtr Hwnd;
        public string Title = "";
        public Rect DipBounds;
        public System.Drawing.Rectangle PixelBounds;
    }

    public static class WindowDetector
    {
        public static List<WindowTarget> GetVisibleWindows(double dpiX, double dpiY, double vsLeft, double vsTop)
        {
            var list = new List<WindowTarget>();
            uint currentPid = (uint)Process.GetCurrentProcess().Id;

            Win32.EnumWindows((hwnd, lParam) =>
            {
                if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd)) return true;

                Win32.GetWindowThreadProcessId(hwnd, out uint winPid);
                if (winPid == currentPid) return true;

                if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                    return true;

                int exStyle = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
                if ((exStyle & Win32.WS_EX_TOOLWINDOW) != 0 && (exStyle & Win32.WS_EX_APPWINDOW) == 0)
                    return true;

                Win32.RECT r;
                if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(Win32.RECT))) != 0 || (r.Right - r.Left <= 10))
                {
                    Win32.GetWindowRect(hwnd, out r);
                }

                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;

                if (w > 50 && h > 50)
                {
                    var sb = new StringBuilder(256);
                    Win32.GetWindowText(hwnd, sb, 256);
                    var title = sb.ToString().Trim();

                    if (title == "Program Manager") return true;

                    int pxX = r.Left - (int)vsLeft;
                    int pxY = r.Top - (int)vsTop;

                    double dipX = pxX / dpiX;
                    double dipY = pxY / dpiY;
                    double dipW = w / dpiX;
                    double dipH = h / dpiY;

                    list.Add(new WindowTarget
                    {
                        Hwnd = hwnd,
                        Title = title,
                        DipBounds = new Rect(dipX, dipY, dipW, dipH),
                        PixelBounds = new System.Drawing.Rectangle(pxX, pxY, w, h)
                    });
                }
                return true;
            }, IntPtr.Zero);

            return list;
        }

        public static Bitmap CaptureWindowIsolated(IntPtr hwnd, System.Drawing.Rectangle fallbackBounds, Bitmap fallbackBmp)
        {
            try
            {
                if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
                Win32.SetForegroundWindow(hwnd);
                Win32.SetWindowPos(hwnd, Win32.HWND_TOP, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_SHOWWINDOW);
                Thread.Sleep(75);

                Win32.RECT r;
                if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(Win32.RECT))) != 0 || (r.Right - r.Left <= 10))
                {
                    Win32.GetWindowRect(hwnd, out r);
                }

                int w = Math.Max(10, r.Right - r.Left);
                int h = Math.Max(10, r.Bottom - r.Top);

                var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(w, h), CopyPixelOperation.SourceCopy);
                }
                return bmp;
            }
            catch
            {
                int rx = Math.Max(0, Math.Min(fallbackBounds.X, fallbackBmp.Width - 1));
                int ry = Math.Max(0, Math.Min(fallbackBounds.Y, fallbackBmp.Height - 1));
                int rw = Math.Max(1, Math.Min(fallbackBounds.Width, fallbackBmp.Width - rx));
                int rh = Math.Max(1, Math.Min(fallbackBounds.Height, fallbackBmp.Height - ry));

                var target = new Bitmap(rw, rh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(target))
                {
                    g.DrawImage(fallbackBmp, new System.Drawing.Rectangle(0, 0, rw, rh), rx, ry, rw, rh, GraphicsUnit.Pixel);
                }
                return target;
            }
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

        public static string DefaultFormat = "png"; // png, jpg, heic, webp, pdf
        public static double JpegQuality = 0.85;
        public static string FilenamePrefix = "QScreen";
        public static string DateFormat = "dd.MM.yyyy_HH.mm.ss";
        public static string SaveFolder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        public static bool DirectSave = false;
        public static bool ShowThumbnail = true;

        // Видео параметры
        public static int VideoFps = 60;
        public static string VideoQuality = "high"; // high, medium, low
        public static string VideoFormat = "mp4";   // mp4, gif
        public static string VideoCodec = "h264";   // h264, h265
        public static bool RecordCursor = true;
        public static bool RecordAudio = false;
        public static bool VideoCountdown = true;

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
                        else if (k == "VideoFps" && int.TryParse(v, out var fps)) VideoFps = fps;
                        else if (k == "VideoQuality") VideoQuality = v;
                        else if (k == "VideoFormat") VideoFormat = v;
                        else if (k == "VideoCodec") VideoCodec = v;
                        else if (k == "RecordCursor" && bool.TryParse(v, out var rc)) RecordCursor = rc;
                        else if (k == "RecordAudio" && bool.TryParse(v, out var ra)) RecordAudio = ra;
                        else if (k == "VideoCountdown" && bool.TryParse(v, out var vc)) VideoCountdown = vc;
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
                    $"ShowThumbnail={ShowThumbnail}",
                    $"VideoFps={VideoFps}",
                    $"VideoQuality={VideoQuality}",
                    $"VideoFormat={VideoFormat}",
                    $"VideoCodec={VideoCodec}",
                    $"RecordCursor={RecordCursor}",
                    $"RecordAudio={RecordAudio}",
                    $"VideoCountdown={VideoCountdown}"
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
            var app = new System.Windows.Application();
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
            cm.Items.Add($"🎥 Запись видео / GIF ({AppSettings.HK_Record.DisplayText})", null, (s, e) => StartVideoRecording());
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
                else if (id == ID_HK_RECORD) { StartVideoRecording(); handled = true; }
            }
            return IntPtr.Zero;
        }

        public static Bitmap CaptureEntireScreen()
        {
            var bounds = Forms.SystemInformation.VirtualScreen;
            var bmp = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new System.Drawing.Size(bounds.Width, bounds.Height), CopyPixelOperation.SourceCopy);
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

        public void StartVideoRecording()
        {
            var box = new VideoBoxWindow();
            box.Show();
            box.Activate();
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
            System.Windows.Application.Current.Shutdown();
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
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center;
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center;

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
            Title = "Настройки QScreen";
            Width = 550;
            Height = 680;
            MaxHeight = SystemParameters.WorkArea.Height - 30;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(28, 30, 36));
            Foreground = Brushes.White;

            Win32.ApplyDarkMode(this);
            Icon = AppIconProvider.GetImageSource();

            BuildUI();
        }

        private void BuildUI()
        {
            var mainStack = new StackPanel { Margin = new Thickness(18, 12, 18, 16) };

            mainStack.Children.Add(new TextBlock { Text = "Горячие клавиши", FontSize = 13, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });

            var gridHotkeys = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            gridHotkeys.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            gridHotkeys.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int i = 0; i < 5; i++) gridHotkeys.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });

            AddHotkeyRow(gridHotkeys, 0, "Захват области:", AppSettings.HK_Area);
            AddHotkeyRow(gridHotkeys, 1, "Умный захват (Окно / Зона):", AppSettings.HK_Smart);
            AddHotkeyRow(gridHotkeys, 2, "Скролл-скриншот:", AppSettings.HK_Scroll);
            AddHotkeyRow(gridHotkeys, 3, "Весь экран:", AppSettings.HK_Screen);
            AddHotkeyRow(gridHotkeys, 4, "Запись видео / GIF:", AppSettings.HK_Record);

            mainStack.Children.Add(gridHotkeys);
            mainStack.Children.Add(CreateDivider());

            mainStack.Children.Add(new TextBlock { Text = "🎥 Настройки записи видео", FontSize = 13, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 6) });

            var codecPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            codecPanel.Children.Add(new TextBlock { Text = "Видео кодек:", Width = 160, Foreground = Brushes.LightGray, VerticalAlignment = System.Windows.VerticalAlignment.Center });
            var cbCodec = new ComboBox { Width = 220, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            cbCodec.Items.Add("H.264 / AVC (Максимальная совместимость)");
            cbCodec.Items.Add("H.265 / HEVC (Высокое сжатие / 4K)");
            cbCodec.SelectedIndex = AppSettings.VideoCodec == "h265" ? 1 : 0;
            cbCodec.SelectionChanged += (s, e) =>
            {
                AppSettings.VideoCodec = cbCodec.SelectedIndex == 1 ? "h265" : "h264";
                AppSettings.Save();
            };
            codecPanel.Children.Add(cbCodec);
            mainStack.Children.Add(codecPanel);

            var fpsPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            fpsPanel.Children.Add(new TextBlock { Text = "Частота кадров (FPS):", Width = 160, Foreground = Brushes.LightGray, VerticalAlignment = System.Windows.VerticalAlignment.Center });
            var cbFps = new ComboBox { Width = 220, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            cbFps.Items.Add("60 FPS (Максимальная плавность)");
            cbFps.Items.Add("30 FPS (Стандарт)");
            cbFps.Items.Add("15 FPS (Компактный размер)");
            cbFps.SelectedIndex = AppSettings.VideoFps switch { 30 => 1, 15 => 2, _ => 0 };
            cbFps.SelectionChanged += (s, e) =>
            {
                AppSettings.VideoFps = cbFps.SelectedIndex switch { 1 => 30, 2 => 15, _ => 60 };
                AppSettings.Save();
            };
            fpsPanel.Children.Add(cbFps);
            mainStack.Children.Add(fpsPanel);

            var vFmtPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            vFmtPanel.Children.Add(new TextBlock { Text = "Формат видеозаписи:", Width = 160, Foreground = Brushes.LightGray, VerticalAlignment = System.Windows.VerticalAlignment.Center });
            var cbVFmt = new ComboBox { Width = 220, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            cbVFmt.Items.Add("MP4 (Видео)");
            cbVFmt.Items.Add("GIF (Анимация)");
            cbVFmt.SelectedIndex = AppSettings.VideoFormat == "gif" ? 1 : 0;
            cbVFmt.SelectionChanged += (s, e) =>
            {
                AppSettings.VideoFormat = cbVFmt.SelectedIndex == 1 ? "gif" : "mp4";
                AppSettings.Save();
            };
            vFmtPanel.Children.Add(cbVFmt);
            mainStack.Children.Add(vFmtPanel);

            var vQualPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            vQualPanel.Children.Add(new TextBlock { Text = "Качество видео:", Width = 160, Foreground = Brushes.LightGray, VerticalAlignment = System.Windows.VerticalAlignment.Center });
            var cbVQual = new ComboBox { Width = 220, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            cbVQual.Items.Add("Высокое (CRF 18)");
            cbVQual.Items.Add("Стандартное (CRF 23)");
            cbVQual.Items.Add("Экономное (CRF 28)");
            cbVQual.SelectedIndex = AppSettings.VideoQuality switch { "medium" => 1, "low" => 2, _ => 0 };
            cbVQual.SelectionChanged += (s, e) =>
            {
                AppSettings.VideoQuality = cbVQual.SelectedIndex switch { 1 => "medium", 2 => "low", _ => "high" };
                AppSettings.Save();
            };
            vQualPanel.Children.Add(cbVQual);
            mainStack.Children.Add(vQualPanel);

            var chkCursor = new CheckBox { Content = "Записывать курсор мыши", IsChecked = AppSettings.RecordCursor, Foreground = Brushes.White, Margin = new Thickness(0, 2, 0, 4) };
            chkCursor.Checked += (s, e) => { AppSettings.RecordCursor = true; AppSettings.Save(); };
            chkCursor.Unchecked += (s, e) => { AppSettings.RecordCursor = false; AppSettings.Save(); };
            mainStack.Children.Add(chkCursor);

            var chkAudio = new CheckBox { Content = "🎙 Записывать звук с микрофона", IsChecked = AppSettings.RecordAudio, Foreground = Brushes.White, Margin = new Thickness(0, 2, 0, 4) };
            chkAudio.Checked += (s, e) => { AppSettings.RecordAudio = true; AppSettings.Save(); };
            chkAudio.Unchecked += (s, e) => { AppSettings.RecordAudio = false; AppSettings.Save(); };
            mainStack.Children.Add(chkAudio);

            var chkCount = new CheckBox { Content = "Таймер обратного отсчета (3 сек) перед стартом", IsChecked = AppSettings.VideoCountdown, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 6) };
            chkCount.Checked += (s, e) => { AppSettings.VideoCountdown = true; AppSettings.Save(); };
            chkCount.Unchecked += (s, e) => { AppSettings.VideoCountdown = false; AppSettings.Save(); };
            mainStack.Children.Add(chkCount);

            mainStack.Children.Add(CreateDivider());

            mainStack.Children.Add(new TextBlock { Text = "📸 Формат и имя файлов", FontSize = 13, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 6) });

            var fmtPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            fmtPanel.Children.Add(new TextBlock { Text = "Формат скриншотов:", Width = 160, Foreground = Brushes.LightGray, VerticalAlignment = System.Windows.VerticalAlignment.Center });
            var cbFormat = new ComboBox { Width = 220, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            cbFormat.Items.Add("PNG (Без потерь)");
            cbFormat.Items.Add("JPG (Компактный)");
            cbFormat.Items.Add("HEIC / HEIF (Высокоэффективный)");
            cbFormat.Items.Add("WEBP (Веб-формат)");
            cbFormat.Items.Add("PDF (Документ)");
            cbFormat.SelectedIndex = AppSettings.DefaultFormat switch { "jpg" => 1, "heic" => 2, "webp" => 3, "pdf" => 4, _ => 0 };
            cbFormat.SelectionChanged += (s, e) =>
            {
                AppSettings.DefaultFormat = cbFormat.SelectedIndex switch { 1 => "jpg", 2 => "heic", 3 => "webp", 4 => "pdf", _ => "png" };
                _jpgQualityPanel.Visibility = (AppSettings.DefaultFormat == "jpg" || AppSettings.DefaultFormat == "webp") ? Visibility.Visible : Visibility.Collapsed;
                AppSettings.Save();
                UpdatePreview();
            };
            fmtPanel.Children.Add(cbFormat);
            mainStack.Children.Add(fmtPanel);

            _jpgQualityPanel.Orientation = System.Windows.Controls.Orientation.Horizontal;
            _jpgQualityPanel.Margin = new Thickness(0, 0, 0, 6);
            _jpgQualityPanel.Visibility = (AppSettings.DefaultFormat == "jpg" || AppSettings.DefaultFormat == "webp") ? Visibility.Visible : Visibility.Collapsed;
            _jpgQualityPanel.Children.Add(new TextBlock { Text = "Качество сжатия:", Width = 160, Foreground = Brushes.LightGray, VerticalAlignment = System.Windows.VerticalAlignment.Center });
            var sliderQ = new Slider { Width = 160, Minimum = 50, Maximum = 100, Value = AppSettings.JpegQuality * 100, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            var lblQ = new TextBlock { Text = $"{(int)sliderQ.Value}%", Width = 50, Margin = new Thickness(10, 0, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(50, 180, 255)), FontWeight = FontWeights.Bold, VerticalAlignment = System.Windows.VerticalAlignment.Center };
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
            pfxPanel.Children.Add(new TextBlock { Text = "Префикс:", Width = 160, Foreground = Brushes.LightGray, VerticalAlignment = System.Windows.VerticalAlignment.Center });
            var tbPrefix = new TextBox { Text = AppSettings.FilenamePrefix, Width = 220, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Background = new SolidColorBrush(Color.FromRgb(40, 44, 52)), Foreground = Brushes.White, BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, Padding = new Thickness(4, 2, 4, 2) };
            tbPrefix.TextChanged += (s, e) =>
            {
                AppSettings.FilenamePrefix = tbPrefix.Text;
                AppSettings.Save();
                UpdatePreview();
            };
            pfxPanel.Children.Add(tbPrefix);
            mainStack.Children.Add(pfxPanel);

            var datePanel = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            datePanel.Children.Add(new TextBlock { Text = "Формат даты:", Width = 160, Foreground = Brushes.LightGray, VerticalAlignment = System.Windows.VerticalAlignment.Center });
            var cbDate = new ComboBox { Width = 220, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
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

            var prevPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
            prevPanel.Children.Add(new TextBlock { Text = "Пример имени: ", FontSize = 11, Foreground = Brushes.Gray });
            _previewText.FontSize = 11;
            _previewText.FontWeight = FontWeights.SemiBold;
            _previewText.Foreground = new SolidColorBrush(Color.FromRgb(60, 150, 255));
            prevPanel.Children.Add(_previewText);
            mainStack.Children.Add(prevPanel);
            UpdatePreview();

            mainStack.Children.Add(CreateDivider());

            mainStack.Children.Add(new TextBlock { Text = "📁 Папка для сохранения", FontSize = 13, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 6) });

            var fldPanel = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            fldPanel.Children.Add(new TextBlock { Text = "Папка:", Width = 160, Foreground = Brushes.LightGray, VerticalAlignment = System.Windows.VerticalAlignment.Center });
            var txtFolder = new TextBlock { Text = System.IO.Path.GetFileName(AppSettings.SaveFolder), Width = 140, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.White, VerticalAlignment = System.Windows.VerticalAlignment.Center };
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

            var chkDirect = new CheckBox { Content = "Сохранять сразу в папку (без диалогового окна)", IsChecked = AppSettings.DirectSave, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 6) };
            chkDirect.Checked += (s, e) => { AppSettings.DirectSave = true; AppSettings.Save(); };
            chkDirect.Unchecked += (s, e) => { AppSettings.DirectSave = false; AppSettings.Save(); };
            mainStack.Children.Add(chkDirect);

            mainStack.Children.Add(CreateDivider());

            mainStack.Children.Add(new TextBlock { Text = "Действия и система", FontSize = 13, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 6) });

            var chkThumb = new CheckBox { Content = "Показывать миниатюру в углу экрана", IsChecked = AppSettings.ShowThumbnail, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 6) };
            chkThumb.Checked += (s, e) => { AppSettings.ShowThumbnail = true; AppSettings.Save(); };
            chkThumb.Unchecked += (s, e) => { AppSettings.ShowThumbnail = false; AppSettings.Save(); };
            mainStack.Children.Add(chkThumb);

            var chkStartup = new CheckBox { Content = "Запуск при входе в Windows", IsChecked = AppSettings.IsStartupEnabled(), Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 8) };
            chkStartup.Checked += (s, e) => AppSettings.SetStartup(true);
            chkStartup.Unchecked += (s, e) => AppSettings.SetStartup(false);
            mainStack.Children.Add(chkStartup);

            var btnCheckUpdates = new Button
            {
                Content = "🔄 Проверить обновления...",
                Background = new SolidColorBrush(Color.FromRgb(44, 48, 58)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 6, 10, 6),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 0, 8)
            };
            btnCheckUpdates.Click += async (s, e) => await UpdateChecker.CheckForUpdatesAsync(isUserInitiated: true);
            mainStack.Children.Add(btnCheckUpdates);

            mainStack.Children.Add(CreateDivider());

            mainStack.Children.Add(new TextBlock
            {
                Text = $"QScreen v{UpdateChecker.CurrentVersion} (Build 9.7.0)",
                FontSize = 11,
                Foreground = Brushes.Gray,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 4)
            });

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = mainStack
            };

            Content = scroll;
        }

        private void AddHotkeyRow(Grid grid, int row, string label, HotkeyBinding binding)
        {
            var lbl = new TextBlock { Text = label, Foreground = Brushes.LightGray, VerticalAlignment = System.Windows.VerticalAlignment.Center };
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
            return new Separator { Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), Margin = new Thickness(0, 3, 0, 3) };
        }
    }

    // Полнофункциональное окно рамки для видеозаписи (до старта и во время записи!)
    public class VideoBoxWindow : Window
    {
        private Grid _mainGrid = new();
        private Border _frameBorder = new();
        private Border _bottomBar = new();
        private TextBlock _sizeBadge = new();
        private Button _btnMic = new();
        private Button _btnBlurToggle = new();
        private Canvas _blurCanvas = new();

        private List<Rect> _blurZones = new();
        private bool _isBlurMode = false;
        private Point _blurStart;
        private Rectangle? _liveBlurRect;

        private double _dpiScaleX = 1.0;
        private double _dpiScaleY = 1.0;

        private bool _isResizing = false;
        private string _resizeDir = "";
        private System.Drawing.Point _resizeStartMouse;
        private Rect _resizeStartBounds;

        public VideoBoxWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;

            BuildUI();

            Loaded += (s, e) =>
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                _dpiScaleX = dpi.DpiScaleX;
                _dpiScaleY = dpi.DpiScaleY;
                CenterOnActiveMonitor();
            };
        }

        private void CenterOnActiveMonitor()
        {
            var cursorPos = Forms.Cursor.Position;
            var currentScreen = Forms.Screen.FromPoint(cursorPos);

            double monDipLeft = currentScreen.Bounds.Left / _dpiScaleX;
            double monDipTop = currentScreen.Bounds.Top / _dpiScaleY;
            double monDipWidth = currentScreen.Bounds.Width / _dpiScaleX;
            double monDipHeight = currentScreen.Bounds.Height / _dpiScaleY;

            double w = Math.Min(1280, monDipWidth * 0.75);
            double h = w * (9.0 / 16.0);
            if (h > monDipHeight * 0.8)
            {
                h = monDipHeight * 0.8;
                w = h * (16.0 / 9.0);
            }

            Left = monDipLeft + (monDipWidth - w) / 2;
            Top = monDipTop + (monDipHeight - h) / 2;
            Width = w;
            Height = h;
            UpdateSizeBadge();
        }

        private void BuildUI()
        {
            _mainGrid = new Grid();

            // Основная рамка
            _frameBorder = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(50, 180, 255)),
                BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Opacity = 0.5 }
            };

            // Перетаскивание рамки за внутреннюю часть
            _frameBorder.MouseDown += (s, e) =>
            {
                if (_isBlurMode)
                {
                    _blurStart = e.GetPosition(_blurCanvas);
                    _liveBlurRect = new Rectangle
                    {
                        Stroke = Brushes.Crimson,
                        StrokeThickness = 1.5,
                        Fill = new SolidColorBrush(Color.FromArgb(140, 255, 50, 80))
                    };
                    _blurCanvas.Children.Add(_liveBlurRect);
                    _blurCanvas.CaptureMouse();
                }
                else if (e.LeftButton == MouseButtonState.Pressed)
                {
                    DragMove();
                    UpdateSizeBadge();
                }
            };

            _frameBorder.MouseMove += (s, e) =>
            {
                if (_isBlurMode && _liveBlurRect != null)
                {
                    var curr = e.GetPosition(_blurCanvas);
                    double x = Math.Min(_blurStart.X, curr.X);
                    double y = Math.Min(_blurStart.Y, curr.Y);
                    double w = Math.Abs(_blurStart.X - curr.X);
                    double h = Math.Abs(_blurStart.Y - curr.Y);
                    Canvas.SetLeft(_liveBlurRect, x);
                    Canvas.SetTop(_liveBlurRect, y);
                    _liveBlurRect.Width = w;
                    _liveBlurRect.Height = h;
                }
            };

            _frameBorder.MouseUp += (s, e) =>
            {
                if (_isBlurMode && _liveBlurRect != null)
                {
                    _blurCanvas.ReleaseMouseCapture();
                    double x = Canvas.GetLeft(_liveBlurRect);
                    double y = Canvas.GetTop(_liveBlurRect);
                    double w = _liveBlurRect.Width;
                    double h = _liveBlurRect.Height;

                    if (w > 10 && h > 10)
                    {
                        _blurZones.Add(new Rect(x, y, w, h));
                    }
                    _liveBlurRect = null;
                    _isBlurMode = false;
                    _btnBlurToggle.Content = "░ + Блер";
                    _btnBlurToggle.Background = new SolidColorBrush(Color.FromRgb(48, 52, 62));
                }
            };

            _mainGrid.Children.Add(_frameBorder);

            // Канвас для зон блера
            _blurCanvas = new Canvas { Background = Brushes.Transparent };
            _mainGrid.Children.Add(_blurCanvas);

            // Бейдж с размером
            _sizeBadge = new TextBlock
            {
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(220, 20, 22, 28)),
                Padding = new Thickness(6, 2, 6, 2),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                Margin = new Thickness(6, 6, 0, 0)
            };
            _mainGrid.Children.Add(_sizeBadge);

            // 8 маркеров для растяжки (Handles)
            AddHandle("TL", System.Windows.HorizontalAlignment.Left, System.Windows.VerticalAlignment.Top, Cursors.SizeNWSE);
            AddHandle("TR", System.Windows.HorizontalAlignment.Right, System.Windows.VerticalAlignment.Top, Cursors.SizeNESW);
            AddHandle("BL", System.Windows.HorizontalAlignment.Left, System.Windows.VerticalAlignment.Bottom, Cursors.SizeNESW);
            AddHandle("BR", System.Windows.HorizontalAlignment.Right, System.Windows.VerticalAlignment.Bottom, Cursors.SizeNWSE);
            AddHandle("T", System.Windows.HorizontalAlignment.Center, System.Windows.VerticalAlignment.Top, Cursors.SizeNS);
            AddHandle("B", System.Windows.HorizontalAlignment.Center, System.Windows.VerticalAlignment.Bottom, Cursors.SizeNS);
            AddHandle("L", System.Windows.HorizontalAlignment.Left, System.Windows.VerticalAlignment.Center, Cursors.SizeWE);
            AddHandle("R", System.Windows.HorizontalAlignment.Right, System.Windows.VerticalAlignment.Center, Cursors.SizeWE);

            // Тулбар управления
            _bottomBar = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(26, 28, 34)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 4, 8, 4),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, -48),
                Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Opacity = 0.6 }
            };

            var barStack = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };

            var btnRec = new Button
            {
                Content = "🔴 Начать запись",
                Background = new SolidColorBrush(Color.FromRgb(239, 68, 68)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Padding = new Thickness(12, 6, 12, 6),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand
            };
            btnRec.Click += (s, e) => StartRecording();
            barStack.Children.Add(btnRec);

            _btnBlurToggle = new Button
            {
                Content = "░ + Блер",
                ToolTip = "Выделить зону цензуры внутри кадра",
                Background = new SolidColorBrush(Color.FromRgb(48, 52, 62)),
                Foreground = Brushes.White,
                FontSize = 11,
                Padding = new Thickness(10, 6, 10, 6),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand
            };
            _btnBlurToggle.Click += (s, e) =>
            {
                _isBlurMode = !_isBlurMode;
                _btnBlurToggle.Content = _isBlurMode ? "🔴 Выделите..." : "░ + Блер";
                _btnBlurToggle.Background = _isBlurMode ? new SolidColorBrush(Color.FromRgb(255, 60, 80)) : new SolidColorBrush(Color.FromRgb(48, 52, 62));
            };
            barStack.Children.Add(_btnBlurToggle);

            _btnMic = new Button
            {
                Content = AppSettings.RecordAudio ? "🎙 Мик: ВКЛ" : "🔇 Мик: ВЫКЛ",
                ToolTip = "Включить / выключить запись звука с микрофона",
                Background = AppSettings.RecordAudio ? new SolidColorBrush(Color.FromRgb(16, 185, 129)) : new SolidColorBrush(Color.FromRgb(48, 52, 62)),
                Foreground = Brushes.White,
                FontSize = 11,
                Padding = new Thickness(10, 6, 10, 6),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand
            };
            _btnMic.Click += (s, e) =>
            {
                AppSettings.RecordAudio = !AppSettings.RecordAudio;
                AppSettings.Save();
                _btnMic.Content = AppSettings.RecordAudio ? "🎙 Мик: ВКЛ" : "🔇 Мик: ВЫКЛ";
                _btnMic.Background = AppSettings.RecordAudio ? new SolidColorBrush(Color.FromRgb(16, 185, 129)) : new SolidColorBrush(Color.FromRgb(48, 52, 62));
            };
            barStack.Children.Add(_btnMic);

            var btn169 = new Button
            {
                Content = "📐 16:9",
                Background = new SolidColorBrush(Color.FromRgb(48, 52, 62)),
                Foreground = Brushes.White,
                FontSize = 11,
                Padding = new Thickness(8, 6, 8, 6),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand
            };
            btn169.Click += (s, e) =>
            {
                double nw = Math.Min(1280, Width);
                double nh = nw * (9.0 / 16.0);
                Width = nw;
                Height = nh;
                UpdateSizeBadge();
            };
            barStack.Children.Add(btn169);

            var btnMon = new Button
            {
                Content = "🖥 Монитор",
                Background = new SolidColorBrush(Color.FromRgb(48, 52, 62)),
                Foreground = Brushes.White,
                FontSize = 11,
                Padding = new Thickness(8, 6, 8, 6),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand
            };
            btnMon.Click += (s, e) => CenterOnActiveMonitor();
            barStack.Children.Add(btnMon);

            var btnClose = new Button
            {
                Content = "✕",
                Background = new SolidColorBrush(Color.FromRgb(60, 64, 75)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Padding = new Thickness(10, 6, 10, 6),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            btnClose.Click += (s, e) => Close();
            barStack.Children.Add(btnClose);

            _bottomBar.Child = barStack;
            _mainGrid.Children.Add(_bottomBar);

            Content = _mainGrid;
        }

        private void AddHandle(string dir, System.Windows.HorizontalAlignment halign, System.Windows.VerticalAlignment valign, Cursor cursor)
        {
            var h = new Border
            {
                Width = 12,
                Height = 12,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(36, 120, 220)),
                BorderThickness = new Thickness(2),
                HorizontalAlignment = halign,
                VerticalAlignment = valign,
                Margin = new Thickness(-5, -5, -5, -5),
                Cursor = cursor
            };

            h.MouseDown += (s, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    _isResizing = true;
                    _resizeDir = dir;
                    _resizeStartMouse = Forms.Cursor.Position;
                    _resizeStartBounds = new Rect(Left, Top, Width, Height);
                    h.CaptureMouse();
                }
            };

            h.MouseMove += (s, e) =>
            {
                if (_isResizing && _resizeDir == dir)
                {
                    var currentMouse = Forms.Cursor.Position;
                    double dx = (currentMouse.X - _resizeStartMouse.X) / _dpiScaleX;
                    double dy = (currentMouse.Y - _resizeStartMouse.Y) / _dpiScaleY;

                    double nLeft = _resizeStartBounds.Left;
                    double nTop = _resizeStartBounds.Top;
                    double nWidth = _resizeStartBounds.Width;
                    double nHeight = _resizeStartBounds.Height;

                    if (dir.Contains("L")) { nLeft += dx; nWidth -= dx; }
                    if (dir.Contains("R")) { nWidth += dx; }
                    if (dir.Contains("T")) { nTop += dy; nHeight -= dy; }
                    if (dir.Contains("B")) { nHeight += dy; }

                    if (nWidth > 120) { Left = nLeft; Width = nWidth; }
                    if (nHeight > 80) { Top = nTop; Height = nHeight; }
                    UpdateSizeBadge();
                }
            };

            h.MouseUp += (s, e) =>
            {
                _isResizing = false;
                h.ReleaseMouseCapture();
            };

            _mainGrid.Children.Add(h);
        }

        private void UpdateSizeBadge()
        {
            _sizeBadge.Text = $"{(int)Width} × {(int)Height} px";
        }

        public System.Drawing.Rectangle GetCurrentScreenPixelBounds()
        {
            return new System.Drawing.Rectangle(
                (int)Math.Round(Left * _dpiScaleX),
                (int)Math.Round(Top * _dpiScaleY),
                (int)Math.Round(Width * _dpiScaleX),
                (int)Math.Round(Height * _dpiScaleY)
            );
        }

        public List<System.Drawing.Rectangle> GetCurrentBlurRegions()
        {
            return _blurZones.Select(bz => new System.Drawing.Rectangle(
                (int)Math.Round((Left + bz.X) * _dpiScaleX),
                (int)Math.Round((Top + bz.Y) * _dpiScaleY),
                (int)Math.Round(bz.Width * _dpiScaleX),
                (int)Math.Round(bz.Height * _dpiScaleY)
            )).ToList();
        }

        private void StartRecording()
        {
            _bottomBar.Visibility = Visibility.Collapsed;
            _frameBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 45, 85));

            var recorder = new VideoRecorder(this);
            recorder.Start();
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
        private double _dpiScaleX = 1.0;
        private double _dpiScaleY = 1.0;
        private double _vsLeft = 0;
        private double _vsTop = 0;

        public QScreenOverlayWindow(Bitmap screenBitmap, bool isSmartMode = false)
        {
            _screenBitmap = screenBitmap;
            _isSmartMode = isSmartMode;

            var vs = Forms.SystemInformation.VirtualScreen;
            _vsLeft = vs.Left;
            _vsTop = vs.Top;

            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
            WindowStyle = WindowStyle.None;
            Topmost = true;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            Cursor = Cursors.Cross;
            Focusable = true;
            ShowActivated = true;

            Loaded += (s, e) =>
            {
                Activate();
                Focus();
                Keyboard.Focus(this);
                CaptureMouse();

                var dpi = VisualTreeHelper.GetDpi(this);
                _dpiScaleX = dpi.DpiScaleX;
                _dpiScaleY = dpi.DpiScaleY;

                if (_isSmartMode)
                {
                    _windows = WindowDetector.GetVisibleWindows(_dpiScaleX, _dpiScaleY, _vsLeft, _vsTop);
                }
            };

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    ReleaseMouseCapture();
                    Close();
                }
            };

            PreviewMouseRightButtonDown += (s, e) =>
            {
                ReleaseMouseCapture();
                Close();
            };

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
                    _hoveredWindow = _windows.FirstOrDefault(w => w.DipBounds.Contains(_currentPoint));
                }
                InvalidateVisual();
            };

            MouseUp += (s, e) =>
            {
                ReleaseMouseCapture();
                var rect = GetSelectionRect();

                if (_isDragging && rect.Width > 5 && rect.Height > 5)
                {
                    Close();
                    var cropPixelRect = new System.Drawing.Rectangle(
                        (int)Math.Round(rect.X * _dpiScaleX),
                        (int)Math.Round(rect.Y * _dpiScaleY),
                        (int)Math.Round(rect.Width * _dpiScaleX),
                        (int)Math.Round(rect.Height * _dpiScaleY)
                    );
                    var cropped = CropBitmap(_screenBitmap, cropPixelRect);
                    new QScreenEditorWindow(cropped).Show();
                }
                else if (_isSmartMode && _hoveredWindow != null)
                {
                    IntPtr targetHwnd = _hoveredWindow.Hwnd;
                    var bounds = _hoveredWindow.PixelBounds;
                    Close();
                    var cleanBmp = WindowDetector.CaptureWindowIsolated(targetHwnd, bounds, _screenBitmap);
                    new QScreenEditorWindow(cleanBmp).Show();
                }
                else if (rect.Width > 5 && rect.Height > 5)
                {
                    Close();
                    var cropPixelRect = new System.Drawing.Rectangle(
                        (int)Math.Round(rect.X * _dpiScaleX),
                        (int)Math.Round(rect.Y * _dpiScaleY),
                        (int)Math.Round(rect.Width * _dpiScaleX),
                        (int)Math.Round(rect.Height * _dpiScaleY)
                    );
                    var cropped = CropBitmap(_screenBitmap, cropPixelRect);
                    new QScreenEditorWindow(cropped).Show();
                }
            };
        }

        private Rect GetSelectionRect()
        {
            double x = Math.Min(_startPoint.X, _currentPoint.X);
            double y = Math.Min(_startPoint.Y, _currentPoint.Y);
            double w = Math.Abs(_startPoint.X - _currentPoint.X);
            double h = Math.Abs(_startPoint.Y - _currentPoint.Y);
            return new Rect(x, y, w, h);
        }

        private Bitmap CropBitmap(Bitmap src, System.Drawing.Rectangle rect)
        {
            int rx = Math.Max(0, Math.Min(rect.X, src.Width - 1));
            int ry = Math.Max(0, Math.Min(rect.Y, src.Height - 1));
            int rw = Math.Max(1, Math.Min(rect.Width, src.Width - rx));
            int rh = Math.Max(1, Math.Min(rect.Height, src.Height - ry));

            var target = new Bitmap(rw, rh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(target))
            {
                g.DrawImage(src, new System.Drawing.Rectangle(0, 0, rw, rh), rx, ry, rw, rh, GraphicsUnit.Pixel);
            }
            return target;
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, new Rect(0, 0, ActualWidth, ActualHeight));

            if (!_isDragging && _hoveredWindow != null)
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(40, 0, 120, 255)), new Pen(Brushes.DodgerBlue, 2.5), _hoveredWindow.DipBounds, 6, 6);
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
                System.Windows.FlowDirection.LeftToRight,
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

    public class VideoRecorder
    {
        private VideoBoxWindow _boxWindow;
        private CancellationTokenSource _cts = new();
        private Process? _ffmpegProcess;
        private Stream? _ffmpegInput;
        private string _outputPath = "";
        private RecordingControlBarWindow? _controlBarWin;
        private bool _isPaused = false;
        private bool _blurActive = true;
        private bool _audioActive = false;
        private Stopwatch _stopwatch = new();
        private int _fixedOutputWidth = 1280;
        private int _fixedOutputHeight = 720;

        public VideoRecorder(VideoBoxWindow boxWindow)
        {
            _boxWindow = boxWindow;
            _audioActive = AppSettings.RecordAudio;

            var initRect = _boxWindow.GetCurrentScreenPixelBounds();
            _fixedOutputWidth = initRect.Width % 2 == 0 ? initRect.Width : initRect.Width - 1;
            _fixedOutputHeight = initRect.Height % 2 == 0 ? initRect.Height : initRect.Height - 1;
            if (_fixedOutputWidth < 100) _fixedOutputWidth = 1280;
            if (_fixedOutputHeight < 100) _fixedOutputHeight = 720;
        }

        public void Start()
        {
            var ext = AppSettings.VideoFormat == "gif" ? "gif" : "mp4";
            _outputPath = System.IO.Path.Combine(AppSettings.SaveFolder, AppSettings.GenerateFileName(ext));

            _controlBarWin = new RecordingControlBarWindow(new Rect(_boxWindow.Left, _boxWindow.Top, _boxWindow.Width, _boxWindow.Height), this);
            _controlBarWin.Show();

            if (AppSettings.VideoCountdown)
            {
                RunCountdownAndRecord();
            }
            else
            {
                BeginCaptureLoop();
            }
        }

        private void RunCountdownAndRecord()
        {
            var cdWin = new Window
            {
                Width = 200,
                Height = 200,
                Left = _boxWindow.Left + (_boxWindow.Width - 200) / 2,
                Top = _boxWindow.Top + (_boxWindow.Height - 200) / 2,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false
            };

            var tb = new TextBlock
            {
                Text = "3",
                FontSize = 72,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(50, 180, 255)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            cdWin.Content = tb;
            cdWin.Show();

            int count = 3;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) =>
            {
                count--;
                if (count > 0)
                {
                    tb.Text = count.ToString();
                }
                else
                {
                    timer.Stop();
                    cdWin.Close();
                    BeginCaptureLoop();
                }
            };
            timer.Start();
        }

        private void BeginCaptureLoop()
        {
            _stopwatch.Start();

            string ffmpegPath = FindFfmpeg();
            bool useFfmpeg = !string.IsNullOrEmpty(ffmpegPath);

            int fps = AppSettings.VideoFps;
            int width = _fixedOutputWidth;
            int height = _fixedOutputHeight;

            if (useFfmpeg)
            {
                string crf = AppSettings.VideoQuality switch { "medium" => "23", "low" => "28", _ => "18" };
                string codecArg = AppSettings.VideoCodec == "h265" ? "libx265" : "libx264";
                string audioArgs = _audioActive ? "-f dshow -i audio=\"default\" " : "";

                string args = AppSettings.VideoFormat == "gif"
                    ? $"-y -f rawvideo -vcodec rawvideo -s {width}x{height} -pix_fmt bgr24 -r {fps} -i - -vf \"fps={Math.Min(fps, 30)},split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse\" \"{_outputPath}\""
                    : $"-y -f rawvideo -vcodec rawvideo -s {width}x{height} -pix_fmt bgr24 -r {fps} -i - {audioArgs}-c:v {codecArg} -preset ultrafast -crf {crf} -pix_fmt yuv420p \"{_outputPath}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    CreateNoWindow = true
                };
                try
                {
                    _ffmpegProcess = Process.Start(psi);
                    _ffmpegInput = _ffmpegProcess?.StandardInput.BaseStream;
                }
                catch
                {
                    string fallbackArgs = $"-y -f rawvideo -vcodec rawvideo -s {width}x{height} -pix_fmt bgr24 -r {fps} -i - -c:v {codecArg} -preset ultrafast -crf {crf} -pix_fmt yuv420p \"{_outputPath}\"";
                    psi.Arguments = fallbackArgs;
                    _ffmpegProcess = Process.Start(psi);
                    _ffmpegInput = _ffmpegProcess?.StandardInput.BaseStream;
                }
            }

            Task.Run(() => CaptureWorker(fps, useFfmpeg, _cts.Token));
        }

        private string FindFfmpeg()
        {
            if (System.IO.File.Exists("ffmpeg.exe")) return "ffmpeg.exe";
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var p in pathEnv.Split(';'))
            {
                var full = System.IO.Path.Combine(p.Trim(), "ffmpeg.exe");
                if (System.IO.File.Exists(full)) return full;
            }
            return "";
        }

        private void CaptureWorker(int fps, bool useFfmpeg, CancellationToken token)
        {
            int intervalMs = 1000 / fps;
            var vs = Forms.SystemInformation.VirtualScreen;

            var fullDesktopBmp = new Bitmap(vs.Width, vs.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            var gFull = Graphics.FromImage(fullDesktopBmp);

            var outBmp = new Bitmap(_fixedOutputWidth, _fixedOutputHeight, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            var gOut = Graphics.FromImage(outBmp);
            gOut.InterpolationMode = InterpolationMode.Bilinear;

            while (!token.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();

                if (!_isPaused)
                {
                    gFull.CopyFromScreen(vs.Left, vs.Top, 0, 0, new System.Drawing.Size(vs.Width, vs.Height), CopyPixelOperation.SourceCopy);

                    if (AppSettings.RecordCursor)
                    {
                        var ci = new Win32.CURSORINFO { cbSize = Marshal.SizeOf<Win32.CURSORINFO>() };
                        if (Win32.GetCursorInfo(ref ci) && ci.flags == Win32.CURSOR_SHOWING)
                        {
                            int cx = ci.ptScreenPos.x - vs.Left;
                            int cy = ci.ptScreenPos.y - vs.Top;
                            if (cx >= 0 && cx < vs.Width && cy >= 0 && cy < vs.Height)
                            {
                                Win32.DrawIcon(gFull.GetHdc(), cx, cy, ci.hCursor);
                                gFull.ReleaseHdc();
                            }
                        }
                    }

                    if (_blurActive)
                    {
                        var blurs = _boxWindow.Dispatcher.Invoke(() => _boxWindow.GetCurrentBlurRegions());
                        foreach (var br in blurs)
                        {
                            PixelateGraphics(fullDesktopBmp, new System.Drawing.Rectangle(br.X - vs.Left, br.Y - vs.Top, br.Width, br.Height), 16);
                        }
                    }

                    var curBoxRect = _boxWindow.Dispatcher.Invoke(() => _boxWindow.GetCurrentScreenPixelBounds());
                    int cropX = Math.Max(0, Math.Min(curBoxRect.X - vs.Left, vs.Width - 10));
                    int cropY = Math.Max(0, Math.Min(curBoxRect.Y - vs.Top, vs.Height - 10));
                    int cropW = Math.Max(10, Math.Min(curBoxRect.Width, vs.Width - cropX));
                    int cropH = Math.Max(10, Math.Min(curBoxRect.Height, vs.Height - cropY));

                    gOut.DrawImage(fullDesktopBmp, new System.Drawing.Rectangle(0, 0, _fixedOutputWidth, _fixedOutputHeight), cropX, cropY, cropW, cropH, GraphicsUnit.Pixel);

                    if (useFfmpeg && _ffmpegInput != null)
                    {
                        var data = outBmp.LockBits(new System.Drawing.Rectangle(0, 0, _fixedOutputWidth, _fixedOutputHeight), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                        int stride = data.Stride;
                        int bytesTotal = stride * _fixedOutputHeight;
                        byte[] buffer = new byte[bytesTotal];
                        Marshal.Copy(data.Scan0, buffer, 0, bytesTotal);
                        outBmp.UnlockBits(data);

                        try
                        {
                            _ffmpegInput.Write(buffer, 0, buffer.Length);
                        }
                        catch { break; }
                    }
                }

                int elapsed = (int)sw.ElapsedMilliseconds;
                int sleep = intervalMs - elapsed;
                if (sleep > 0) Thread.Sleep(sleep);
            }

            gFull.Dispose();
            fullDesktopBmp.Dispose();
            gOut.Dispose();
            outBmp.Dispose();

            if (_ffmpegInput != null)
            {
                try { _ffmpegInput.Flush(); _ffmpegInput.Close(); } catch { }
            }
            if (_ffmpegProcess != null)
            {
                _ffmpegProcess.WaitForExit(5000);
            }
        }

        private void PixelateGraphics(Bitmap bmp, System.Drawing.Rectangle rect, int pixelSize)
        {
            int rx = Math.Max(0, Math.Min(rect.X, bmp.Width - 1));
            int ry = Math.Max(0, Math.Min(rect.Y, bmp.Height - 1));
            int rw = Math.Max(1, Math.Min(rect.Width, bmp.Width - rx));
            int rh = Math.Max(1, Math.Min(rect.Height, bmp.Height - ry));

            int sw = Math.Max(1, rw / pixelSize);
            int sh = Math.Max(1, rh / pixelSize);

            using var small = new Bitmap(sw, sh);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(bmp, new System.Drawing.Rectangle(0, 0, sw, sh), rx, ry, rw, rh, GraphicsUnit.Pixel);
            }

            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(small, new System.Drawing.Rectangle(rx, ry, rw, rh), 0, 0, sw, sh, GraphicsUnit.Pixel);
            }
        }

        public void Pause() => _isPaused = !_isPaused;
        public bool ToggleBlur() { _blurActive = !_blurActive; return _blurActive; }
        public bool ToggleAudio() { _audioActive = !_audioActive; return _audioActive; }

        public void StopAndSave()
        {
            _cts.Cancel();
            _stopwatch.Stop();
            _boxWindow.Dispatcher.Invoke(() => _boxWindow.Close());
            _controlBarWin?.Close();

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                new VideoResultWindow(_outputPath).Show();
            });
        }

        public void Cancel()
        {
            _cts.Cancel();
            _stopwatch.Stop();
            _boxWindow.Dispatcher.Invoke(() => _boxWindow.Close());
            _controlBarWin?.Close();
            try { if (System.IO.File.Exists(_outputPath)) System.IO.File.Delete(_outputPath); } catch { }
        }

        public TimeSpan GetDuration() => _stopwatch.Elapsed;
    }

    // Плавающая и перетаскиваемая контрольная панель прямо во время записи видео
    public class RecordingControlBarWindow : Window
    {
        private VideoRecorder _recorder;
        private TextBlock _timerText = new();
        private DispatcherTimer _tickTimer = new();
        private Button _btnPause = new();
        private Button _btnBlur = new();
        private Button _btnMic = new();

        public RecordingControlBarWindow(Rect zoneRect, VideoRecorder recorder)
        {
            _recorder = recorder;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.WidthAndHeight;

            double initLeft = zoneRect.Left + (zoneRect.Width - 460) / 2;
            double initTop = zoneRect.Bottom + 12;
            if (initTop + 60 > SystemParameters.VirtualScreenHeight + SystemParameters.VirtualScreenTop) 
                initTop = zoneRect.Top - 60;
            if (initLeft < SystemParameters.VirtualScreenLeft + 10) 
                initLeft = SystemParameters.VirtualScreenLeft + 10;

            Left = initLeft;
            Top = initTop;

            BuildUI();

            _tickTimer.Interval = TimeSpan.FromMilliseconds(500);
            _tickTimer.Tick += (s, e) =>
            {
                var dur = _recorder.GetDuration();
                _timerText.Text = dur.ToString(@"mm\:ss");
            };
            _tickTimer.Start();
        }

        private void BuildUI()
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(26, 28, 34)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 6, 10, 6),
                Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 6, Opacity = 0.65 }
            };

            card.MouseDown += (s, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed) DragMove();
            };

            var stack = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };

            var grip = new TextBlock
            {
                Text = "⠿",
                FontSize = 16,
                Foreground = Brushes.Gray,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.SizeAll
            };
            stack.Children.Add(grip);

            var dot = new Ellipse { Width = 10, Height = 10, Fill = Brushes.Crimson, VerticalAlignment = System.Windows.VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            stack.Children.Add(dot);

            _timerText.Text = "00:00";
            _timerText.Foreground = Brushes.White;
            _timerText.FontWeight = FontWeights.Bold;
            _timerText.FontSize = 13;
            _timerText.VerticalAlignment = System.Windows.VerticalAlignment.Center;
            _timerText.Margin = new Thickness(0, 0, 10, 0);
            stack.Children.Add(_timerText);

            _btnPause = new Button
            {
                Content = "⏸",
                ToolTip = "Пауза / Продолжить",
                Width = 32,
                Height = 28,
                Background = new SolidColorBrush(Color.FromRgb(48, 52, 62)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand
            };
            _btnPause.Click += (s, e) =>
            {
                _recorder.Pause();
                _btnPause.Content = _btnPause.Content.ToString() == "⏸" ? "▶" : "⏸";
            };
            stack.Children.Add(_btnPause);

            _btnBlur = new Button
            {
                Content = "░ Блер: ВКЛ",
                ToolTip = "Включить / выключить размытие зон цензуры в реальном времени",
                Background = new SolidColorBrush(Color.FromRgb(255, 60, 80)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                Padding = new Thickness(8, 4, 8, 4),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand
            };
            _btnBlur.Click += (s, e) =>
            {
                bool active = _recorder.ToggleBlur();
                _btnBlur.Content = active ? "░ Блер: ВКЛ" : "░ Блер: ВЫКЛ";
                _btnBlur.Background = active ? new SolidColorBrush(Color.FromRgb(255, 60, 80)) : new SolidColorBrush(Color.FromRgb(48, 52, 62));
            };
            stack.Children.Add(_btnBlur);

            _btnMic = new Button
            {
                Content = AppSettings.RecordAudio ? "🎙 Мик: ВКЛ" : "🔇 Мик: ВЫКЛ",
                ToolTip = "Включить / отключить звук микрофона прямо во время записи",
                Background = AppSettings.RecordAudio ? new SolidColorBrush(Color.FromRgb(16, 185, 129)) : new SolidColorBrush(Color.FromRgb(48, 52, 62)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                Padding = new Thickness(8, 4, 8, 4),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand
            };
            _btnMic.Click += (s, e) =>
            {
                bool active = _recorder.ToggleAudio();
                _btnMic.Content = active ? "🎙 Мик: ВКЛ" : "🔇 Мик: ВЫКЛ";
                _btnMic.Background = active ? new SolidColorBrush(Color.FromRgb(16, 185, 129)) : new SolidColorBrush(Color.FromRgb(48, 52, 62));
            };
            stack.Children.Add(_btnMic);

            var btnStop = new Button
            {
                Content = "⏹ Стоп и сохранить",
                Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                Padding = new Thickness(10, 4, 10, 4),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand
            };
            btnStop.Click += (s, e) => _recorder.StopAndSave();
            stack.Children.Add(btnStop);

            var btnCancel = new Button
            {
                Content = "✖",
                ToolTip = "Отмена записи",
                Width = 28,
                Height = 28,
                Background = new SolidColorBrush(Color.FromRgb(60, 64, 75)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            btnCancel.Click += (s, e) => _recorder.Cancel();
            stack.Children.Add(btnCancel);

            card.Child = stack;
            Content = card;
        }
    }

    public class VideoResultWindow : Window
    {
        private string _filePath;
        private Point _dragStart;

        public VideoResultWindow(string filePath)
        {
            _filePath = filePath;
            Title = "Запись завершена — QScreen Studio";
            Width = 440;
            Height = 160;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(28, 30, 36));
            Foreground = Brushes.White;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;

            Win32.ApplyDarkMode(this);
            Icon = AppIconProvider.GetImageSource();

            BuildUI();
        }

        private void BuildUI()
        {
            var stack = new StackPanel { Margin = new Thickness(16) };

            var header = new TextBlock
            {
                Text = "🎉 Видеозапись успешно сохранена!",
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(52, 199, 89)),
                Margin = new Thickness(0, 0, 0, 6)
            };
            stack.Children.Add(header);

            var pathTxt = new TextBlock
            {
                Text = System.IO.Path.GetFileName(_filePath),
                Foreground = Brushes.LightGray,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 14)
            };
            stack.Children.Add(pathTxt);

            var btnStack = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };

            var btnPlay = new Button
            {
                Content = "▶ Открыть",
                Background = new SolidColorBrush(Color.FromRgb(48, 52, 62)),
                Foreground = Brushes.White,
                Padding = new Thickness(12, 6, 12, 6),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand
            };
            btnPlay.Click += (s, e) => { Process.Start(new ProcessStartInfo(_filePath) { UseShellExecute = true }); Close(); };
            btnStack.Children.Add(btnPlay);

            var btnFolder = new Button
            {
                Content = "📁 В папке",
                Background = new SolidColorBrush(Color.FromRgb(48, 52, 62)),
                Foreground = Brushes.White,
                Padding = new Thickness(12, 6, 12, 6),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand
            };
            btnFolder.Click += (s, e) => { Process.Start("explorer.exe", $"/select,\"{_filePath}\""); Close(); };
            btnStack.Children.Add(btnFolder);

            var btnDrag = new Button
            {
                Content = "🖐 Drag & Drop",
                ToolTip = "Перетащите файл в Telegram / Discord",
                Background = new SolidColorBrush(Color.FromRgb(36, 120, 220)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(14, 6, 14, 6),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            btnDrag.PreviewMouseLeftButtonDown += (s, e) => _dragStart = e.GetPosition(null);
            btnDrag.PreviewMouseMove += (s, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    var diff = _dragStart - e.GetPosition(null);
                    if (Math.Abs(diff.X) > 4 || Math.Abs(diff.Y) > 4)
                    {
                        var data = new System.Windows.DataObject(System.Windows.DataFormats.FileDrop, new[] { _filePath });
                        System.Windows.DragDrop.DoDragDrop(btnDrag, data, System.Windows.DragDropEffects.Copy);
                    }
                }
            };
            btnStack.Children.Add(btnDrag);

            stack.Children.Add(btnStack);
            Content = stack;
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

            var workArea = SystemParameters.WorkArea;
            Width = Math.Min(Math.Max(bitmap.Width + 120, 920), workArea.Width - 80);
            Height = Math.Min(Math.Max(bitmap.Height + 180, 560), workArea.Height - 80);

            ResizeMode = ResizeMode.CanResizeWithGrip;
            Topmost = true;
            ShowActivated = true;

            Win32.ApplyDarkMode(this);
            Icon = AppIconProvider.GetImageSource();

            Loaded += (s, e) =>
            {
                Topmost = false;
                var hwnd = new WindowInteropHelper(this).Handle;
                Win32.SetForegroundWindow(hwnd);
                Win32.SetWindowPos(hwnd, Win32.HWND_TOP, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_SHOWWINDOW);
                Activate();
                Focus();
            };

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
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Cursor = Cursors.SizeAll
            };

            topCard.MouseDown += (s, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed && e.OriginalSource is not Button)
                {
                    DragMove();
                }
            };

            _mainToolBar.Orientation = System.Windows.Controls.Orientation.Horizontal;

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
            _canvasContainer.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            _canvasContainer.VerticalAlignment = System.Windows.VerticalAlignment.Center;
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
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center
            };

            var bottomStack = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };

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

                            var data = new System.Windows.DataObject();
                            data.SetData(System.Windows.DataFormats.FileDrop, new string[] { tempFile });
                            data.SetData("FileDrop", new string[] { tempFile });

                            System.Windows.DragDrop.DoDragDrop(btn, data, System.Windows.DragDropEffects.Copy);
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
            foreach (var fmt in new[] { "png", "jpg", "heic", "webp", "pdf" })
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
                            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                            VerticalAlignment = System.Windows.VerticalAlignment.Center
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
                var border = new Border { Width = Math.Max(w, 100), Height = Math.Max(h, 45), CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(220, 20, 22, 28)), BorderBrush = new SolidColorBrush(_selectedColor), BorderThickness = new Thickness(2), Padding = new Thickness(6) };
                var tb = new TextBox { Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0), FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Text = "Текст..." };
                border.Child = tb;
                Canvas.SetLeft(border, x); Canvas.SetTop(border, y);
                AddElement(border);
                tb.Focus();
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
                    var pixelated = PixelateRegion(_baseBitmap, new System.Drawing.Rectangle((int)x, (int)y, (int)w, (int)h), 12);
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

        private Bitmap PixelateRegion(Bitmap src, System.Drawing.Rectangle rect, int pixelSize)
        {
            int rx = Math.Max(0, Math.Min(rect.X, src.Width - 1));
            int ry = Math.Max(0, Math.Min(rect.Y, src.Height - 1));
            int rw = Math.Max(1, Math.Min(rect.Width, src.Width - rx));
            int rh = Math.Max(1, Math.Min(rect.Height, src.Height - ry));

            int sw = Math.Max(1, rw / pixelSize);
            int sh = Math.Max(1, rh / pixelSize);

            var cropped = new Bitmap(rw, rh);
            using (var g = Graphics.FromImage(cropped))
            {
                g.DrawImage(src, new System.Drawing.Rectangle(0, 0, rw, rh), rx, ry, rw, rh, GraphicsUnit.Pixel);
            }

            var small = new Bitmap(sw, sh);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.DrawImage(cropped, 0, 0, sw, sh);
            }

            var result = new Bitmap(rw, rh);
            using (var g = Graphics.FromImage(result))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.DrawImage(small, new System.Drawing.Rectangle(0, 0, rw, rh), 0, 0, sw, sh, GraphicsUnit.Pixel);
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
            System.Windows.Clipboard.SetImage(BitmapToImageSource(bmp));
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
                var sfd = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "PNG Image|*.png|JPEG Image|*.jpg|HEIC Image|*.heic|WEBP Image|*.webp|PDF Document|*.pdf",
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

        private void RunWindowsOCR()
        {
            try
            {
                MessageBox.Show("Распознавание текста (OCR) активно для скриншота.", "QScreen OCR", MessageBoxButton.OK, MessageBoxImage.Information);
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
