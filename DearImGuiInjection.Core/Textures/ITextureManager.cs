using Hexa.NET.ImGui;
using System;
using System.Collections.Generic;
using System.Text;

namespace DearImGuiInjection.Textures;

public interface ITextureManager
{
    public struct TextureData
    {
        public struct TextureFrameData
        {
            public ImTextureRef TextureRef;
            public int Width;
            public int Height;
            public int DelayMs;
        }
        public TextureFrameData[] Frames;
        public int FrameIndex;
        public float NextFrameInSeconds;
    }

    internal void Update();
    internal void Dispose();

    public bool TryGetTextureData(string relativePath, out TextureData textureData);

    internal bool RegisterTexture(string ownerId, string key, IntPtr ptr);
    public bool UnregisterTexture(string ownerId, string key);
    public bool TryGetTextureData(string ownerId, string key, out TextureData textureData);
}