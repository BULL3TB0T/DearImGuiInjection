using DearImGuiInjection.Renderers;
using DearImGuiInjection.Windows;
using Hexa.NET.ImGui;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Device = SharpDX.Direct3D11.Device;

namespace DearImGuiInjection.Backends;

internal static class ImGuiDX11
{
    private static bool _isInitialized;

    private static Device _device;
    private static DeviceContext _deviceContext;

    private static IntPtr _windowHandle;
    private static User32.WndProcDelegate _myWindowProc;
    private static IntPtr _originalWindowProc;

    private static RenderTargetView _renderTargetView;

    private static List<ImGuiModule> _orderedModules = new();
    private static ImGuiModule _focusedModule;
    private static ImGuiModule _dragModule;

    private static bool _saveToSettings;
    private const string _settingsTypeName = "DearImGuiInjection";
    private const string _settingsSectionName = "ZOrder";

    private static IntPtr _settingsTypeNamePtr;
    private static readonly Dictionary<string, int> _settingsData = new();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void ClearAllDelegate(ImGuiContext* ctx, ImGuiSettingsHandler* handler);
    private unsafe static readonly ClearAllDelegate _zClearAllDel = Z_ClearAll;
    private static readonly IntPtr _zClearAllPtr = Marshal.GetFunctionPointerForDelegate(_zClearAllDel);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void* ReadOpenDelegate(ImGuiContext* ctx, ImGuiSettingsHandler* handler, byte* name);
    private unsafe static readonly ReadOpenDelegate _zReadOpenDel = Z_ReadOpen;
    private static readonly IntPtr _zReadOpenPtr = Marshal.GetFunctionPointerForDelegate(_zReadOpenDel);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void ReadLineDelegate(ImGuiContext* ctx, ImGuiSettingsHandler* handler, void* entry, byte* line);
    private unsafe static readonly ReadLineDelegate _zReadLineDel = Z_ReadLine;
    private static readonly IntPtr _zReadLinePtr = Marshal.GetFunctionPointerForDelegate(_zReadLineDel);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void ApplyAllDelegate(ImGuiContext* ctx, ImGuiSettingsHandler* handler);
    private unsafe static readonly ApplyAllDelegate _zApplyAllDel = Z_ApplyAll;
    private static readonly IntPtr _zApplyAllPtr = Marshal.GetFunctionPointerForDelegate(_zApplyAllDel);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void WriteAllDelegate(ImGuiContext* ctx, ImGuiSettingsHandler* handler, ImGuiTextBuffer* outBuf);
    private unsafe static readonly WriteAllDelegate _zWriteAllDel = Z_WriteAll;
    private static readonly IntPtr _zWriteAllPtr = Marshal.GetFunctionPointerForDelegate(_zWriteAllDel);

    private static unsafe void Z_ClearAll(ImGuiContext* ctx, ImGuiSettingsHandler* handler) => _settingsData.Clear();

    private static unsafe void* Z_ReadOpen(ImGuiContext* ctx, ImGuiSettingsHandler* handler, byte* name)
    {
        if (name == null || Marshal.PtrToStringAnsi((IntPtr)name) != _settingsSectionName)
            return null;
        return ctx;
    }

    private static unsafe void Z_ReadLine(ImGuiContext* ctx, ImGuiSettingsHandler* handler, void* entry, byte* line)
    {
        if (line == null)
            return;
        string s = Marshal.PtrToStringAnsi((IntPtr)line);
        if (string.IsNullOrWhiteSpace(s))
            return;
        int eq = s.IndexOf('=');
        if (eq <= 0 || eq >= s.Length - 1)
            return;
        string GUID = s.Substring(0, eq).Trim();
        if (GUID.Length == 0)
            return;
        if (!int.TryParse(s.Substring(eq + 1).Trim(), out int zIndex))
            return;
        _settingsData[GUID] = zIndex;
    }

