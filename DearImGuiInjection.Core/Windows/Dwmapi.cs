using System;
using System.Runtime.InteropServices;

namespace DearImGuiInjection.Windows;

internal static class Dwmapi
{
    private const string Dll = "dwmapi.dll";

    [DllImport(Dll)]
    public static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DwmBlurBehind blurBehind);

    [DllImport(Dll)]
    public static extern int DwmGetColorizationColor(out uint colorizationColor, out bool colorizationOpaqueBlend);

    [DllImport(Dll)]
    public static extern int DwmIsCompositionEnabled(out bool enabled);
}
