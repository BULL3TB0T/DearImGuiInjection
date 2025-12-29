using DearImGuiInjection.Renderers;
using DearImGuiInjection.Textures;
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
        bool IsKeyUpMsg()
            => uMsg == WindowMessage.WM_KEYUP || uMsg == WindowMessage.WM_SYSKEYUP;
        bool IsMouseUpMsg()
            => uMsg == WindowMessage.WM_LBUTTONUP
            || uMsg == WindowMessage.WM_RBUTTONUP
            || uMsg == WindowMessage.WM_MBUTTONUP
            || uMsg == WindowMessage.WM_XBUTTONUP;
        bool IsKeyboardMsg()
            => uMsg == WindowMessage.WM_KEYDOWN
            || uMsg == WindowMessage.WM_KEYUP
            || uMsg == WindowMessage.WM_SYSKEYDOWN
            || uMsg == WindowMessage.WM_SYSKEYUP
            || uMsg == WindowMessage.WM_CHAR
            || uMsg == WindowMessage.WM_SYSCHAR
            || uMsg == WindowMessage.WM_IME_STARTCOMPOSITION
            || uMsg == WindowMessage.WM_IME_COMPOSITION
            || uMsg == WindowMessage.WM_IME_ENDCOMPOSITION;
        bool IsMouseMsg()
            => uMsg == WindowMessage.WM_MOUSEMOVE
            || uMsg == WindowMessage.WM_LBUTTONDOWN
            || uMsg == WindowMessage.WM_LBUTTONUP
            || uMsg == WindowMessage.WM_LBUTTONDBLCLK
            || uMsg == WindowMessage.WM_RBUTTONDOWN
            || uMsg == WindowMessage.WM_RBUTTONUP
            || uMsg == WindowMessage.WM_RBUTTONDBLCLK
            || uMsg == WindowMessage.WM_MBUTTONDOWN
            || uMsg == WindowMessage.WM_MBUTTONUP
            || uMsg == WindowMessage.WM_MBUTTONDBLCLK
            || uMsg == WindowMessage.WM_XBUTTONDOWN
            || uMsg == WindowMessage.WM_XBUTTONUP
            || uMsg == WindowMessage.WM_XBUTTONDBLCLK
            || uMsg == WindowMessage.WM_MOUSEWHEEL
            || uMsg == WindowMessage.WM_MOUSEHWHEEL;
        IntPtr result = IntPtr.Zero;
        bool allowUpMessages = DearImGuiInjectionCore.AllowUpMessages.GetValue();
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
            if (result == IntPtr.Zero)
            {
                if (IsMouseMsg() && io.WantCaptureMouse && !(allowUpMessages && IsMouseUpMsg()))
                    result = (IntPtr)1;
                if (IsKeyboardMsg() && io.WantCaptureKeyboard && !(allowUpMessages && IsKeyUpMsg()))
                    result = (IntPtr)1;
            }
        }
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
        DearImGuiInjectionCore.TextureManager.Dispose();
        foreach (ImGuiModule module in DearImGuiInjectionCore.MultiContextCompositor.Modules)
        {
            ImGui.SetCurrentContext(module.Context);
            ImGuiImplDX11.Shutdown();
            ImGuiImplWin32.Shutdown();
            ImGui.DestroyPlatformWindows();
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
            DearImGuiInjectionCore.TextureManager = new DX11TextureManager(_device);
            _isInitialized = true;
        }
        DearImGuiInjectionCore.TextureManager.Update();
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
                try
                {
                    module.OnInit?.Invoke();
                }
                catch (Exception e)
                {
                    Log.Error($"Module \"{module.Id}\" OnInit threw an exception: {e}");
                }
                module.IsInitialized = true;
            }
            ImGuiImplWin32.NewFrame();
            ImGuiImplDX11.NewFrame();
            ImGui.NewFrame();
            DearImGuiInjectionCore.MultiContextCompositor.PostNewFrameUpdateOne(module);
            try
            {
                module.OnRender();
                ImGui.Render();
                ImGuiImplDX11.RenderDrawData(ImGui.GetDrawData().Handle);
            }
            catch (Exception e)
            {
                ImGui.EndFrame();
                Log.Error($"Module \"{module.Id}\" OnRender threw an exception: {e}");
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
