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
    private static Device _device;
    private static DeviceContext _deviceContext;

    private static IntPtr _windowHandle;
    private static User32.WndProcDelegate _myWindowProc;
    private static IntPtr _originalWindowProc;

    private static RenderTargetView _renderTargetView;

    private static bool _isInitialized;

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

    private static IntPtr WndProcHandler(IntPtr hWnd, WindowMessage uMsg, IntPtr wParam, IntPtr lParam)
    {
        var io = ImGui.GetIO();
        ImGuiImplWin32.WndProcHandler(hWnd, uMsg, wParam, lParam, io);
        bool wantCaptureMouse = io.WantCaptureMouse;
        bool wantCaptureKeyboard = io.WantCaptureKeyboard;
        if (wantCaptureMouse)
        {
            switch (uMsg)
            {
                case WindowMessage.WM_MOUSEMOVE:
                case WindowMessage.WM_NCMOUSEMOVE:
                case WindowMessage.WM_LBUTTONDOWN:
                case WindowMessage.WM_LBUTTONUP:
                case WindowMessage.WM_RBUTTONDOWN:
                case WindowMessage.WM_RBUTTONUP:
                case WindowMessage.WM_MBUTTONDOWN:
                case WindowMessage.WM_MBUTTONUP:
                case WindowMessage.WM_MOUSEWHEEL:
                case WindowMessage.WM_MOUSEHWHEEL:
                case WindowMessage.WM_XBUTTONDOWN:
                case WindowMessage.WM_XBUTTONUP:
                    return IntPtr.Zero;
            }
        }
        if (wantCaptureKeyboard)
        {
            switch (uMsg)
            {
                case WindowMessage.WM_KEYDOWN:
                case WindowMessage.WM_KEYUP:
                case WindowMessage.WM_SYSKEYDOWN:
                case WindowMessage.WM_SYSKEYUP:
                case WindowMessage.WM_CHAR:
                case WindowMessage.WM_SYSCHAR:
                    return IntPtr.Zero;
            }
        }
        return User32.CallWindowProc(_originalWindowProc, hWnd, uMsg, wParam, lParam);
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
        foreach (ImGuiModule module in DearImGuiInjectionCore.Modules)
        {
            ImGui.SetCurrentContext(module.Context);
            if (!module.IsInitialized)
            {
                ImGuiImplWin32.Init(_windowHandle);
                ImGuiImplDX11.Init(_device.NativePointer, _deviceContext.NativePointer);
                module.OnInit?.Invoke(module);
                module.IsInitialized = true;
            }
            ImGuiImplWin32.NewFrame();
            ImGuiImplDX11.NewFrame();
            ImGui.NewFrame();
            module.OnRender.Invoke(module);
            ImGui.Render();
            ImGuiImplDX11.RenderDrawData(ImGui.GetDrawData().Handle);
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
