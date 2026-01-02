using System;
using System.Runtime.InteropServices;
using System.Security;

namespace DearImGuiInjection.Windows;

internal static class Kernel32
{
    private const string Dll = "kernel32.dll";

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandle([MarshalAs(UnmanagedType.LPWStr)] string lpModuleName);

    [DllImport(Dll, CharSet = CharSet.Ansi, SetLastError = true)]
    public static extern IntPtr LoadLibrary([MarshalAs(UnmanagedType.LPStr)] string lpFileName);

    [DllImport(Dll, SetLastError = true)]
    public static extern bool FreeLibrary(IntPtr hModule);

    [DllImport(Dll, CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport(Dll, SetLastError = true)]
    public static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

    [DllImport(Dll, SetLastError = true)]
    public static extern bool QueryPerformanceFrequency(out long frequency);

    [DllImport(Dll, SetLastError = true)]
    public static extern uint GetLocaleInfoA(uint Locale, uint LCType, IntPtr lpLCData, int cchData);

    [DllImport(Dll, SetLastError = true)]
    public static unsafe extern int MultiByteToWideChar(
        uint codePage,
        uint dwFlags,
        byte* lpMultiByteStr,
        int cbMultiByte,
        char* lpWideCharStr,
        int cchWideChar);

    [SecurityCritical, SuppressUnmanagedCodeSecurity]
    [DllImport(Dll, CallingConvention = CallingConvention.Winapi)]
    private static extern ulong VerSetConditionMask(ulong dwlConditionMask, uint dwTypeBitMask, byte dwConditionMask);

    [SecuritySafeCritical]
    public static void VER_SET_CONDITION(ref ulong dwlConditionMask, uint dwTypeBitMask, byte dwConditionMask)
        => dwlConditionMask = VerSetConditionMask(dwlConditionMask, dwTypeBitMask, dwConditionMask);
}