    private static unsafe void Z_ApplyAll(ImGuiContext* ctx, ImGuiSettingsHandler* handler)
    {
        if (_settingsData.Count != _orderedModules.Count)
            return;
        for (int i = 0; i < _orderedModules.Count; i++)
        {
            ImGuiModule module = _orderedModules[i];
            if (_settingsData.TryGetValue(module.GUID, out int zIndex))
                module.ZIndex = zIndex;
        }
        _orderedModules.Sort((a, b) => a.ZIndex.CompareTo(b.ZIndex));
        Normalize();
    }

    private static unsafe void Z_WriteAll(ImGuiContext* ctx, ImGuiSettingsHandler* handler, ImGuiTextBuffer* buf)
    {
        buf->appendf($"[{_settingsTypeName}][{_settingsSectionName}]\n");
        IntPtr iniFilename = (IntPtr)ctx->IO.IniFilename;
        List<ImGuiModule> list = new();
        for (int i = 0; i < _orderedModules.Count; i++)
        {
            ImGuiModule module = _orderedModules[i];
            var moduleIniFilename = module.IO.IniFilename;
            if (moduleIniFilename == null || 
                Marshal.PtrToStringAnsi((IntPtr)moduleIniFilename) != Marshal.PtrToStringAnsi(iniFilename))
                continue;
            list.Add(module);
        }
        list.Sort((a, b) => a.ZIndex.CompareTo(b.ZIndex));
        for (int i = 0; i < list.Count; i++)
        {
            var module = list[i];
            buf->appendf($"{module.GUID}={module.ZIndex}\n");
        }
        buf->appendf("\n");
    }

    private static void Normalize()
    {
        for (int i = 0; i < _orderedModules.Count; i++)
            _orderedModules[i].ZIndex = i;
    }

    private static void EnsureCache()
    {
        if (_orderedModules.Count == DearImGuiInjectionCore.Modules.Count)
            return;
        foreach (var module in DearImGuiInjectionCore.Modules)
            if (!_orderedModules.Contains(module))
                _orderedModules.Add(module);
        _orderedModules.Sort((a, b) => a.ZIndex.CompareTo(b.ZIndex));
        Normalize();
    }

    private unsafe static void BringToFront(ImGuiModule module)
    {
        int indexOf = _orderedModules.IndexOf(module);
        if (indexOf < 0 || indexOf == _orderedModules.Count - 1)
            return;
        _orderedModules.RemoveAt(indexOf);
        _orderedModules.Add(module);
        Normalize();
        _saveToSettings = true;
    }
     
    private static ImGuiModule FindTopHoveredModule()
    {
        for (int i = _orderedModules.Count - 1; i >= 0; i--)
        {
            ImGuiModule module = _orderedModules[i];
            if (module.IO.WantCaptureMouse)
                return module;
        }
        return null;
    }

