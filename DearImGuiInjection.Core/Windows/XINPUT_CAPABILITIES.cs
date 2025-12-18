using System.Runtime.InteropServices;

namespace DearImGuiInjection.Windows;

internal delegate uint XInputGetCapabilitiesDelegate(uint dwUserIndex, uint dwFlags, out XINPUT_CAPABILITIES capabilities);

[StructLayout(LayoutKind.Sequential)]
internal struct XINPUT_CAPABILITIES
{
    public byte Type;
    public byte SubType;
    public ushort Flags;
    public XINPUT_GAMEPAD Gamepad;
    public XINPUT_VIBRATION Vibration;
}
