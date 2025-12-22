using System;
using System.Runtime.InteropServices;

namespace DearImGuiInjection.Windows;

[Flags]
internal enum DwmBlurBehindFlags
{
    Enable = 1,
    BlurRegion = 2,
    TransitionMaximized = 4
}

[StructLayout(LayoutKind.Sequential)]
internal struct DwmBlurBehind
{
    public DwmBlurBehindFlags Flags;
    public bool Enable;
    public IntPtr BlurRegion;
    public bool TransitionOnMaximized;

    public DwmBlurBehind(bool enable)
    {
        Enable = enable;
        BlurRegion = IntPtr.Zero;
        TransitionOnMaximized = false;
        Flags = DwmBlurBehindFlags.Enable;
    }
}
