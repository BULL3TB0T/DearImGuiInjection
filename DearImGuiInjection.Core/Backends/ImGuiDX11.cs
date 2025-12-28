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

    private static IntPtr WndProcHandler(IntPtr hWnd, WindowMessage uMsg, IntPtr wParam, IntPtr lParam)
    {
        IntPtr result = IntPtr.Zero;
        bool wantCaptureMouse = false;
        bool wantCaptureKeyboard = false;
        foreach (ImGuiModule module in DearImGuiInjectionCore.MultiContextCompositor.Modules)
        {
            var io = module.IO;
            IntPtr handlerResult = ImGuiImplWin32.WndProcHandler(hWnd, uMsg, wParam, lParam, io);
            if (result == IntPtr.Zero && handlerResult != IntPtr.Zero)
                result = handlerResult;
            if (module.OnWndProc != null)
            {
                bool modResult = module.OnWndProc(hWnd, uMsg, wParam, lParam);
                if (result == IntPtr.Zero && modResult)
                    result = (IntPtr)1;
            }
            wantCaptureMouse |= io.WantCaptureMouse;
            wantCaptureKeyboard |= io.WantCaptureKeyboard;
        }
        if (wantCaptureMouse || wantCaptureKeyboard)
            result = (IntPtr)1;
        if (result != IntPtr.Zero)
            return result;
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
        foreach (ImGuiModule module in DearImGuiInjectionCore.MultiContextCompositor.Modules)
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
        DearImGuiInjectionCore.MultiContextCompositor.PreNewFrameUpdateAll();
        for (int i = DearImGuiInjectionCore.MultiContextCompositor.ModulesFrontToBack.Count - 1; i >= 0; i--)
        {
            ImGuiModule module = DearImGuiInjectionCore.MultiContextCompositor.ModulesFrontToBack[i];
            ImGui.SetCurrentContext(module.Context);
            if (!module.IsInitialized)
            {
                ImGuiImplWin32.Init(_windowHandle);
                ImGuiImplDX11.Init(_device.NativePointer, _deviceContext.NativePointer);
                module.OnInit?.Invoke();
            }
            ImGuiImplWin32.NewFrame();
            ImGuiImplDX11.NewFrame();
            ImGui.NewFrame();
            DearImGuiInjectionCore.MultiContextCompositor.PostNewFrameUpdateOne(module);
            if (module.OnRender != null)
            {
                module.OnRender.Invoke();
                ImGui.Render();
            }
            else
                ImGui.EndFrame();
            ImGuiImplDX11.RenderDrawData(ImGui.GetDrawData().Handle);
            if (!module.IsInitialized)
            {
                ImGuiP.FocusWindow(null);
                module.IsInitialized = true;
            }
        }
        DearImGuiInjectionCore.MultiContextCompositor.PostEndFrameUpdateAll();
    }

    private static void OnPreResizeBuffers(SwapChain swapChain, uint bufferCount, uint width, uint height, Format newFormat, uint swapchainFlags)
    {
        if (!DearImGuiInjectionCore.IsInitialized)
            return;
        _renderTargetView?.Dispose();
        _renderTargetView = null;
        foreach (ImGuiModule module in DearImGuiInjectionCore.MultiContextCompositor.Modules)
        {
            ImGui.SetCurrentContext(module.Context);
            ImGuiImplDX11.InvalidateDeviceObjects();
        }
    }

    private static void OnPostResizeBuffers(SwapChain swapChain, uint bufferCount, uint width, uint height, Format newFormat, uint swapchainFlags)
    {
        if (!DearImGuiInjectionCore.IsInitialized)
            return;
        foreach (ImGuiModule module in DearImGuiInjectionCore.MultiContextCompositor.Modules)
        {
            ImGui.SetCurrentContext(module.Context);
            ImGuiImplDX11.CreateDeviceObjects();
        }
    }
}
