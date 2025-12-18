using System;
using System.Runtime.InteropServices;
using System.Security;

namespace DearImGuiInjection.Windows;

internal static class Kernel32
{
    public const uint CP_ACP = 0;
    public const uint CP_BIG5 = 950;
    public const uint CP_GB2312 = 936;
    public const uint CP_OEMCP = 1;
    public const uint CP_SHIFTJIS = 932;
    public const uint CP_SYMBOL = 42;
    public const uint CP_UTF7 = 65000;
    public const uint CP_UTF8 = 65001;
    public const uint MB_PRECOMPOSED = 0x00000001;

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
    public static unsafe extern int MultiByteToWideChar(
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
    internal static void VER_SET_CONDITION(ref ulong dwlConditionMask, uint dwTypeBitMask, byte dwConditionMask)
        => dwlConditionMask = VerSetConditionMask(dwlConditionMask, dwTypeBitMask, dwConditionMask);

    public static ushort HiByte(ushort wValue) => (ushort)((wValue >> 8) & 0xFF);
}