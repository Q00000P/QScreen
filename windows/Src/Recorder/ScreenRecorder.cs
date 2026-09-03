using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using D2DAlphaMode = Vortice.DCommon.AlphaMode;

namespace QScreen.Recorder
{
    /// <summary>
    /// Аналог ScreenRecorder мака: захватываем все мониторы целиком (WGC), каждый кадр обрезаем и масштабируем на GPU (Direct2D)
    /// в фиксированный холст, отдаём ffmpeg. Зона записи двигается/ресайзится на лету и может лежать на стыке мониторов.
    /// </summary>
    public sealed class ScreenRecorder
    {
        public static readonly ScreenRecorder Shared = new();

        public bool IsRecording { get; private set; }
        public bool IsPaused { get; private set; }
        public bool IsAudioMuted { get; private set; }
        public Action<bool>? OnStateChange;
        public TimeSpan Elapsed => _clock.Elapsed - _pausedTotal - (IsPaused ? _clock.Elapsed - _pauseStart : TimeSpan.Zero);

        private GpuDevice? _gpu;
        private readonly List<MonitorCapture> _monitors = new();
        private readonly Dictionary<MonitorCapture, ID2D1Bitmap1> _monBitmaps = new();
        private ID2D1Factory1? _d2dFactory;
        private ID2D1Device? _d2dDevice;
        private ID2D1DeviceContext? _d2d;
        private ID3D11Texture2D? _canvas, _staging, _blurTex;
        private ID2D1Bitmap1? _target, _blurBmp;
        private readonly object _blurLock = new();
        private readonly List<Rectangle> _blurRects = new();   // физ. пиксели, снимок для потока кодирования
        private FfmpegEncoder? _enc;
        private AudioCapture? _audio;
        private Thread? _thread;
        private volatile bool _stop;
        private byte[] _frameBuf = Array.Empty<byte>();

        private int _canvasW, _canvasH, _fps;
        private Rectangle _crop;              // физ. пиксели виртуального экрана
        private readonly object _cropLock = new();
        private readonly Stopwatch _clock = new();
        private TimeSpan _pausedTotal, _pauseStart;
        private string _outputPath = "";
        private string? _failure;

        private LiveResizableFrameWindow? _frameWin;
        private RecordingControlBarWindow? _bar;
        private readonly List<LiveBlurZoneWindow> _blurZones = new();

        // ---------- Вооружение: рамка + панель, запись ещё не идёт ----------
        public bool IsArmed { get; private set; }
        private string? _ffmpegPath;

        public async void StartRecording(Rectangle initialRect)
        {
            if (IsRecording || IsArmed) return;
            _ffmpegPath = await FfmpegInstaller.EnsureAsync();
            if (_ffmpegPath == null) return;
            if (IsRecording || IsArmed) return;

            AppController.Shared?.CloseEditor();
            AppController.Shared?.CloseSettings();

            _crop = initialRect;
            IsArmed = true; IsPaused = false; IsAudioMuted = false;
            _bar = new RecordingControlBarWindow(this);
            _bar.Show();
            _frameWin = new LiveResizableFrameWindow(initialRect, OnFrameChanged);
            _frameWin.Show();
            _bar.PlaceNear(initialRect);
        }

