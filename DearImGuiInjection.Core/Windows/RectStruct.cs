using System.Runtime.InteropServices;

namespace DearImGuiInjection.Windows;

[StructLayout(LayoutKind.Sequential)]
    internal struct RectStruct
    {
        public int Left;

        public int Top;

        public int Right;

        public int Bottom;
    }
