using System.Runtime.InteropServices;
using System;

namespace DearImGuiInjection.Windows;

[Flags]
internal enum TMEFlags : uint
{
    TME_CANCEL = 0x80000000,
    TME_HOVER = 0x00000001,
    TME_LEAVE = 0x00000002,
    TME_NONCLIENT = 0x00000010,
    TME_QUERY = 0x40000000,
}

[StructLayout(LayoutKind.Sequential)]
internal struct TRACKMOUSEEVENT
{
    public Int32 cbSize;    // using Int32 instead of UInt32 is safe here, and this avoids casting the result  of Marshal.SizeOf()
    [MarshalAs(UnmanagedType.U4)]
    public TMEFlags dwFlags;
    public IntPtr hWnd;
    public UInt32 dwHoverTime;

    public TRACKMOUSEEVENT(TMEFlags dwFlags, IntPtr hWnd, UInt32 dwHoverTime)
    {
        this.cbSize = Marshal.SizeOf(typeof(TRACKMOUSEEVENT));
        this.dwFlags = dwFlags;
        this.hWnd = hWnd;
        this.dwHoverTime = dwHoverTime;
    }
}