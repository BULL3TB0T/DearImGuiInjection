using System.Runtime.InteropServices;

namespace DearImGuiInjection.Windows;

[StructLayout(LayoutKind.Sequential)]
internal struct XINPUT_VIBRATION
{
    public ushort wLeftMotorSpeed;
    public ushort wRightMotorSpeed;
}
