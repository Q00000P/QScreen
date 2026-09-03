using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QScreen
{
    public static class AppIconProvider
    {
        private static System.Drawing.Icon? _icon;

        public static System.Drawing.Icon GetAppIcon()
        {
            if (_icon != null) return _icon;
            // Сначала icon.ico рядом с exe, затем ресурс QScreen.exe (не ProcessPath — при запуске через dotnet это dotnet.exe)
            foreach (var f in new[] { "icon.ico", "QScreen.ico" })
            {
                var p = Path.Combine(AppContext.BaseDirectory, f);
                if (File.Exists(p)) { try { _icon = new System.Drawing.Icon(p); break; } catch { } }
            }
            if (_icon == null)
            {
                try
                {
                    var exe = Path.Combine(AppContext.BaseDirectory, "QScreen.exe");
                    if (!File.Exists(exe)) exe = Environment.ProcessPath ?? "";
                    if (File.Exists(exe)) _icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                }
                catch { }
            }
            return _icon ?? System.Drawing.SystemIcons.Application;
        }

        public static ImageSource? GetImageSource()
        {
            try { return Imaging.CreateBitmapSourceFromHIcon(GetAppIcon().Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); }
            catch { return null; }
        }
    }

    public static class UpdateChecker
    {
        public const string CurrentVersion = "10.1.0";
        public const string BuildTag = "w8"; // метка сборки для трея — поднимать при каждой правке
        public const string Repo = "Q00000P/QScreen";

        public static async Task CheckForUpdatesAsync(bool isUserInitiated = false)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "QScreen-Win");
                var json = await client.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var remoteVer = (root.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
                if (!Version.TryParse(remoteVer, out var latest)) throw new Exception("bad tag");

                if (latest > new Version(CurrentVersion))
                {
                    // Только виндовый ассет — в релизе лежит и macOS-zip
                    string zipUrl = "";
                    if (root.TryGetProperty("assets", out var assets))
                        foreach (var a in assets.EnumerateArray())
                        {
                            var name = a.GetProperty("name").GetString() ?? "";
                            if (name.Contains("Windows", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            { zipUrl = a.GetProperty("browser_download_url").GetString() ?? ""; break; }
                        }

                    var r = MessageBox.Show($"Доступна новая версия QScreen v{remoteVer}!\nТекущая: v{CurrentVersion}\n\nСкачать и установить обновление автоматически?",
                        "Обновление QScreen", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (r != MessageBoxResult.Yes) return;

                    if (zipUrl.Length > 0) await PerformSilentUpdate(zipUrl, remoteVer);
                    else Process.Start(new ProcessStartInfo(root.GetProperty("html_url").GetString() ?? "") { UseShellExecute = true });
                }
                else if (isUserInitiated)
                {
                    MessageBox.Show($"У вас установлена актуальная версия QScreen (v{CurrentVersion}).", "Обновлений нет", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                if (isUserInitiated) MessageBox.Show($"Не удалось проверить обновления.\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static async Task PerformSilentUpdate(string zipUrl, string newVer)
        {
            var win = new Window
            {
                Title = "Обновление QScreen", Width = 360, Height = 110, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Background = new SolidColorBrush(Color.FromRgb(28, 30, 36)), ResizeMode = ResizeMode.NoResize, Topmost = true, Icon = AppIconProvider.GetImageSource()
            };
            Win32.ApplyDarkMode(win);
            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = $"Загрузка QScreen v{newVer}...", Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 10), FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new System.Windows.Controls.ProgressBar { Height = 14, IsIndeterminate = true });
            win.Content = panel;
            win.Show();

            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "QScreen_Update_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                var zipFile = Path.Combine(tempDir, "update.zip");
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "QScreen-Win");
                    await File.WriteAllBytesAsync(zipFile, await client.GetByteArrayAsync(zipUrl));
                }
                var extractDir = Path.Combine(tempDir, "extracted");
                ZipFile.ExtractToDirectory(zipFile, extractDir);

                var exe = Environment.ProcessPath!;
                var installDir = Path.GetDirectoryName(exe)!;
                var script = Path.Combine(tempDir, "apply_update.ps1");
                File.WriteAllText(script, $@"
Start-Sleep -Milliseconds 800
Copy-Item -Path '{extractDir}\*' -Destination '{installDir}' -Recurse -Force
Start-Process '{exe}'
Remove-Item -Path '{tempDir}' -Recurse -Force
", Encoding.UTF8);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-ExecutionPolicy Bypass -NoProfile -WindowStyle Hidden -File \"{script}\"",
                    UseShellExecute = true, CreateNoWindow = true
                });
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                win.Close();
                MessageBox.Show($"Ошибка при установке обновления: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