        // ---------- Реальный старт ----------
        public void BeginCapture()
        {
            if (!IsArmed || IsRecording) return;
            Rectangle initialRect; lock (_cropLock) initialRect = _crop;
            try
            {
                _gpu = new GpuDevice();

                // Все мониторы — зона записи может уехать на любой из них
                foreach (var scr in System.Windows.Forms.Screen.AllScreens)
                {
                    var b = scr.Bounds;
                    var hmon = Win32.MonitorFromPoint(new Win32.POINT { X = b.X + b.Width / 2, Y = b.Y + b.Height / 2 }, Win32.MONITOR_DEFAULTTONEAREST);
                    _monitors.Add(new MonitorCapture(_gpu, hmon, b));
                }

                _canvasW = Math.Max(640, initialRect.Width & ~1);
                _canvasH = Math.Max(360, initialRect.Height & ~1);
                _fps = AppSettings.VideoFps;

                InitComposer();

                var ext = AppSettings.VideoFormat == "mov" ? "mov" : "mp4";
                _outputPath = Path.Combine(FilenameHelper.GetDefaultSaveFolder(), FilenameHelper.GenerateFilename(ext));
                bool audio = AppSettings.RecordAudio && AudioCapture.HasMicrophone();

                _enc = new FfmpegEncoder();
                _enc.Start(_ffmpegPath!, _outputPath, _canvasW, _canvasH, _fps, AppSettings.VideoCodec, audio);
                Program.Trace($"record start crop={initialRect} canvas={_canvasW}x{_canvasH} fps={_fps} codec={AppSettings.VideoCodec} encoder={FfmpegEncoder.PickEncoder(_ffmpegPath!, AppSettings.VideoCodec)} audio={audio} monitors={_monitors.Count}");
                if (audio) { _audio = new AudioCapture(_enc); _audio.Start(); }

                foreach (var m in _monitors) m.Start(AppSettings.ShowCursor);

                _pausedTotal = TimeSpan.Zero; _stop = false; _failure = null;
                IsRecording = true; IsArmed = false; IsPaused = false;
                _clock.Restart();
                _thread = new Thread(EncodeLoop) { IsBackground = true, Name = "QScreen.Encode", Priority = ThreadPriority.AboveNormal };
                _thread.Start();
                _bar?.Refresh();
                OnStateChange?.Invoke(true);
            }
            catch (Exception ex)
            {
                IsArmed = false;
                Cleanup();
                MessageBox.Show("Не удалось запустить запись:\n" + ex, "QScreen", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void InitComposer()
        {
            var gpu = _gpu!;
            _canvas = gpu.CreateTexture(_canvasW, _canvasH, BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None);
            _staging = gpu.CreateTexture(_canvasW, _canvasH, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);

            _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded);
            using var dxgi = gpu.D3D.QueryInterface<IDXGIDevice>();
            _d2dDevice = _d2dFactory.CreateDevice(dxgi);
            _d2d = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);

            using var surf = _canvas.QueryInterface<IDXGISurface>();
            var props = new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw);
            _target = _d2d.CreateBitmapFromDxgiSurface(surf, props);
            _d2d.Target = _target;
            _frameBuf = new byte[_canvasW * _canvasH * 4];
        }

        private ID2D1Bitmap1 GetMonitorBitmap(MonitorCapture m)
        {
            if (m.BitmapInvalidated && _monBitmaps.TryGetValue(m, out var old)) { old.Dispose(); _monBitmaps.Remove(m); m.BitmapInvalidated = false; }
            if (_monBitmaps.TryGetValue(m, out var bmp)) return bmp;
            using var surf = m.Texture!.QueryInterface<IDXGISurface>();
            var props = new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96, BitmapOptions.None);
            bmp = _d2d!.CreateBitmapFromDxgiSurface(surf, props);
            _monBitmaps[m] = bmp;
            return bmp;
        }

        // ---------- Кадр: композиция всех мониторов в холст с кропом и зумом + пикселизация блюр-зон ----------
        /// <summary>Рисует область экрана region (физ. пиксели) в текущий Target с масштабом sx/sy; (0,0) target = region.Location.</summary>
        private void DrawRegion(Rectangle region, float sx, float sy, BitmapInterpolationMode mode)
        {
            foreach (var m in _monitors)
            {
                if (m.Texture == null) continue;
                var inter = Rectangle.Intersect(region, m.Bounds);
                if (inter.Width <= 0 || inter.Height <= 0) continue;
                float kx = (float)m.Width / m.Bounds.Width, ky = (float)m.Height / m.Bounds.Height;
                var src = new RectangleF((inter.X - m.Bounds.X) * kx, (inter.Y - m.Bounds.Y) * ky, inter.Width * kx, inter.Height * ky);
                var dst = new RectangleF((inter.X - region.X) * sx, (inter.Y - region.Y) * sy, inter.Width * sx, inter.Height * sy);
                _d2d!.DrawBitmap(GetMonitorBitmap(m), dst, 1f, mode, src);
            }
        }

