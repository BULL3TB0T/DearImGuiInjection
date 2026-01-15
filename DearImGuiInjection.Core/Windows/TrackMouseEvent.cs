using System.Runtime.InteropServices;
using System;

namespace DearImGuiInjection.Windows;

[Flags]
internal enum TrackMouseEventFlags : uint
{
    TME_CANCEL = 0x80000000,
    TME_HOVER = 0x00000001,
    TME_LEAVE = 0x00000002,
    TME_NONCLIENT = 0x00000010,
    TME_QUERY = 0x40000000,
}

[StructLayout(LayoutKind.Sequential)]
internal struct TrackMouseEvent
{
    public int cbSize;
    [MarshalAs(UnmanagedType.U4)]
    public TrackMouseEventFlags dwFlags;
    public IntPtr hWnd;
    public uint dwHoverTime;

    public TrackMouseEvent(TrackMouseEventFlags dwFlags, IntPtr hWnd, UInt32 dwHoverTime)
    {
        this.cbSize = Marshal.SizeOf(typeof(TrackMouseEvent));
        this.dwFlags = dwFlags;
        this.hWnd = hWnd;
        this.dwHoverTime = dwHoverTime;
    }
}