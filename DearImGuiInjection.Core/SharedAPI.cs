using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.OpenGL;
using Silk.NET.Vulkan;

namespace DearImGuiInjection;

internal static class SharedAPI
{
    public static D3D11 D3D11;
    public static D3D12 D3D12;
    public static D3DCompiler D3DCompiler;
    public static DXGI DXGI;
    public static Vk Vulkan;
    public static GL GL;
}