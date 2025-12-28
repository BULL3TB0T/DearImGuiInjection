using Hexa.NET.ImGui;
using System;
using System.Collections.Generic;
using System.Text;

namespace DearImGuiInjection.Textures;

public interface ITextureManager : IDisposable
{
    internal void Update();

    public bool TryGetTextureRef(string relativePath, out ImTextureRef textureRef);
    public bool TryGetTextureSize(string relativePath, out int width, out int height);
}