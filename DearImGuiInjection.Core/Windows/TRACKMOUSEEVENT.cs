using System.Runtime.InteropServices;
using System;

namespace DearImGuiInjection.Windows;

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