        private void EnsureBlurScratch(int w, int h)
        {
            if (_blurTex != null && _blurBmp != null) return;
            _blurTex = _gpu!.CreateTexture(w, h, BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None);
            using var surf = _blurTex.QueryInterface<IDXGISurface>();
            _blurBmp = _d2d!.CreateBitmapFromDxgiSurface(surf, new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
        }

        private void ComposeFrame()
        {
            Rectangle crop;
            lock (_cropLock) crop = _crop;
            float sx = (float)_canvasW / Math.Max(1, crop.Width);
            float sy = (float)_canvasH / Math.Max(1, crop.Height);
            Rectangle[] blurs;
            lock (_blurLock) blurs = _blurRects.ToArray();

            var gpu = _gpu!;
            lock (gpu.Lock)
            {
                _d2d!.Target = _target;
                _d2d.BeginDraw();
                _d2d.Clear(new Color4(0f, 0f, 0f, 1f));
                DrawRegion(crop, sx, sy, BitmapInterpolationMode.Linear);
                _d2d.EndDraw();

                // Пикселизация: область → уменьшить в 16 раз в scratch → нарисовать обратно NearestNeighbor (аналог CIPixellate scale=16)
                foreach (var bz in blurs)
                {
                    var inter = Rectangle.Intersect(bz, crop);
                    if (inter.Width < 2 || inter.Height < 2) continue;
                    const int block = 16;
                    int sw = Math.Max(1, inter.Width / block), sh = Math.Max(1, inter.Height / block);
                    EnsureBlurScratch(256, 256); // фикс. scratch 256×256, используем его часть
                    sw = Math.Min(sw, 256); sh = Math.Min(sh, 256);

                    _d2d.Target = _blurBmp;
                    _d2d.BeginDraw();
                    _d2d.Clear(new Color4(0f, 0f, 0f, 1f));
                    DrawRegion(inter, (float)sw / inter.Width, (float)sh / inter.Height, BitmapInterpolationMode.Linear);
                    _d2d.EndDraw();

                    _d2d.Target = _target;
                    _d2d.BeginDraw();
                    var dst = new RectangleF((inter.X - crop.X) * sx, (inter.Y - crop.Y) * sy, inter.Width * sx, inter.Height * sy);
                    _d2d.DrawBitmap(_blurBmp!, dst, 1f, BitmapInterpolationMode.NearestNeighbor, new RectangleF(0, 0, sw, sh));
                    _d2d.EndDraw();
                }

                gpu.Context.CopyResource(_staging!, _canvas!);
                var map = gpu.Context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    int rowBytes = _canvasW * 4;
                    unsafe
                    {
                        byte* srcPtr = (byte*)map.DataPointer;
                        fixed (byte* dstPtr = _frameBuf)
                        {
                            if ((int)map.RowPitch == rowBytes) Buffer.MemoryCopy(srcPtr, dstPtr, _frameBuf.Length, _frameBuf.Length);
                            else for (int y = 0; y < _canvasH; y++) Buffer.MemoryCopy(srcPtr + y * (long)map.RowPitch, dstPtr + y * rowBytes, rowBytes, rowBytes);
                        }
                    }
                }
                finally { gpu.Context.Unmap(_staging!, 0); }
            }
        }

        // ---------- Цикл кодирования: держим CFR по настенным часам ----------
        private void EncodeLoop()
        {
            long written = 0;
            double frameMs = 1000.0 / _fps;
            try
            {
                while (!_stop)
                {
                    if (IsPaused) { Thread.Sleep(20); continue; }
                    double activeMs = (_clock.Elapsed - _pausedTotal).TotalMilliseconds;
                    long due = (long)(activeMs / frameMs);
                    if (due <= written)
                    {
                        double wait = (written + 1) * frameMs - activeMs;
                        if (wait > 2) Thread.Sleep((int)Math.Min(wait, 50)); else Thread.Yield();
                        continue;
                    }
                    ComposeFrame();
                    // Если отстали — дублируем кадр, чтобы не уехал звук (но не больше 5 подряд)
                    long n = Math.Min(due - written, 5);
                    for (long i = 0; i < n; i++) _enc!.WriteVideoFrame(_frameBuf, _frameBuf.Length);
                    written = due;
                    if (!_enc!.IsRunning) { _failure = "ffmpeg завершился: " + _enc.LastError; break; }
                }
            }
            catch (Exception ex) { _failure = ex.ToString(); }

            if (_failure != null && !_stop)
                Application.Current.Dispatcher.BeginInvoke(new Action(() => { StopRecording(); MessageBox.Show(_failure, "QScreen — запись прервана", MessageBoxButton.OK, MessageBoxImage.Error); }));
        }

        // ---------- Управление ----------
        public void UpdateCropRect(Rectangle newRect)
        {
            lock (_cropLock) _crop = newRect;
        }

