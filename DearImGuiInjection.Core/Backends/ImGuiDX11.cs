using DearImGuiInjection.Renderers;
using DearImGuiInjection.Windows;
using Hexa.NET.ImGui;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using System;
using System.Collections.Generic;
using System.Numerics;
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

    private static readonly List<ImGuiModule> _zOrdered = new();
    private static ImGuiModule _mouseOwner;
    private static ImGuiModule _focusedModule;
    private static int _mouseButtonsDownMask;

    private static void EnsureZCache()
    {
        if (_zOrdered.Count == DearImGuiInjectionCore.Modules.Count)
            return;
        _zOrdered.Clear();
        foreach (var m in DearImGuiInjectionCore.Modules)
            _zOrdered.Add(m);
        _zOrdered.Sort(static (a, b) =>
        {
            int z = a.ZIndex.CompareTo(b.ZIndex);
            return z != 0 ? z : StringComparer.Ordinal.Compare(a.GUID, b.GUID);
        });
        NormalizeZIndices();
    }

    private static void NormalizeZIndices()
    {
        for (int i = 0; i < _zOrdered.Count; i++)
            _zOrdered[i].ZIndex = i;
    }

    private static void BringToFront(ImGuiModule module)
    {
        if (module == null)
            return;
        int idx = _zOrdered.IndexOf(module);
        if (idx < 0)
            return;
        if (idx == _zOrdered.Count - 1)
            return;
        _zOrdered.RemoveAt(idx);
        _zOrdered.Add(module);
        NormalizeZIndices();
    }

    private static ImGuiModule FindTopHoveredModule()
    {
        for (int i = _zOrdered.Count - 1; i >= 0; i--)
        {
            if (_zOrdered[i].IsHoveredThisFrame)
                return _zOrdered[i];
        }
        return null;
    }

    private static int GetXButtonMask(IntPtr wParam)
    {
        int button = (short)((((long)wParam) >> 16) & 0xFFFF);
        return button == 1 ? (1 << 3) : button == 2 ? (1 << 4) : 0;
    }

    private static bool IsMouseDownMsg(WindowMessage m) => m is
        WindowMessage.WM_LBUTTONDOWN or WindowMessage.WM_RBUTTONDOWN or
        WindowMessage.WM_MBUTTONDOWN or WindowMessage.WM_XBUTTONDOWN or
        WindowMessage.WM_LBUTTONDBLCLK or WindowMessage.WM_RBUTTONDBLCLK or
        WindowMessage.WM_MBUTTONDBLCLK or WindowMessage.WM_XBUTTONDBLCLK;

    private static bool IsMouseUpMsg(WindowMessage m) => m is
        WindowMessage.WM_LBUTTONUP or WindowMessage.WM_RBUTTONUP or
        WindowMessage.WM_MBUTTONUP or WindowMessage.WM_XBUTTONUP;

    private static bool IsMouseMsg(WindowMessage m) => m is
        WindowMessage.WM_MOUSEMOVE or WindowMessage.WM_NCMOUSEMOVE or
        WindowMessage.WM_LBUTTONDOWN or WindowMessage.WM_LBUTTONUP or
        WindowMessage.WM_RBUTTONDOWN or WindowMessage.WM_RBUTTONUP or
        WindowMessage.WM_MBUTTONDOWN or WindowMessage.WM_MBUTTONUP or
        WindowMessage.WM_MOUSEWHEEL or WindowMessage.WM_MOUSEHWHEEL or
        WindowMessage.WM_XBUTTONDOWN or WindowMessage.WM_XBUTTONUP or
        WindowMessage.WM_LBUTTONDBLCLK or WindowMessage.WM_RBUTTONDBLCLK or
        WindowMessage.WM_MBUTTONDBLCLK or WindowMessage.WM_XBUTTONDBLCLK;

    private static bool IsKeyMsg(WindowMessage m) => m is
        WindowMessage.WM_KEYDOWN or WindowMessage.WM_KEYUP or
        WindowMessage.WM_SYSKEYDOWN or WindowMessage.WM_SYSKEYUP or
        WindowMessage.WM_CHAR or WindowMessage.WM_SYSCHAR;

    private static bool IsCaptureCancelMsg(WindowMessage m) => m is
        WindowMessage.WM_CANCELMODE or WindowMessage.WM_CAPTURECHANGED or WindowMessage.WM_KILLFOCUS;

    private static void UpdateMouseButtonsDown(WindowMessage uMsg, IntPtr wParam)
    {
        switch (uMsg)
        {
            case WindowMessage.WM_LBUTTONDOWN: _mouseButtonsDownMask |= (1 << 0); break;
            case WindowMessage.WM_LBUTTONUP: _mouseButtonsDownMask &= ~(1 << 0); break;

            case WindowMessage.WM_RBUTTONDOWN: _mouseButtonsDownMask |= (1 << 1); break;
            case WindowMessage.WM_RBUTTONUP: _mouseButtonsDownMask &= ~(1 << 1); break;

            case WindowMessage.WM_MBUTTONDOWN: _mouseButtonsDownMask |= (1 << 2); break;
            case WindowMessage.WM_MBUTTONUP: _mouseButtonsDownMask &= ~(1 << 2); break;

            case WindowMessage.WM_XBUTTONDOWN:
                {
                    int x = GetXButtonMask(wParam);
                    if (x != 0) _mouseButtonsDownMask |= x;
                    break;
                }
            case WindowMessage.WM_XBUTTONUP:
                {
                    int x = GetXButtonMask(wParam);
                    if (x != 0) _mouseButtonsDownMask &= ~x;
                    break;
                }
        }
    }

    private static IntPtr WndProcHandler(IntPtr hWnd, WindowMessage uMsg, IntPtr wParam, IntPtr lParam)
    {
        EnsureZCache();
        bool isMouse = IsMouseMsg(uMsg);
        bool isKey = IsKeyMsg(uMsg);
        if (IsCaptureCancelMsg(uMsg))
        {
            _mouseOwner = null;
            _mouseButtonsDownMask = 0;
            return User32.CallWindowProc(_originalWindowProc, hWnd, uMsg, wParam, lParam);
        }
        if (_mouseOwner == null && IsMouseDownMsg(uMsg))
        {
            ImGuiModule topHovered = FindTopHoveredModule();
            if (topHovered != null)
            {
                if (_focusedModule != null && _focusedModule != topHovered)
                    _focusedModule.Unfocus();
                _focusedModule = topHovered;
                BringToFront(topHovered);
                _mouseOwner = topHovered;
                UpdateMouseButtonsDown(uMsg, wParam);
                ImGuiImplWin32.WndProcHandler(hWnd, uMsg, wParam, lParam, _mouseOwner.IO);

                return IntPtr.Zero;
            }
        }
        if (_mouseOwner != null)
        {
            bool isMouseUp = IsMouseUpMsg(uMsg);
            if (isMouse)
                UpdateMouseButtonsDown(uMsg, wParam);
            ImGuiImplWin32.WndProcHandler(hWnd, uMsg, wParam, lParam, _mouseOwner.IO);
            if (isMouse && isMouseUp && _mouseButtonsDownMask == 0)
                _mouseOwner = null;
            if (isMouse || isKey)
                return IntPtr.Zero;
            return User32.CallWindowProc(_originalWindowProc, hWnd, uMsg, wParam, lParam);
        }
        for (int i = 0; i < _zOrdered.Count; i++)
            ImGuiImplWin32.WndProcHandler(hWnd, uMsg, wParam, lParam, _zOrdered[i].IO);
        bool wantCaptureMouse = false;
        bool wantCaptureKeyboard = false;
        for (int i = _zOrdered.Count - 1; i >= 0; i--)
        {
            var io = _zOrdered[i].IO;
            if (!wantCaptureMouse && io.WantCaptureMouse)
                wantCaptureMouse = true;
            if (!wantCaptureKeyboard && io.WantCaptureKeyboard)
                wantCaptureKeyboard = true;
            if (wantCaptureMouse && wantCaptureKeyboard)
                break;
        }
        var hoveredTop2 = FindTopHoveredModule();
        if (hoveredTop2 != null && hoveredTop2.IO.WantCaptureMouse)
            wantCaptureMouse = true;
        if (wantCaptureMouse && isMouse)
            return IntPtr.Zero;
        if (wantCaptureKeyboard && isKey)
            return IntPtr.Zero;
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
        Log.Info("ImGui_ImplWin32_Shutdown()");
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
        EnsureZCache();
        for (int i = 0; i < _zOrdered.Count; i++)
        {
            ImGuiModule module = _zOrdered[i];
            ImGui.SetCurrentContext(module.Context);
            if (!module.IsInitialized)
            {
                ImGuiImplWin32.Init(_windowHandle);
                ImGuiImplDX11.Init(_device.NativePointer, _deviceContext.NativePointer);
                module.OnInit?.Invoke();
            }
            module.IsHoveredThisFrame = false;
            ImGuiImplWin32.NewFrame();
            ImGuiImplDX11.NewFrame();
            ImGui.NewFrame();
            module.OnRender.Invoke();
            module.IsHoveredThisFrame = ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow) ||
                ImGui.IsAnyItemHovered() ||
                ImGui.IsAnyItemActive();
            ImGui.Render();
            ImGuiImplDX11.RenderDrawData(ImGui.GetDrawData().Handle);
            if (!module.IsInitialized)
            {
                module.Unfocus();
                module.IsInitialized = true;
            }
        }
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
