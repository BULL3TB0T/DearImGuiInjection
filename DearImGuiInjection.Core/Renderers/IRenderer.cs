using System;

namespace DearImGuiInjection.Renderers;

public enum RendererKind
{
    None,
    DX11
}

internal interface IRenderer : IDisposable
{
    public RendererKind Kind { get; }

    public bool IsSupported();

    public void Init();
}