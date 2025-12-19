using DearImGuiInjection.Backends;
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;

namespace DearImGuiInjection.Renderers;

internal static class RendererManager
{
    private static IRenderer _activeRenderer;

    public static RendererKind Kind { get; private set; } = _activeRenderer != null ? _activeRenderer.Kind : RendererKind.None;

    public static bool Init()
    {
        if (_activeRenderer != null)
            return true;
        foreach (RendererKind candidate in Enum.GetValues(typeof(RendererKind)))
        {
            if (candidate == RendererKind.None)
                continue;
            IRenderer renderer = candidate switch
            {
                RendererKind.D3D11 => new D3D11Renderer(),
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
            _activeRenderer = renderer;
            try
            {
                Log.Info($"Renderer {renderer.Kind} Init()");
                renderer.Init();
            }
            catch (Exception ex)
            {
                Log.Error($"Renderer {renderer.Kind} Init() failed: {ex}");
                renderer.Dispose();
                continue;
            }
            return true;
        }
        _activeRenderer = null;
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