    private static IntPtr WndProcHandler(IntPtr hWnd, WindowMessage uMsg, IntPtr wParam, IntPtr lParam)
    {
        bool isMouse =
               uMsg == WindowMessage.WM_MOUSEMOVE || uMsg == WindowMessage.WM_NCMOUSEMOVE
               || uMsg == WindowMessage.WM_LBUTTONDOWN || uMsg == WindowMessage.WM_LBUTTONUP
               || uMsg == WindowMessage.WM_RBUTTONDOWN || uMsg == WindowMessage.WM_RBUTTONUP
               || uMsg == WindowMessage.WM_MBUTTONDOWN || uMsg == WindowMessage.WM_MBUTTONUP
               || uMsg == WindowMessage.WM_MOUSEWHEEL || uMsg == WindowMessage.WM_MOUSEHWHEEL
               || uMsg == WindowMessage.WM_XBUTTONDOWN || uMsg == WindowMessage.WM_XBUTTONUP
               || uMsg == WindowMessage.WM_LBUTTONDBLCLK || uMsg == WindowMessage.WM_RBUTTONDBLCLK
               || uMsg == WindowMessage.WM_MBUTTONDBLCLK || uMsg == WindowMessage.WM_XBUTTONDBLCLK;
        bool isKey =
               uMsg == WindowMessage.WM_KEYDOWN || uMsg == WindowMessage.WM_KEYUP || uMsg == WindowMessage.WM_SYSKEYDOWN
               || uMsg == WindowMessage.WM_SYSKEYUP || uMsg == WindowMessage.WM_CHAR || uMsg == WindowMessage.WM_SYSCHAR;
        ImGuiModule topHoveredModule = FindTopHoveredModule();
        if (uMsg == WindowMessage.WM_MOUSEMOVE || uMsg == WindowMessage.WM_NCMOUSEMOVE)
        {
            for (int i = 0; i < _orderedModules.Count; i++)
                ImGuiImplWin32.WndProcHandler(hWnd, uMsg, wParam, lParam, _orderedModules[i].IO);
            if (topHoveredModule != null)
                return IntPtr.Zero;
            return User32.CallWindowProc(_originalWindowProc, hWnd, uMsg, wParam, lParam);
        }
        if (_dragModule != null)
        {
            ImGuiImplWin32.WndProcHandler(hWnd, uMsg, wParam, lParam, _dragModule.IO);
            if (uMsg == WindowMessage.WM_LBUTTONUP || uMsg == WindowMessage.WM_KILLFOCUS)
                _dragModule = null;
            return User32.CallWindowProc(_originalWindowProc, hWnd, uMsg, wParam, lParam);
        }
        if (isMouse && topHoveredModule != null)
        {
            ImGuiImplWin32.WndProcHandler(hWnd, uMsg, wParam, lParam, topHoveredModule.IO);
            if (uMsg == WindowMessage.WM_LBUTTONDOWN)
            {
                if (_focusedModule != topHoveredModule)
                {
                    if (_focusedModule != null)
                        _focusedModule.UnfocusNextFrame = true;
                    BringToFront(topHoveredModule);
                }
                _focusedModule = topHoveredModule;
                _dragModule = topHoveredModule;
            }
            return IntPtr.Zero;
        }
        if (uMsg == WindowMessage.WM_LBUTTONDOWN && _focusedModule != null)
        {
            _focusedModule.UnfocusNextFrame = true;
            _focusedModule = null;
        }
        if (isKey && _focusedModule != null && _focusedModule.IO.WantCaptureKeyboard)
        {
            ImGuiImplWin32.WndProcHandler(hWnd, uMsg, wParam, lParam, _focusedModule.IO);
            return IntPtr.Zero;
        }
        return User32.CallWindowProc(_originalWindowProc, hWnd, uMsg, wParam, lParam);
    }

    public static void Init()
    {
        DX11Renderer.OnPresent += OnPresent;
        DX11Renderer.OnPreResizeBuffers += OnPreResizeBuffers;
        DX11Renderer.OnPostResizeBuffers += OnPostResizeBuffers;
    }

    public static void Dispose()
    {
        DX11Renderer.OnPostResizeBuffers -= OnPostResizeBuffers;
        DX11Renderer.OnPreResizeBuffers -= OnPreResizeBuffers;
        DX11Renderer.OnPresent -= OnPresent;
        if (!DearImGuiInjectionCore.IsInitialized)
            return;
        foreach (ImGuiModule module in DearImGuiInjectionCore.Modules)
        {
            ImGui.SetCurrentContext(module.Context);
            ImGuiImplDX11.Shutdown();
            ImGuiImplWin32.Shutdown();
        }
        _renderTargetView?.Dispose();
        _renderTargetView = null;
        _deviceContext?.Dispose();
        _deviceContext = null;
        _device?.Dispose();
        _device = null;
        User32.SetWindowLong(_windowHandle, User32.GWL_WNDPROC, _originalWindowProc);
        _originalWindowProc = IntPtr.Zero;
        _myWindowProc = null;
        _windowHandle = IntPtr.Zero;
    }

