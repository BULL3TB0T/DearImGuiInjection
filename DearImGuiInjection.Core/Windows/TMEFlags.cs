using System;

namespace DearImGuiInjection.Windows;

[Flags]
    public enum TMEFlags : uint
    {
        TME_CANCEL = 0x80000000,
        TME_HOVER = 0x00000001,
        TME_LEAVE = 0x00000002,
        TME_NONCLIENT = 0x00000010,
        TME_QUERY = 0x40000000,
    }
