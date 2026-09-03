using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace QScreen
{
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            using var mutex = new Mutex(true, "QScreen_SingleInstance", out bool first);
            if (!first)
            {
                MessageBox.Show("QScreen уже запущен — иконка в трее.\nЕсли он не отвечает: taskkill /IM QScreen.exe /F", "QScreen", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) => { Log(e.Exception); e.SetObserved(); };
            try
            {
                AppSettings.Load();
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (s, e) => { e.Handled = true; Log(e.Exception); try { MessageBox.Show(e.Exception.ToString(), "QScreen — ошибка"); } catch { } };
                var controller = new AppController();
                app.Run();
                controller.Dispose();
            }
            catch (Exception ex) { Crash(ex); }
        }

        public static string LogPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QScreen", "crash.log");

        public static void Trace(string msg)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
                System.IO.File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
            }
            catch { }
        }

        public static void Log(Exception? ex)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
                System.IO.File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
            }
            catch { }
        }

        private static void Crash(Exception? ex)
        {
            Log(ex);
            try { MessageBox.Show($"{ex}\n\nЛог: {LogPath}", "QScreen — крэш при запуске"); } catch { }
        }
    }

    public sealed partial class AppController : IDisposable
    {
        public static AppController? Shared { get; private set; }

        private const int HK_AREA = 1, HK_SMART = 2, HK_SCROLL = 3, HK_SCREEN = 4, HK_RECORD = 5, HK_RECORD_STOP = 6, HK_RECORD_PAUSE = 7;

        private readonly Forms.NotifyIcon _tray;
        private readonly HwndSource _msgWindow;
        private EditorWindow? _editor;
        private SettingsWindow? _settings;
        private readonly List<PinnedWindow> _pinned = new();

        public AppController()
        {
            Shared = this;

            _tray = new Forms.NotifyIcon { Icon = AppIconProvider.GetAppIcon(), Text = $"QScreen v{UpdateChecker.CurrentVersion} ({UpdateChecker.BuildTag})", Visible = true };
            _tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) CaptureAreaAction(); };

            _msgWindow = new HwndSource(new HwndSourceParameters("QScreenMsg") { WindowStyle = 0, Width = 0, Height = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ });
            _msgWindow.AddHook(WndProc);
            InitRecorder();
            RegisterHotkeys();

            _ = UpdateChecker.CheckForUpdatesAsync(false);
        }

        // ---------- Трей ----------
        public void UpdateTrayMenu(bool isRecording)
        {
            var m = new Forms.ContextMenuStrip();
            if (isRecording)
            {
                m.Items.Add("⏹ Остановить запись", null, (s, e) => StopRecordingAction());
                m.Items.Add("⏸ Пауза / Продолжить", null, (s, e) => TogglePauseAction());
            }
            else
            {
                m.Items.Add($"🎯 Захват области ({AppSettings.HK_Area.DisplayText})", null, (s, e) => CaptureAreaAction());
                m.Items.Add($"🔲 Умный захват (Окно / Зона) ({AppSettings.HK_Smart.DisplayText})", null, (s, e) => CaptureSmartAction());
                m.Items.Add($"📜 Скролл-скриншот ({AppSettings.HK_Scroll.DisplayText})", null, (s, e) => CaptureScrollAction());
                m.Items.Add($"🖥 Захват экрана ({AppSettings.HK_Screen.DisplayText})", null, (s, e) => CaptureScreenAction());
                m.Items.Add($"🎬 Запись видео области ({AppSettings.HK_Record.DisplayText})", null, (s, e) => RecordAreaAction());
            }
            m.Items.Add(new Forms.ToolStripSeparator());
            m.Items.Add("⚙ Настройки...", null, (s, e) => ShowSettings());
            m.Items.Add("🔄 Проверить обновления...", null, (s, e) => _ = UpdateChecker.CheckForUpdatesAsync(true));
            m.Items.Add(new Forms.ToolStripSeparator());
            m.Items.Add("Выход", null, (s, e) => Quit());
            _tray.ContextMenuStrip = m;
        }

        // ---------- Хоткеи ----------
        public void RegisterHotkeys()
        {
            var h = _msgWindow.Handle;
            for (int i = 1; i <= 7; i++) Win32.UnregisterHotKey(h, i);
            Reg(HK_AREA, AppSettings.HK_Area); Reg(HK_SMART, AppSettings.HK_Smart); Reg(HK_SCROLL, AppSettings.HK_Scroll); Reg(HK_SCREEN, AppSettings.HK_Screen);
            Reg(HK_RECORD, AppSettings.HK_Record); Reg(HK_RECORD_STOP, AppSettings.HK_RecordStop); Reg(HK_RECORD_PAUSE, AppSettings.HK_RecordPause);
            UpdateTrayMenu(IsRecording);
        }

        private void Reg(int id, HotkeyBinding b)
        {
            if (b.Key == 0) return;
            if (!Win32.RegisterHotKey(_msgWindow.Handle, id, b.Modifiers | Win32.MOD_NOREPEAT, b.Key))
                _tray.ShowBalloonTip(3000, "QScreen", $"Хоткей {b.DisplayText} занят другим приложением", Forms.ToolTipIcon.Warning);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != Win32.WM_HOTKEY) return IntPtr.Zero;
            handled = true;
            switch (wParam.ToInt32())
            {
                case HK_AREA: CaptureAreaAction(); break;
                case HK_SMART: CaptureSmartAction(); break;
                case HK_SCROLL: CaptureScrollAction(); break;
                case HK_SCREEN: CaptureScreenAction(); break;
                case HK_RECORD: RecordAreaAction(); break;
                case HK_RECORD_STOP: StopRecordingAction(); break;
                case HK_RECORD_PAUSE: TogglePauseAction(); break;
            }
            return IntPtr.Zero;
        }

        // ---------- Действия ----------
        /// <summary>Хоткей всегда сбрасывает незавершённый оверлей/скролл-сессию — иначе застрявшее окно блокирует всё.</summary>
        private void ResetPending()
        {
            OverlayManager.Close();
            ScrollCaptureManager.Cancel();
            FloatingThumbnailManager.Dismiss();
        }

        public void CaptureAreaAction()
        {
            ResetPending();
            OverlayManager.ShowAreaOverlay(HandleCapturedImage);
        }

        public void CaptureScrollAction()
        {
            ResetPending();
            OverlayManager.ShowScrollOverlay(HandleCapturedImage);
        }

        public void CaptureSmartAction()
        {
            ResetPending();
            OverlayManager.ShowSmartOverlay(HandleCapturedImage);
        }

        public void CaptureScreenAction()
        {
            ResetPending();
            var p = Win32.CursorPos();
            using var bmp = CaptureEngine.CaptureFullScreen();
            if (bmp != null) OpenEditor(OverlayManager.Tag(BitmapUtil.ToSource(bmp), Win32.ScaleForPoint(p.X, p.Y)));
        }

        private void HandleCapturedImage(BitmapSource img)
        {
            if (AppSettings.ShowThumbnail) FloatingThumbnailManager.Show(img, () => OpenEditor(img));
            else OpenEditor(img);
        }

        public void OpenEditor(BitmapSource img)
        {
            CloseEditor();
            _editor = new EditorWindow(img) { OnPin = pinned => { CloseEditor(); CreatePinnedWindow(pinned); } };
            _editor.Closed += (s, e) => { if (_editor == s) _editor = null; };
            _editor.Show();
            _editor.Activate();
        }

        public void CloseEditor()
        {
            var e = _editor; _editor = null;
            e?.Close();
        }

        public void CreatePinnedWindow(BitmapSource img)
        {
            var w = new PinnedWindow(img);
            w.Closed += (s, e) => _pinned.Remove(w);
            _pinned.Add(w);
            w.Show();
        }

        public void ShowSettings()
        {
            if (_settings == null)
            {
                _settings = new SettingsWindow(RegisterHotkeys);
                _settings.Closed += (s, e) => _settings = null;
            }
            _settings.Show(); _settings.Activate();
        }

        public void CloseSettings() => _settings?.Close();

        private void Quit()
        {
            Dispose();
            Application.Current.Shutdown();
        }

        public void Dispose()
        {
            try { for (int i = 1; i <= 7; i++) Win32.UnregisterHotKey(_msgWindow.Handle, i); } catch { }
            _tray.Visible = false;
            _tray.Dispose();
        }
    }
}
