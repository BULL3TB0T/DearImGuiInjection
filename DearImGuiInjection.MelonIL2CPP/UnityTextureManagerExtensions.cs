using System;
using DearImGuiInjection.Textures;
using UnityEngine;

namespace DearImGuiInjection.MelonIL2CPP;

public static class UnityTextureManagerExtensions
{
    public static bool RegisterTexture(this ITextureManager textureManager, string ownerId, string key, Texture texture)
    {
        if (string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(key) || texture == null) 
            return false;
        IntPtr ptr = texture.GetNativeTexturePtr();
        if (ptr == IntPtr.Zero)
            return false;
        return textureManager.RegisterTexture(ownerId, key, ptr);
    }

    public static bool RegisterTexture(this ITextureManager textureManager, string ownerId, string key, Texture2D texture)
        => RegisterTexture(textureManager, ownerId, key, (Texture)texture);

    public static bool RegisterTexture(this ITextureManager textureManager, string ownerId, string key, RenderTexture texture)
        => RegisterTexture(textureManager, ownerId, key, (Texture)texture);
}
