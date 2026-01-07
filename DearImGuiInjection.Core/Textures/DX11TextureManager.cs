using Hexa.NET.ImGui;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System;
using System.Runtime.InteropServices;

namespace DearImGuiInjection.Textures;

internal sealed unsafe class DX11TextureManager : TextureManager<DX11TextureManager.EntryData, DX11TextureManager.EntryFrameData>
{
    internal struct EntryData : IEntryData
    {
        public EntryFrameData[] FrameDatas { get; set; }
        public int FrameIndex { get; set; }
        public float NextFrameInSeconds { get; set; }
        public ITextureManager.TextureData CachedTextureData { get; set; }
        public ITextureManager.TextureData.TextureFrameData[] CachedTextureFrameDatas { get; set; }
    }

    internal struct EntryFrameData : IEntryFrameData
    {
        public ID3D11ShaderResourceView* Srv;
        public int Width { get; set; }
        public int Height { get; set; }
        public int DelayMs { get; set; }
    }

    private readonly ID3D11Device* _device;

    public DX11TextureManager(ID3D11Device* device)
    {
        _device = device;
        _device->AddRef();
    }

    public override void OnDispose()
    {
        if (_device != null)
            _device->Release();
    }

    public override void DisposeEntryData(EntryData entryData)
    {
        EntryFrameData[] frames = entryData.FrameDatas;
        for (int i = 0; i < frames.Length; i++)
        {
            EntryFrameData frameData = frames[i];
            if (frameData.Srv != null)
                frameData.Srv->Release();
        }
    }

    public override bool TryCreateEntryData(IntPtr ptr, out EntryData entry)
    {
        entry = default;
        ID3D11Texture2D* texture = (ID3D11Texture2D*)ptr;
        if (texture == null)
            return false;
        Texture2DDesc desc;
        texture->GetDesc(&desc);
        ShaderResourceViewDesc srvDesc = default;
        srvDesc.Format = Format.FormatR8G8B8A8Unorm;
        srvDesc.ViewDimension = D3DSrvDimension.D3D11SrvDimensionTexture2D;
        srvDesc.Texture2D = new Tex2DSrv
        {
            MipLevels = desc.MipLevels,
            MostDetailedMip = 0
        };
        ID3D11ShaderResourceView* srv = null;
        if (_device->CreateShaderResourceView((ID3D11Resource*)texture, &srvDesc, &srv) < 0)
        {
            texture->Release();
            return false;
        }
        texture->Release();
        entry = new EntryData
        {
            FrameDatas = new[]
            {
                new EntryFrameData
                {
                    Srv = srv,
                    Width = (int)desc.Width,
                    Height = (int)desc.Height
                }
            }
        };
        return true;
    }

    public override bool TryCreateEntryDatas(DecodedFrame[] frames, out EntryData entry)
    {
        entry = default;
        var entryFrames = new EntryFrameData[frames.Length];
        try
        {
            for (int i = 0; i < frames.Length; i++)
            {
                var decodedFrame = frames[i];
                if (!TryCreateTexture(decodedFrame.Pixels, decodedFrame.Width, decodedFrame.Height, 
                    out ID3D11ShaderResourceView* srv))
                    throw new Exception("Failed to create texture.");
                entryFrames[i] = new EntryFrameData
                {
                    Srv = srv,
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
                if (entryFrames[i].Srv != null)
                    entryFrames[i].Srv->Release();
            }
            return false;
        }
    }

    public override ITextureManager.TextureData GetTextureData(ref EntryData entryData)
    {
        if (entryData.CachedTextureFrameDatas == null)
        {
            int frameCount = entryData.FrameDatas.Length;
            entryData.CachedTextureFrameDatas = new ITextureManager.TextureData.TextureFrameData[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                EntryFrameData frameData = entryData.FrameDatas[i];
                entryData.CachedTextureFrameDatas[i] = new ITextureManager.TextureData.TextureFrameData
                {
                    TextureRef = new ImTextureRef
                    {
                        TexID = frameData.Srv
                    },
                    Width = frameData.Width,
                    Height = frameData.Height,
                    DelayMs = frameData.DelayMs
                };
            }
            entryData.CachedTextureData = new ITextureManager.TextureData
            {
                Frames = entryData.CachedTextureFrameDatas
            };
        }
        return entryData.CachedTextureData;
    }

    private bool TryCreateTexture(byte[] pixels, int width, int height, out ID3D11ShaderResourceView* srv)
    {
        srv = null;
        Texture2DDesc desc = new()
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.FormatR8G8B8A8Unorm,
            SampleDesc = new SampleDesc(1),
            Usage = Usage.Default,
            BindFlags = (uint)BindFlag.ShaderResource,
            CPUAccessFlags = 0,
            MiscFlags = 0
        };
        SubresourceData subResource;
        fixed (byte* image_data = pixels)
        {
            subResource = new()
            {
                PSysMem = image_data,
                SysMemPitch = (uint)width * 4,
                SysMemSlicePitch = 0
            };
        }
        ID3D11Texture2D* texture = null;
        if (_device->CreateTexture2D(&desc, &subResource, &texture) < 0)
            return false;
        ShaderResourceViewDesc srvDesc = new()
        {
            Format = Format.FormatR8G8B8A8Unorm,
            ViewDimension = D3DSrvDimension.D3D11SrvDimensionTexture2D,
            Texture2D = new Tex2DSrv
            {
                MipLevels = desc.MipLevels,
                MostDetailedMip = 0
            }
        };
        ID3D11ShaderResourceView* out_srv = null;
        if (_device->CreateShaderResourceView((ID3D11Resource*)texture, &srvDesc, &out_srv) < 0)
        {
            texture->Release();
            return false;
        }
        texture->Release();
        srv = out_srv;
        return true;
    }
}