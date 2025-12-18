using System;

namespace DearImGuiInjection.Windows;

[Flags]
public enum DwmBlurBehindFlags
{
    Enable = 1,
    BlurRegion = 2,
    TransitionMaximized = 4
}
