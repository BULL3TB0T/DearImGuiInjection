using Hexa.NET.ImGui;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.WIC;
using System;
using System.Runtime.InteropServices;

using Device = SharpDX.Direct3D11.Device;

namespace DearImGuiInjection.Textures;

internal class DX11TextureManager : TextureManager<DX11TextureManager.Entry>
{
    public class Entry
    {
        public class Frame
        {
            public Texture2D Tex;
            public ShaderResourceView Srv;
            public int Width;
            public int Height;
            public int DelayMs;
        }

        public Texture2D StaticTex;
        public ShaderResourceView StaticSrv;
        public int StaticWidth;
        public int StaticHeight;

        public Frame[] Frames;
        public int FrameIndex;
        public double NextSwitchTimeSeconds;
    }

    private readonly Device _device;

    public DX11TextureManager(Device device) => _device = device;

    public override void UpdateFrames(double nowSeconds)
    {
        foreach (var pair in Entries)
            UpdateEntryFrame(nowSeconds, pair.Value);
        foreach (var pair in RegisteredEntries)
            UpdateEntryFrame(nowSeconds, pair.Value);
    }

    public override bool TryCreateEntryFromNative(IntPtr ptr, out Entry entry)
    {
        entry = null;
        try
        {
            var texture = new Texture2D(ptr);
            var description = texture.Description;
            var shaderResourceView = new ShaderResourceView(_device, texture);
            entry = new Entry
            {
                StaticTex = texture,
                StaticSrv = shaderResourceView,
                StaticWidth = description.Width,
                StaticHeight = description.Height
            };

            return true;
        }
        catch
        {
            return false;
        }
    }

    public override bool TryLoadEntry(string fullPath, DecodedFrame[] frames, out Entry entry)
    {
        entry = null;

        if (frames.Length == 1)
        {
            if (!TryCreateTexture(frames[0].Rgba, frames[0].Width, frames[0].Height, out var texture, out var shaderResourceView))
                return false;
            entry = new Entry
            {
                StaticTex = texture,
                StaticSrv = shaderResourceView,
                StaticWidth = frames[0].Width,
                StaticHeight = frames[0].Height
            };
            return true;
        }
        var entryFrames = new Entry.Frame[frames.Length];
        try
        {
            for (int i = 0; i < frames.Length; i++)
            {
                var decodedFrame = frames[i];
                if (!TryCreateTexture(decodedFrame.Rgba, decodedFrame.Width, decodedFrame.Height, out var texture, out var shaderResourceView))
                    throw new Exception("Failed to create DX11 texture for a GIF frame.");
                entryFrames[i] = new Entry.Frame
                {
                    Tex = texture,
                    Srv = shaderResourceView,
                    Width = decodedFrame.Width,
                    Height = decodedFrame.Height,
                    DelayMs = decodedFrame.DelayMs
                };
            }
            entry = new Entry
            {
                Frames = entryFrames
            };
            return true;
        }
        catch
        {
            for (int i = 0; i < entryFrames.Length; i++)
            {
                entryFrames[i]?.Tex?.Dispose();
                entryFrames[i]?.Srv?.Dispose();
            }
            return false;
        }
    }

    public override void DisposeEntry(Entry entry)
    {
        entry.StaticSrv?.Dispose();
        entry.StaticSrv = null;
        entry.StaticTex?.Dispose();
        entry.StaticTex = null;
        if (entry.Frames != null)
        {
            for (int i = 0; i < entry.Frames.Length; i++)
            {
                var frame = entry.Frames[i];
                frame?.Srv?.Dispose();
                frame?.Tex?.Dispose();
            }
            entry.Frames = null;
        }
    }

    public override ImTextureID GetTextureId(Entry entry, int frame = -1)
    {
        if (entry.Frames != null)
        {
            int index = frame >= 0 ? frame : entry.FrameIndex;
            if ((uint)index >= (uint)entry.Frames.Length)
                index = 0;
            return entry.Frames[index]?.Srv?.NativePointer ?? IntPtr.Zero;
        }
        return entry.StaticSrv?.NativePointer ?? IntPtr.Zero;
    }

    public override void GetTextureSize(Entry entry, out int width, out int height)
    {
        if (entry.Frames != null)
        {
            int index = entry.FrameIndex;
            if ((uint)index >= (uint)entry.Frames.Length)
                index = 0;
            var frame = entry.Frames[index];
            width = frame?.Width ?? 0;
            height = frame?.Height ?? 0;
            return;
        }
        width = entry.StaticWidth;
        height = entry.StaticHeight;
    }

    private bool TryCreateTexture(byte[] rgba, int width, int height, out Texture2D tex, out ShaderResourceView srv)
    {
        tex = null;
        srv = null;
        if (rgba == null || rgba.Length == 0 || width <= 0 || height <= 0)
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
            tex = new Texture2D(_device, description, rect);
            srv = new ShaderResourceView(_device, tex);
            return true;
        }
        catch
        {
            srv?.Dispose();
            tex?.Dispose();
            tex = null;
            srv = null;
            return false;
        }
        finally
        {
            if (dataPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(dataPtr);
        }
    }

    private void UpdateEntryFrame(double nowSeconds, Entry entry)
    {
        if (entry.Frames == null)
            return;
        if (entry.NextSwitchTimeSeconds <= 0)
        {
            int firstDelayMs = entry.Frames[0].DelayMs;
            if (firstDelayMs <= 0)
                firstDelayMs = 100;
            entry.FrameIndex = 0;
            entry.NextSwitchTimeSeconds = nowSeconds + (firstDelayMs / 1000.0);
            return;
        }
        if (nowSeconds < entry.NextSwitchTimeSeconds)
            return;
        int nextIndex = entry.FrameIndex + 1;
        if (nextIndex >= entry.Frames.Length)
            nextIndex = 0;
        entry.FrameIndex = nextIndex;
        int delayMs = entry.Frames[nextIndex].DelayMs;
        if (delayMs <= 0)
            delayMs = 100;
        entry.NextSwitchTimeSeconds = nowSeconds + (delayMs / 1000.0);
    }
}