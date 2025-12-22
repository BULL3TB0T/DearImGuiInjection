using System.Runtime.InteropServices;
using System;

namespace DearImGuiInjection.Windows;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
internal struct MonitorInfoEx
{
    private const int CCHDEVICENAME = 32;

    public int Size;
    public RECT Monitor;
    public RECT WorkArea;
    public uint Flags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
    public string DeviceName;

    public void Init()
    {
        this.Size = Marshal.SizeOf(typeof(MonitorInfoEx));
        this.DeviceName = string.Empty;
    }
}
