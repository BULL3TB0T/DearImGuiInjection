using Hexa.NET.ImGui;
using System;
using System.Collections.Generic;
using System.Text;

namespace DearImGuiInjection.Textures;

public interface ITextureManager : IDisposable
{
    internal void Update();

    public bool TryGetTextureRef(string relativePath, out ImTextureRef textureRef);
    public bool TryGetTextureRefForFrame(string relativePath, int frame, out ImTextureRef textureRef);
    public bool TryGetTextureSize(string relativePath, out int width, out int height);

    internal bool RegisterTexture(string ownerId, string key, IntPtr ptr);
    public bool UnregisterTexture(string ownerId, string key);
    public bool TryGetRegisteredTextureRef(string ownerId, string key, out ImTextureRef textureRef);
    public bool TryGetRegisteredTextureRefForFrame(string ownerId, string key, int frame, out ImTextureRef textureRef);
    public bool TryGetRegisteredTextureSize(string ownerId, string key, out int width, out int height);
}