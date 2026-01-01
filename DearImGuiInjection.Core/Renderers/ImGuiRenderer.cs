using DearImGuiInjection.Handlers;
using System;

namespace DearImGuiInjection.Renderers;

public enum RendererKind
{
    None,
    DX11
}

internal abstract class ImGuiRenderer
{
    public abstract RendererKind Kind { get; }

    public abstract bool IsSupported();

    public ImGuiHandler Handler;

    public abstract void Init();
    public abstract void Dispose();
}