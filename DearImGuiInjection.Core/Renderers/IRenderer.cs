namespace DearImGuiInjection.Renderers;

public enum RendererKind
{
    None,
    D3D11
}

public interface IRenderer
{
    public bool Init();

    public void Dispose();
}