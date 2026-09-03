using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QScreen.Recorder
{
    /// <summary>Скачивает ffmpeg по запросу пользователя (как автообновление): один вопрос, прогресс, готово.</summary>
    internal static class FfmpegInstaller
    {
        private const string DownloadUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";

        public static async Task<string?> EnsureAsync()
        {
            var found = FfmpegEncoder.FindFfmpeg();
            if (found != null) return found;

            var r = System.Windows.MessageBox.Show(
                "Для записи видео нужен ffmpeg (~110 МБ, разовая загрузка).\n\nСкачать и установить сейчас?",
                "QScreen — запись видео", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (r != System.Windows.MessageBoxResult.Yes) return null;

            var win = new System.Windows.Window
            {
                Title = "QScreen — загрузка ffmpeg", Width = 380, Height = 120, WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 30, 36)), ResizeMode = System.Windows.ResizeMode.NoResize, Topmost = true,
                Icon = AppIconProvider.GetImageSource()
            };
            Win32.ApplyDarkMode(win);
            var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(16) };
            var label = new System.Windows.Controls.TextBlock { Text = "Загрузка ffmpeg...", Foreground = System.Windows.Media.Brushes.White, Margin = new System.Windows.Thickness(0, 0, 0, 10), FontWeight = System.Windows.FontWeights.SemiBold };
            var bar = new System.Windows.Controls.ProgressBar { Height = 14, Minimum = 0, Maximum = 100, IsIndeterminate = true };
            panel.Children.Add(label); panel.Children.Add(bar);
            win.Content = panel;
            win.Show();

            try
            {
                Directory.CreateDirectory(FfmpegEncoder.LocalFfmpegDir);
                var zipPath = Path.Combine(FfmpegEncoder.LocalFfmpegDir, "ffmpeg.zip");
                using (var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(15) })
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "QScreen-Win");
                    using var resp = await client.GetAsync(DownloadUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? -1;
                    if (total > 0) bar.IsIndeterminate = false;
                    using var src = await resp.Content.ReadAsStreamAsync();
                    using var dst = File.Create(zipPath);
                    var buf = new byte[1 << 16]; long done = 0; int n; var lastUi = DateTime.MinValue;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n); done += n;
                        if ((DateTime.Now - lastUi).TotalMilliseconds > 150)
                        {
                            lastUi = DateTime.Now;
                            if (total > 0) { bar.Value = done * 100.0 / total; label.Text = $"Загрузка ffmpeg... {done / 1048576} / {total / 1048576} МБ"; }
                            else label.Text = $"Загрузка ffmpeg... {done / 1048576} МБ";
                        }
                    }
                }

                label.Text = "Распаковка..."; bar.IsIndeterminate = true;
                var exePath = Path.Combine(FfmpegEncoder.LocalFfmpegDir, "ffmpeg.exe");
                await Task.Run(() =>
                {
                    using var zip = ZipFile.OpenRead(zipPath);
                    var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase) || e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                                ?? throw new Exception("в архиве нет ffmpeg.exe");
                    entry.ExtractToFile(exePath, true);
                });
                try { File.Delete(zipPath); } catch { }

                // Проверка, что бинарник живой
                using var p = Process.Start(new ProcessStartInfo(exePath, "-version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
                p.WaitForExit(5000);
                if (p.ExitCode != 0) throw new Exception("ffmpeg.exe не запускается");

                win.Close();
                return exePath;
            }
            catch (Exception ex)
            {
                win.Close();
                System.Windows.MessageBox.Show("Не удалось установить ffmpeg:\n" + ex.Message + "\n\nВручную: winget install Gyan.FFmpeg", "QScreen", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return null;
            }
        }
    }

    internal sealed class FfmpegEncoder : IDisposable
    {
        public const string AudioPipeName = "qscreen_audio";
        public const int AudioRate = 44100, AudioChannels = 2;

        private Process? _proc;
        private Stream? _videoIn;
        private NamedPipeServerStream? _audioPipe;
        private readonly StringBuilder _stderr = new();
        private readonly object _vLock = new(), _aLock = new();
        private bool _audioConnected;

        public string OutputPath { get; private set; } = "";
        public string LastError => _stderr.ToString();

        // ---------- Поиск ffmpeg ----------
        public static string LocalFfmpegDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QScreen", "ffmpeg");

        public static string? FindFfmpeg()
        {
            var candidates = new List<string>
            {
                Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                Path.Combine(LocalFfmpegDir, "ffmpeg.exe"),
                Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "ffmpeg.exe"),
            };
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            candidates.AddRange(path.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(d => Path.Combine(d.Trim(), "ffmpeg.exe")));
            return candidates.FirstOrDefault(File.Exists);
        }

        // ---------- Выбор энкодера (кэш на процесс) ----------
        private static readonly Dictionary<string, string> _encoderCache = new();

        /// <summary>Возвращает имя рабочего энкодера для кодека: nvenc → qsv → amf → libx264/libx265.</summary>
        public static string PickEncoder(string ffmpeg, string codec)
        {
            lock (_encoderCache)
            {
                if (_encoderCache.TryGetValue(codec, out var cached)) return cached;
                string[] hw = codec == "hevc" ? new[] { "hevc_nvenc", "hevc_qsv", "hevc_amf" } : new[] { "h264_nvenc", "h264_qsv", "h264_amf" };
                string sw = codec == "hevc" ? "libx265" : "libx264";
                string available = Run(ffmpeg, "-hide_banner -encoders", 5000);
                foreach (var enc in hw)
                {
                    if (!available.Contains(" " + enc + " ")) continue;
                    // Реальная проверка: энкодер может быть собран, но железа нет
                    var probe = Run(ffmpeg, $"-hide_banner -loglevel error -f lavfi -i nullsrc=s=256x256:r=10:d=0.3 -c:v {enc} -f null -", 8000, out int code);
                    if (code == 0) { _encoderCache[codec] = enc; return enc; }
                }
                _encoderCache[codec] = sw;
                return sw;
            }
        }

        private static string Run(string exe, string args, int timeoutMs) => Run(exe, args, timeoutMs, out _);
        private static string Run(string exe, string args, int timeoutMs, out int exitCode)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
                exitCode = p.HasExited ? p.ExitCode : -1;
                return outTask.Result + "\n" + errTask.Result;
            }
            catch { exitCode = -1; return ""; }
        }

        private static string EncoderArgs(string enc) => enc switch
        {
            "h264_nvenc" => "-c:v h264_nvenc -preset p4 -tune hq -rc vbr -cq 21 -b:v 0 -pix_fmt yuv420p",
            "hevc_nvenc" => "-c:v hevc_nvenc -preset p4 -tune hq -rc vbr -cq 24 -b:v 0 -pix_fmt yuv420p -tag:v hvc1",
            "h264_qsv" => "-c:v h264_qsv -preset medium -global_quality 21 -pix_fmt nv12",
            "hevc_qsv" => "-c:v hevc_qsv -preset medium -global_quality 24 -pix_fmt nv12 -tag:v hvc1",
            "h264_amf" => "-c:v h264_amf -quality quality -rc cqp -qp_i 21 -qp_p 21 -pix_fmt yuv420p",
            "hevc_amf" => "-c:v hevc_amf -quality quality -rc cqp -qp_i 24 -qp_p 24 -pix_fmt yuv420p -tag:v hvc1",
            "libx265" => "-c:v libx265 -preset fast -crf 24 -pix_fmt yuv420p -tag:v hvc1",
            _ => "-c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p",
        };

        // ---------- Запуск ----------
        public void Start(string ffmpeg, string outputPath, int width, int height, int fps, string codec, bool withAudio)
        {
            OutputPath = outputPath;
            var enc = PickEncoder(ffmpeg, codec);
            var sb = new StringBuilder();
            sb.Append("-hide_banner -loglevel error -y -thread_queue_size 1024 ");
            sb.Append($"-f rawvideo -pix_fmt bgra -s {width}x{height} -framerate {fps} -i pipe:0 ");
            if (withAudio)
            {
                _audioPipe = new NamedPipeServerStream(AudioPipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20);
                sb.Append($"-thread_queue_size 1024 -f s16le -ar {AudioRate} -ac {AudioChannels} -i \\\\.\\pipe\\{AudioPipeName} ");
            }
            sb.Append(EncoderArgs(enc)).Append(' ');
            if (withAudio) sb.Append("-c:a aac -b:a 128k ");
            sb.Append("-movflags +faststart ");
            sb.Append('"').Append(outputPath).Append('"');

            _proc = new Process
            {
                StartInfo = new ProcessStartInfo(ffmpeg, sb.ToString())
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = false
                },
                EnableRaisingEvents = true
            };
            _proc.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (_stderr) { _stderr.AppendLine(e.Data); if (_stderr.Length > 8000) _stderr.Remove(0, 4000); } };
            _proc.Start();
            _proc.BeginErrorReadLine();
            _videoIn = new BufferedStream(_proc.StandardInput.BaseStream, 1 << 20);

            if (_audioPipe != null)
            {
                // ffmpeg подключится к пайпу как клиент, когда дойдёт до второго входа
                var pipe = _audioPipe;
                Task.Run(() => { try { pipe.WaitForConnection(); _audioConnected = true; } catch { } });
            }
        }

        public bool IsRunning => _proc != null && !_proc.HasExited;

        public void WriteVideoFrame(byte[] bgra, int length)
        {
            lock (_vLock)
            {
                if (_videoIn == null || !IsRunning) return;
                try { _videoIn.Write(bgra, 0, length); } catch { }
            }
        }

        public void WriteAudio(byte[] pcm, int length)
        {
            if (_audioPipe == null || !_audioConnected) return;
            lock (_aLock)
            {
                try { _audioPipe.Write(pcm, 0, length); } catch { }
            }
        }

        /// <summary>Корректное завершение: закрываем входы, ждём финализацию mp4.</summary>
        public bool Finish(int timeoutMs = 20000)
        {
            lock (_vLock) { try { _videoIn?.Flush(); _videoIn?.Dispose(); } catch { } _videoIn = null; }
            lock (_aLock) { try { _audioPipe?.Flush(); _audioPipe?.Dispose(); } catch { } _audioPipe = null; }
            if (_proc == null) return false;
            bool ok = _proc.WaitForExit(timeoutMs);
            if (!ok) { try { _proc.Kill(); } catch { } }
            return ok && _proc.ExitCode == 0;
        }

        public void Abort()
        {
            lock (_vLock) { try { _videoIn?.Dispose(); } catch { } _videoIn = null; }
            lock (_aLock) { try { _audioPipe?.Dispose(); } catch { } _audioPipe = null; }
            try { if (_proc != null && !_proc.HasExited) _proc.Kill(); } catch { }
        }

        public void Dispose()
        {
            Abort();
            _proc?.Dispose(); _proc = null;
        }
    }
}
