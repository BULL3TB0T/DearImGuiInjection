using System;
using System.Runtime.InteropServices;

namespace DearImGuiInjection.Windows;

[Flags]
public enum PRODUCT_SUITE : short
{
    VER_SUITE_BACKOFFICE = 0x00000004,
    VER_SUITE_BLADE = 0x00000400,
    VER_SUITE_COMPUTE_SERVER = 0x00004000,
    VER_SUITE_DATACENTER = 0x00000080,
    VER_SUITE_ENTERPRISE = 0x00000002,
    VER_SUITE_EMBEDDEDNT = 0x00000040,
    VER_SUITE_PERSONAL = 0x00000200,
    VER_SUITE_SINGLEUSERTS = 0x00000100,
    VER_SUITE_SMALLBUSINESS = 0x00000001,
    VER_SUITE_SMALLBUSINESS_RESTRICTED = 0x00000020,
    VER_SUITE_STORAGE_SERVER = 0x00002000,
    VER_SUITE_TERMINAL = 0x00000010,
    VER_SUITE_WH_SERVER = unchecked((short)0x00008000),
}

public enum OS_TYPE : byte
{
    VER_NT_WORKSTATION = 0x00000001,
    VER_NT_DOMAIN_CONTROLLER = 0x00000002,
    VER_NT_SERVER = 0x00000003,
}

[Flags]
public enum VER_MASK : int
{
    VER_MINORVERSION = 0x0000001,
    VER_MAJORVERSION = 0x0000002,
    VER_BUILDNUMBER = 0x0000004,
    VER_PLATFORMID = 0x0000008,
    VER_SERVICEPACKMINOR = 0x0000010,
    VER_SERVICEPACKMAJOR = 0x0000020,
    VER_SUITENAME = 0x0000040,
    VER_PRODUCT_TYPE = 0x0000080,
}

public unsafe partial struct OSVERSIONINFOEX
{
    public int dwOSVersionInfoSize;
    public int dwMajorVersion;
    public int dwMinorVersion;
    public int dwBuildNumber;
    public int dwPlatformId;
    public fixed char szCSDVersion[128];
    public short wServicePackMajor;
    public short wServicePackMinor;
    public PRODUCT_SUITE wSuiteMask;
    public OS_TYPE wProductType;
    public byte wReserved;

    public static OSVERSIONINFOEX Create() => new() { dwOSVersionInfoSize = sizeof(OSVERSIONINFOEX) };
}

internal static class Ntdll
{
    [DllImport("ntdll.dll")]
    public static unsafe extern NtStatus RtlVerifyVersionInfo(OSVERSIONINFOEX* VersionInfo, VER_MASK TypeMask, long ConditionMask);
}
