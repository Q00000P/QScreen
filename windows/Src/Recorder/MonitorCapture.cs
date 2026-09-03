using System;
using System.Drawing;
using Vortice.Direct3D11;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace QScreen.Recorder
{
    /// <summary>Сессия захвата одного монитора. Последний кадр всегда лежит в Texture (копия, живёт независимо от пула).</summary>
    internal sealed class MonitorCapture : IDisposable
    {
        public readonly Rectangle Bounds;   // физические пиксели виртуального экрана
        public ID3D11Texture2D? Texture;    // B8G8R8A8, ShaderResource
        public int Width, Height;
        public long FrameCounter;

        private readonly GpuDevice _gpu;
        private readonly GraphicsCaptureItem _item;
        private Direct3D11CaptureFramePool? _pool;
        private GraphicsCaptureSession? _session;
        private bool _disposed;

        public MonitorCapture(GpuDevice gpu, IntPtr hmon, Rectangle bounds)
        {
            _gpu = gpu; Bounds = bounds;
            _item = GpuDevice.CreateItemForMonitor(hmon);
            Width = _item.Size.Width; Height = _item.Size.Height;
            Texture = gpu.CreateTexture(Width, Height, BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None);
        }

        public void Start(bool showCursor)
        {
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_gpu.WinRTDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, new SizeInt32 { Width = Width, Height = Height });
            _pool.FrameArrived += OnFrame;
            _session = _pool.CreateCaptureSession(_item);
            try { _session.IsCursorCaptureEnabled = showCursor; } catch { }
            try
            {
                if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
                    _session.IsBorderRequired = false; // жёлтая рамка WGC (Win11)
            }
            catch { }
            _session.StartCapture();
        }

        private void OnFrame(Direct3D11CaptureFramePool sender, object args)
        {
            if (_disposed) return;
            using var frame = sender.TryGetNextFrame();
            if (frame == null) return;

            var size = frame.ContentSize;
            if (size.Width != Width || size.Height != Height)
            {
                // Сменилось разрешение монитора — пересоздаём пул и текстуру
                lock (_gpu.Lock)
                {
                    Width = size.Width; Height = size.Height;
                    Texture?.Dispose();
                    Texture = _gpu.CreateTexture(Width, Height, BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None);
                    BitmapInvalidated = true;
                }
                sender.Recreate(_gpu.WinRTDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                return;
            }

            using var src = GpuDevice.TextureFromSurface(frame.Surface);
            lock (_gpu.Lock)
            {
                if (Texture != null) _gpu.Context.CopyResource(Texture, src);
                FrameCounter++;
            }
        }

        /// <summary>Выставляется при пересоздании текстуры — композитору надо пересоздать D2D-битмап.</summary>
        public bool BitmapInvalidated;

        public void Dispose()
        {
            _disposed = true;
            try { _session?.Dispose(); } catch { }
            try { if (_pool != null) { _pool.FrameArrived -= OnFrame; _pool.Dispose(); } } catch { }
            lock (_gpu.Lock) { Texture?.Dispose(); Texture = null; }
        }
    }
}
