namespace DearImGuiInjection.Renderers;

public enum RendererKind
{
    None,
    DX11
}

internal interface IRenderer
{
    public RendererKind Kind { get; }

    public bool IsSupported();

    public void Init();

    public void Dispose();
}