using Hexa.NET.ImGui;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.WIC;
using System;
using System.Runtime.InteropServices;

using Device = SharpDX.Direct3D11.Device;

namespace DearImGuiInjection.Textures;

internal class DX11TextureManager : TextureManager<DX11TextureManager.EntryData>
{
    public struct EntryData
    {
        public struct FrameData
        {
            public Texture2D Tex;
            public ShaderResourceView Srv;
            public int Width;
            public int Height;
            public int DelayMs;
        }
        public FrameData[] FrameDatas;
        public int FrameIndex;
        public float NextFrameInSeconds;
        internal ITextureManager.TextureData CachedTextureData;
        internal ITextureManager.TextureData.TextureFrameData[] CachedFrameDatas;
    }

    private Device _device;

    public DX11TextureManager(Device device) => _device = device;

    public override void UpdateEntryData(ref EntryData entryData)
    {
        float nowSeconds = NowSeconds;
        int frameCount = entryData.FrameDatas.Length;
        if (entryData.NextFrameInSeconds <= 0)
        {
            int firstDelayMs = entryData.FrameDatas[0].DelayMs;
            if (frameCount > 1 && firstDelayMs <= 0)
                firstDelayMs = 100;
            entryData.FrameIndex = 0;
            entryData.NextFrameInSeconds = nowSeconds + (firstDelayMs / 1000.0f);
            return;
        }
        if (nowSeconds < entryData.NextFrameInSeconds)
            return;
        int nextIndex = entryData.FrameIndex + 1;
        if (nextIndex >= frameCount)
            nextIndex = 0;
        entryData.FrameIndex = nextIndex;
        int delayMs = entryData.FrameDatas[nextIndex].DelayMs;
        if (frameCount > 1 && delayMs <= 0)
            delayMs = 100;
        entryData.NextFrameInSeconds = nowSeconds + (delayMs / 1000.0f);
    }

    public override void DisposeEntryData(EntryData entryData)
    {
        for (int i = 0; i < entryData.FrameDatas.Length; i++)
        {
            var frame = entryData.FrameDatas[i];
            frame.Tex?.Dispose();
            frame.Srv?.Dispose();
        }
    }

    public override bool TryCreateEntryData(string fullPath, DecodedFrame[] frames, out EntryData entry)
    {
        entry = default;
        var entryFrames = new EntryData.FrameData[frames.Length];
        try
        {
            for (int i = 0; i < frames.Length; i++)
            {
                var decodedFrame = frames[i];
                if (!TryCreateTexture(decodedFrame.Rgba, decodedFrame.Width, decodedFrame.Height, out var texture, out var shaderResourceView))
                    throw new Exception("Failed to create DX11 texture for a GIF frame.");
                entryFrames[i] = new EntryData.FrameData
                {
                    Tex = texture,
                    Srv = shaderResourceView,
                    Width = decodedFrame.Width,
                    Height = decodedFrame.Height,
                    DelayMs = decodedFrame.DelayMs
                };
            }
            entry = new EntryData
            {
                FrameDatas = entryFrames
            };
            return true;
        }
        catch
        {
            for (int i = 0; i < entryFrames.Length; i++)
            {
                entryFrames[i].Tex?.Dispose();
                entryFrames[i].Srv?.Dispose();
            }
            return false;
        }
    }

    public override bool TryCreateEntryData(IntPtr ptr, out EntryData entry)
    {
        entry = default;
        Texture2D texture = null;
        ShaderResourceView shaderResourceView = null;
        try
        {
            texture = new Texture2D(ptr);
            shaderResourceView = new ShaderResourceView(_device, texture);
            var description = texture.Description;
            entry = new EntryData
            {
                FrameDatas = new[]
                {
                    new EntryData.FrameData()
                    {
                        Tex = texture,
                        Srv = shaderResourceView,
                        Width = description.Width,
                        Height = description.Height
                    }
                }
            };
            return true;
        }
        catch
        {
            texture?.Dispose();
            shaderResourceView?.Dispose();
            return false;
        }
    }

    public override ITextureManager.TextureData GetTextureData(EntryData entryData)
    {
        if (entryData.CachedFrameDatas == null)
        {
            int frameCount = entryData.FrameDatas.Length;
            entryData.CachedFrameDatas = new ITextureManager.TextureData.TextureFrameData[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                EntryData.FrameData frameData = entryData.FrameDatas[i];
                entryData.CachedFrameDatas[i] = new ITextureManager.TextureData.TextureFrameData
                {
                    TextureRef = new ImTextureRef
                    {
                        TexID = frameData.Srv.NativePointer
                    },
                    Width = frameData.Width,
                    Height = frameData.Height,
                    DelayMs = frameData.DelayMs
                };
            }
            entryData.CachedTextureData = new ITextureManager.TextureData
            {
                Frames = entryData.CachedFrameDatas
            };
        }
        entryData.CachedTextureData.FrameIndex = entryData.FrameIndex;
        entryData.CachedTextureData.NextFrameInSeconds = entryData.NextFrameInSeconds;
        return entryData.CachedTextureData;
    }

    private bool TryCreateTexture(byte[] rgba, int width, int height, out Texture2D texture, 
        out ShaderResourceView shaderResourceView)
    {
        texture = null;
        shaderResourceView = null;
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
            texture = new Texture2D(_device, description, rect);
            shaderResourceView = new ShaderResourceView(_device, texture);
            return true;
        }
        catch
        {
            texture?.Dispose();
            texture = null;
            shaderResourceView?.Dispose();
            shaderResourceView = null;
            return false;
        }
        finally
        {
            if (dataPtr != IntPtr.Zero)
                Marshal.FreeHGlobal(dataPtr);
        }
    }
}