    private unsafe static void OnPresent(SwapChain swapChain, uint syncInterval, uint flags)
    {
        if (!DearImGuiInjectionCore.IsInitialized)
            return;
        if (!_isInitialized)
        {
            _device = swapChain.GetDevice<Device>();
            _deviceContext = _device.ImmediateContext;
            _windowHandle = swapChain.Description.OutputHandle;
            _myWindowProc = new User32.WndProcDelegate(WndProcHandler);
            _originalWindowProc = User32.SetWindowLong(_windowHandle, User32.GWL_WNDPROC,
                Marshal.GetFunctionPointerForDelegate(_myWindowProc));
            _isInitialized = true;
        }
        if (_renderTargetView == null)
        {
            using var backBuffer = swapChain.GetBackBuffer<Texture2D>(0);
            _renderTargetView = new RenderTargetView(_device, backBuffer);
        }
        _deviceContext.OutputMerger.SetRenderTargets(_renderTargetView);
        EnsureCache();
        for (int i = 0; i < _orderedModules.Count; i++)
        {
            ImGuiModule module = _orderedModules[i];
            ImGui.SetCurrentContext(module.Context);
            var iniFileName = module.IO.IniFilename;
            if (!module.IsInitialized)
            {
                ImGuiImplWin32.Init(_windowHandle);
                ImGuiImplDX11.Init(_device.NativePointer, _deviceContext.NativePointer);
                module.OnInit?.Invoke();
                if (iniFileName != null)
                {
                    if (_settingsTypeNamePtr == IntPtr.Zero)
                        _settingsTypeNamePtr = Marshal.StringToHGlobalAnsi(_settingsTypeName);
                    ImGuiSettingsHandler* handler = 
                        (ImGuiSettingsHandler*)Marshal.AllocHGlobal(sizeof(ImGuiSettingsHandler));
                    *handler = default;
                    handler->TypeName = (byte*)_settingsTypeNamePtr;
                    handler->TypeHash = ImGuiP.ImHashStr((byte*)_settingsTypeNamePtr);
                    handler->ClearAllFn = (void*)_zClearAllPtr;
                    handler->ReadOpenFn = (void*)_zReadOpenPtr;
                    handler->ReadLineFn = (void*)_zReadLinePtr;
                    handler->ApplyAllFn = (void*)_zApplyAllPtr;
                    handler->WriteAllFn = (void*)_zWriteAllPtr;
                    ImGuiP.AddSettingsHandler(handler);
                }
                module.IsInitialized = true;
            }
            ImGuiImplWin32.NewFrame();
            ImGuiImplDX11.NewFrame();
            ImGui.NewFrame();
            if (_saveToSettings && iniFileName != null)
                ImGui.SaveIniSettingsToDisk(iniFileName);
            module.OnRender.Invoke();
            ImGui.Render();
            ImGuiImplDX11.RenderDrawData(ImGui.GetDrawData().Handle);
            if (module.UnfocusNextFrame)
            {
                ImGuiP.FocusWindow(null);
                module.UnfocusNextFrame = false;
            }
        }
        _saveToSettings = false;
    }

    private static void OnPreResizeBuffers(SwapChain swapChain, uint bufferCount, uint width, uint height, Format newFormat, uint swapchainFlags)
    {
        if (!DearImGuiInjectionCore.IsInitialized)
            return;
        _renderTargetView?.Dispose();
        _renderTargetView = null;
        foreach (ImGuiModule module in DearImGuiInjectionCore.Modules)
        {
            ImGui.SetCurrentContext(module.Context);
            ImGuiImplDX11.InvalidateDeviceObjects();
        }
    }

    private static void OnPostResizeBuffers(SwapChain swapChain, uint bufferCount, uint width, uint height, Format newFormat, uint swapchainFlags)
    {
        if (!DearImGuiInjectionCore.IsInitialized)
            return;
        foreach (ImGuiModule module in DearImGuiInjectionCore.Modules)
        {
            ImGui.SetCurrentContext(module.Context);
            ImGuiImplDX11.CreateDeviceObjects();
        }
    }
}
