using System;
using System.Runtime.InteropServices;

namespace DearImGuiInjection.Windows;

internal enum MONITOR_DPI_TYPE
{
    MDT_EFFECTIVE_DPI,
    MDT_ANGULAR_DPI,
    MDT_RAW_DPI,
    MDT_DEFAULT = MDT_EFFECTIVE_DPI
}

internal enum MONITOR_FROM_FLAGS
{
    MONITOR_DEFAULTTONULL,
    MONITOR_DEFAULTTOPRIMARY,
    MONITOR_DEFAULTTONEAREST,
}

internal enum PROCESS_DPI_AWARENESS
{
    PROCESS_DPI_UNAWARE,
    PROCESS_SYSTEM_DPI_AWARE,
    PROCESS_PER_MONITOR_DPI_AWARE
}

internal static class ShCore
{
    private const string Dll = "shcore.dll";

    [DllImport(Dll)]
    public static extern uint GetDpiForMonitor(IntPtr hmonitor, MONITOR_DPI_TYPE dpiType, out uint dpiX, out uint dpiY);

    [DllImport(Dll)]
    public static extern IntPtr SetProcessDpiAwareness(PROCESS_DPI_AWARENESS value);
}
