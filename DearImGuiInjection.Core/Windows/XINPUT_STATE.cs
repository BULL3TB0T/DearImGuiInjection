using System.Runtime.InteropServices;

namespace DearImGuiInjection.Windows;

internal delegate uint XInputGetStateDelegate(uint dwUserIndex, out XINPUT_STATE state);

[StructLayout(LayoutKind.Sequential)]
internal struct XINPUT_STATE
{
    public uint dwPacketNumber;
    public XINPUT_GAMEPAD Gamepad;
}