        /// <summary>Перенос рамки (размер не изменился) тащит за собой блюр-зоны; ресайз зоны не трогает.</summary>
        private void OnFrameChanged(Rectangle newRect)
        {
            Rectangle old; lock (_cropLock) old = _crop;
            if (old.Size == newRect.Size && old.Location != newRect.Location)
            {
                int dx = newRect.X - old.X, dy = newRect.Y - old.Y;
                foreach (var z in _blurZones.ToArray()) z.Offset(dx, dy);
            }
            UpdateCropRect(newRect);
            _bar?.PlaceNear(newRect);
        }

        public void TogglePause()
        {
            if (!IsRecording) return;
            IsPaused = !IsPaused;
            if (IsPaused) _pauseStart = _clock.Elapsed;
            else _pausedTotal += _clock.Elapsed - _pauseStart;
            if (_audio != null) _audio.Paused = IsPaused;
            _bar?.Refresh();
        }

        public void ToggleMicrophone()
        {
            IsAudioMuted = !IsAudioMuted;
            if (_audio != null) _audio.Muted = IsAudioMuted;
            _bar?.Refresh();
        }

        public void AddLiveBlurZone()
        {
            Rectangle crop; lock (_cropLock) crop = _crop;
            var z = new LiveBlurZoneWindow(new Rectangle(crop.X + crop.Width / 2 - 110, crop.Y + crop.Height / 2 - 50, 220, 100), _ => SyncBlurRects());
            z.Closed += (s, e) => { _blurZones.Remove(z); SyncBlurRects(); };
            _blurZones.Add(z);
            z.Show();
            SyncBlurRects();
        }

        private void SyncBlurRects()
        {
            lock (_blurLock)
            {
                _blurRects.Clear();
                foreach (var z in _blurZones) _blurRects.Add(z.Zone);
            }
        }

        public void StopRecording()
        {
            if (IsArmed && !IsRecording) { IsArmed = false; Cleanup(); return; }
            if (!IsRecording) return;
            IsRecording = false; IsPaused = false;
            OnStateChange?.Invoke(false);
            _stop = true;
            _thread?.Join(3000);

            bool ok = false;
            string err = "";
            try
            {
                _audio?.Dispose(); _audio = null;
                foreach (var m in _monitors) m.Dispose();
                if (_enc != null) { ok = _enc.Finish(); err = _enc.LastError; }
            }
            catch (Exception ex) { err = ex.ToString(); }
            Cleanup();

            var path = _outputPath;
            if (ok && File.Exists(path))
            {
                try
                {
                    var d = new DataObject();
                    d.SetData(DataFormats.FileDrop, new[] { path });
                    d.SetText(path);
                    Clipboard.SetDataObject(d, true);
                }
                catch { }
                try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); } catch { }
            }
            else if (_failure == null)
            {
                MessageBox.Show("Запись не сохранилась.\n" + err, "QScreen", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void CancelRecording()
        {
            if (IsArmed && !IsRecording) { IsArmed = false; Cleanup(); return; }
            if (!IsRecording) return;
            IsRecording = false; IsPaused = false;
            OnStateChange?.Invoke(false);
            _stop = true;
            _thread?.Join(2000);
            try { _audio?.Dispose(); _audio = null; foreach (var m in _monitors) m.Dispose(); _enc?.Abort(); } catch { }
            Cleanup();
            try { File.Delete(_outputPath); } catch { }
        }

        private void Cleanup()
        {
            IsArmed = false;
            try { _bar?.Close(); } catch { } _bar = null;
            try { _frameWin?.Close(); } catch { } _frameWin = null;
            foreach (var z in _blurZones.ToArray()) { try { z.Close(); } catch { } }
            _blurZones.Clear();

            _enc?.Dispose(); _enc = null;
            _audio?.Dispose(); _audio = null;
            foreach (var m in _monitors) { try { m.Dispose(); } catch { } }
            _monitors.Clear();
            foreach (var b in _monBitmaps.Values) b.Dispose();
            _monBitmaps.Clear();
            _target?.Dispose(); _target = null;
            _blurBmp?.Dispose(); _blurBmp = null;
            _blurTex?.Dispose(); _blurTex = null;
            lock (_blurLock) _blurRects.Clear();
            _d2d?.Dispose(); _d2d = null;
            _d2dDevice?.Dispose(); _d2dDevice = null;
            _d2dFactory?.Dispose(); _d2dFactory = null;
            _staging?.Dispose(); _staging = null;
            _canvas?.Dispose(); _canvas = null;
            _gpu?.Dispose(); _gpu = null;
            _thread = null;
        }
    }
}
