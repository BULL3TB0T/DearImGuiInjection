using DearImGuiInjection.Backends;
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;

namespace DearImGuiInjection.Renderers;

internal static class RendererManager
{
    public static RendererKind Kind => _activeRenderer != null ? _activeRenderer.Kind : RendererKind.None;

    private static IRenderer _activeRenderer;

    public static bool Init()
    {
        if (_activeRenderer != null)
            return false;
        foreach (RendererKind kind in Enum.GetValues(typeof(RendererKind)))
        {
            if (kind == RendererKind.None)
                continue;
            IRenderer renderer = kind switch
            {
                RendererKind.DX11 => new DX11Renderer(),
                _ => null
            };
            if (renderer == null)
                continue;
            bool isSupported = false;
            try
            {
                isSupported = renderer.IsSupported();
            }
            catch (Exception e)
            {
                Log.Error($"Renderer {renderer.Kind} IsSupported() failed: {e}");
                isSupported = false;
            }
            if (!isSupported)
                continue;
            try
            {
                Log.Info($"Renderer {renderer.Kind} Init()");
                renderer.Init();
                _activeRenderer = renderer;
                return true;
            }
            catch (Exception e)
            {
                Log.Error($"Renderer {renderer.Kind} Init() failed: {e}");
                renderer.Dispose();
                return false;
            }
        }
        return false;
    }

    public static void Shutdown()
    {
        if (_activeRenderer == null)
            return;
        _activeRenderer.Dispose();
        _activeRenderer = null;
    }
}
