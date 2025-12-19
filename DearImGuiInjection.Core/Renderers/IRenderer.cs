namespace DearImGuiInjection.Renderers;

internal interface IRenderer
{
    internal RendererKind Kind { get; }

    internal bool IsSupported();

    internal bool Init();

    internal void Dispose();
}