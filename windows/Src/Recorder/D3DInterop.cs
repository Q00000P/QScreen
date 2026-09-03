using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace QScreen.Recorder
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    /// <summary>Общее D3D11-устройство для захвата и композиции. Один на процесс.</summary>
    internal sealed class GpuDevice : IDisposable
    {
        [DllImport("d3d11.dll")]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        private static readonly Guid IID_GraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

        public readonly ID3D11Device D3D;
        public readonly ID3D11DeviceContext Context;
        public readonly IDirect3DDevice WinRTDevice;
        public readonly object Lock = new();

        public GpuDevice()
        {
            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
            var r = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out ID3D11Device dev, out ID3D11DeviceContext ctx);
            if (r.Failure) D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out dev, out ctx).CheckError();
            D3D = dev; Context = ctx;

            using var dxgi = D3D.QueryInterface<IDXGIDevice>();
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr p));
            WinRTDevice = MarshalInterface<IDirect3DDevice>.FromAbi(p);
            Marshal.Release(p);
        }

        public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmon)
        {
            var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
            var iid = IID_GraphicsCaptureItem;
            IntPtr p = interop.CreateForMonitor(hmon, ref iid);
            var item = GraphicsCaptureItem.FromAbi(p);
            Marshal.Release(p);
            return item;
        }

        public static ID3D11Texture2D TextureFromSurface(IDirect3DSurface surface)
        {
            var access = surface.As<IDirect3DDxgiInterfaceAccess>();
            var iid = typeof(ID3D11Texture2D).GUID;
            IntPtr p = access.GetInterface(ref iid);
            return new ID3D11Texture2D(p);
        }

        public ID3D11Texture2D CreateTexture(int w, int h, BindFlags bind, ResourceUsage usage, CpuAccessFlags cpu)
        {
            var desc = new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = usage, BindFlags = bind, CPUAccessFlags = cpu, MiscFlags = ResourceOptionFlags.None
            };
            return D3D.CreateTexture2D(desc);
        }

        public void Dispose()
        {
            Context.Dispose();
            D3D.Dispose();
        }
    }
}
