using System;
using System.Runtime.InteropServices;

namespace DearImGuiInjection.Windows;

internal enum MH_STATUS
{
    // Unknown error. Should not be returned.
    UNKNOWN = -1,

    // Successful.
    OK,

    // MinHook is already initialized.
    ERROR_ALREADY_INITIALIZED,

    // MinHook is not initialized yet, or already uninitialized.
    ERROR_NOT_INITIALIZED,

    // The hook for the specified target function is already created.
    ERROR_ALREADY_CREATED,

    // The hook for the specified target function is not created yet.
    ERROR_NOT_CREATED,

    // The hook for the specified target function is already enabled.
    ERROR_ENABLED,

    // The hook for the specified target function is not enabled yet, or already
    // disabled.
    ERROR_DISABLED,

    // The specified pointer is invalid. It points the address of non-allocated
    // and/or non-executable region.
    ERROR_NOT_EXECUTABLE,

    // The specified target function cannot be hooked.
    ERROR_UNSUPPORTED_FUNCTION,

    // Failed to allocate memory.
    ERROR_MEMORY_ALLOC,

    // Failed to change the memory protection.
    ERROR_MEMORY_PROTECT,

    // The specified module is not loaded.
    ERROR_MODULE_NOT_FOUND,

    // The specified function is not found.
    ERROR_FUNCTION_NOT_FOUND
}

internal static class MinHook
{
    private const string Dll86 = "MinHook-x86.dll";
    private const string Dll64 = "MinHook-x64.dll";

    private static class MinHook86
    {
        [DllImport(Dll86, CallingConvention = CallingConvention.Cdecl)]
        public static extern MH_STATUS MH_Initialize();

        [DllImport(Dll86, CallingConvention = CallingConvention.Cdecl)]
        public static extern MH_STATUS MH_Uninitialize();

        [DllImport(Dll86, CallingConvention = CallingConvention.Cdecl)]
        public static extern MH_STATUS MH_CreateHook(IntPtr pTarget, IntPtr pDetour, out IntPtr ppOriginal);

        [DllImport(Dll86, CallingConvention = CallingConvention.Cdecl)]
        public static extern MH_STATUS MH_RemoveHook(IntPtr pTarget);

        [DllImport(Dll86, CallingConvention = CallingConvention.Cdecl)]
        public static extern MH_STATUS MH_EnableHook(IntPtr pTarget);

        [DllImport(Dll86, CallingConvention = CallingConvention.Cdecl)]
        public static extern MH_STATUS MH_DisableHook(IntPtr pTarget);
    }

    private static class MinHook64
    {
        [DllImport(Dll64, CallingConvention = CallingConvention.Cdecl)]
        public static extern MH_STATUS MH_Initialize();

        [DllImport(Dll64, CallingConvention = CallingConvention.Cdecl)]
        public static extern MH_STATUS MH_Uninitialize();

        [DllImport(Dll64, CallingConvention = CallingConvention.Cdecl)]
        public static extern MH_STATUS MH_CreateHook(IntPtr pTarget, IntPtr pDetour, out IntPtr ppOriginal);

        [DllImport(Dll64, CallingConvention = CallingConvention.Cdecl)]
        public static extern MH_STATUS MH_RemoveHook(IntPtr pTarget);

        [DllImport(Dll64, CallingConvention = CallingConvention.Cdecl)]
        public static extern MH_STATUS MH_EnableHook(IntPtr pTarget);

        [DllImport(Dll64, CallingConvention = CallingConvention.Cdecl)]
        public static extern MH_STATUS MH_DisableHook(IntPtr pTarget);
    }

    public static MH_STATUS Initialize()
        => IntPtr.Size == 8 ? MinHook64.MH_Initialize() : MinHook86.MH_Initialize();

    public static MH_STATUS Uninitialize()
        => IntPtr.Size == 8 ? MinHook64.MH_Uninitialize() : MinHook86.MH_Uninitialize();

    public static MH_STATUS CreateHook(IntPtr target, IntPtr detour, out IntPtr original)
        => IntPtr.Size == 8 ? MinHook64.MH_CreateHook(target, detour, out original) : MinHook86.MH_CreateHook(target, detour, out original);

    public static MH_STATUS RemoveHook(IntPtr target)
        => IntPtr.Size == 8 ? MinHook64.MH_RemoveHook(target) : MinHook86.MH_RemoveHook(target);

    public static MH_STATUS EnableHook(IntPtr target)
        => IntPtr.Size == 8 ? MinHook64.MH_EnableHook(target) : MinHook86.MH_EnableHook(target);

    public static MH_STATUS DisableHook(IntPtr target)
        => IntPtr.Size == 8 ? MinHook64.MH_DisableHook(target) : MinHook86.MH_DisableHook(target);

    public static void Ok(MH_STATUS status, string operation)
    {
        if (status != 0)
            throw new InvalidOperationException($"MinHook {operation} failed: {status}");
    }
}