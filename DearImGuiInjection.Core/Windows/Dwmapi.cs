using System;
using System.Runtime.InteropServices;

namespace DearImGuiInjection.Windows;

internal static class Dwmapi
{
    [DllImport("dwmapi.dll")]
    public static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DwmBlurBehind blurBehind);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetColorizationColor(out uint colorizationColor, out bool colorizationOpaqueBlend);

    [DllImport("dwmapi.dll")]
    public static extern int DwmIsCompositionEnabled(out bool enabled);
}
