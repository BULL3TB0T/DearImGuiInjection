using System;
using System.Runtime.InteropServices;

namespace DearImGuiInjection.Windows;

internal static class Gdi32
{
    private const string Dll = "gdi32.dll";

    [DllImport(Dll)]
    public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport(Dll, EntryPoint = "DeleteObject")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport(Dll)]
    public static extern int GetDeviceCaps(IntPtr hdc, int index);
}
