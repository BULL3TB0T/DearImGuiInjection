using System;
using System.Runtime.InteropServices;
using System.Security;

namespace DearImGuiInjection.Windows;

internal static class Kernel32
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr LoadLibrary([MarshalAs(UnmanagedType.LPStr)] string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool QueryPerformanceFrequency(out long frequency);

    [DllImport("kernel32.dll", SetLastError = true)]
    public unsafe static extern int MultiByteToWideChar(
        uint codePage,
        uint dwFlags,
        [In][MarshalAs(UnmanagedType.LPArray)] byte[] lpMultiByteStr,
        int cbMultiByte,
        IntPtr lpWideCharStr,
        int cchWideChar);

    [SecurityCritical, SuppressUnmanagedCodeSecurity]
    [DllImport("kernel32.dll", CallingConvention = CallingConvention.Winapi)]
    private static extern ulong VerSetConditionMask(ulong dwlConditionMask, uint dwTypeBitMask, byte dwConditionMask);

    [SecuritySafeCritical]
    public static void VER_SET_CONDITION(ref ulong dwlConditionMask, uint dwTypeBitMask, byte dwConditionMask)
        => dwlConditionMask = VerSetConditionMask(dwlConditionMask, dwTypeBitMask, dwConditionMask);

    public static ushort HiByte(ushort wValue) => (ushort)((wValue >> 8) & 0xFF);
}