using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.WIC;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

using Device = SharpDX.Direct3D11.Device;

namespace DearImGuiInjection.Textures;

internal class DX11TextureManager : TextureManager<DX11TextureManager.Entry>
{
    public class Entry
    {
        public ShaderResourceView Srv;
        public int Width;
        public int Height;
    }

    private readonly Device _device;

    public DX11TextureManager(Device device) => _device = device;

    public override bool TryLoadEntry(string fullPath, out Entry entry)
    {
        entry = null;
        if (!TryDecodeRgba32Wic(fullPath, out byte[] rgba, out int width, out int height))
            return false;
        var description = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None
        };
        IntPtr dataPtr = IntPtr.Zero;
        try
        {
            dataPtr = Marshal.AllocHGlobal(rgba.Length);
            Marshal.Copy(rgba, 0, dataPtr, rgba.Length);
            var rect = new DataRectangle(dataPtr, width * 4);
            var texture = new Texture2D(_device, description, new[] { rect });
            var srv = new ShaderResourceView(_device, texture);
            entry = new Entry
            {
                Srv = srv,
                Width = width,
                Height = height
            };
            return true;
        }
        catch
        {
            entry = null;
            return false;
        }
        finally
        {
            if (dataPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(dataPtr);
        }
    }

    public override void DisposeEntry(Entry entry)
    {
        if (entry == null)
            return;
        entry.Srv?.Dispose();
        entry.Srv = null;
    }

    public override IntPtr GetTextureId(Entry entry) => entry?.Srv?.NativePointer ?? IntPtr.Zero;

    public override void GetTextureSize(Entry entry, out int width, out int height)
    {
        width = entry?.Width ?? 0;
        height = entry?.Height ?? 0;
    }

    private static bool TryDecodeRgba32Wic(string filename, out byte[] rgba, out int width, out int height)
    {
        rgba = null;
        width = 0;
        height = 0;
        try
        {
            using var imagingFactory = new ImagingFactory2();
            using var decoder = new BitmapDecoder(imagingFactory, filename, DecodeOptions.CacheOnDemand);
            using var frame = decoder.GetFrame(0);
            width = frame.Size.Width;
            height = frame.Size.Height;
            if (width <= 0 || height <= 0)
                return false;
            using var converter = new FormatConverter(imagingFactory);
            converter.Initialize(frame, PixelFormat.Format32bppRGBA);
            rgba = new byte[width * height * 4];
            converter.CopyPixels(rgba, width * 4);
            return true;
        }
        catch
        {
            rgba = null;
            width = 0;
            height = 0;
            return false;
        }
    }
}