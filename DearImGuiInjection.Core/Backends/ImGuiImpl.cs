using Hexa.NET.ImGui;
using System.Runtime.InteropServices;

namespace DearImGuiInjection.Backends;

internal static class ImGuiImpl
{
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct VERTEX_CONSTANT_BUFFER
    {
        public const int ElementCount = 4 * 4;
        public const int ByteWidth = ElementCount * sizeof(float);
        public fixed float mvp[ElementCount];
    }
}