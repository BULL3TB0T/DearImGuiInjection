using DearImGuiInjection.Renderers;
using DearImGuiInjection.Windows;
using Hexa.NET.ImGui;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Runtime.InteropServices;
using Device = SharpDX.Direct3D11.Device;

namespace DearImGuiInjection.Backends;

internal static class ImGuiDX11
{
    private static IntPtr _windowHandle;

    private static Device _device;
    private static DeviceContext _deviceContext;

    private static RenderTargetView _renderTargetView;
    private static User32.WndProcDelegate _myWindowProc;
    private static IntPtr _originalWindowProc;

    private static POINT _cursorCoords;

    internal static void Init()
    {
        DX11Renderer.OnPresent += OnPresent;
        DX11Renderer.PreResizeBuffers += PreResizeBuffers;
        DX11Renderer.PostResizeBuffers += PostResizeBuffers;
    }

    internal static void Dispose()
    {
        DX11Renderer.PostResizeBuffers -= PostResizeBuffers;
        DX11Renderer.PreResizeBuffers -= PreResizeBuffers;
        DX11Renderer.OnPresent -= OnPresent;

        if (!DearImGuiInjectionCore.Initialized)
            return;

        ImGuiDX11Impl.Shutdown();
        Log.Info("ImGui.ImGui_ImplWin32_Shutdown()");
        ImGuiWin32Impl.Shutdown();

        _renderTargetView?.Dispose();
        _renderTargetView = null;

        if (_windowHandle != IntPtr.Zero && _originalWindowProc != IntPtr.Zero)
            User32.SetWindowLong(_windowHandle, User32.GWL_WNDPROC, _originalWindowProc);

        _originalWindowProc = IntPtr.Zero;
        _myWindowProc = null;
        _windowHandle = IntPtr.Zero;

        _deviceContext?.Dispose();
        _deviceContext = null;

        _device?.Dispose();
        _device = null;
    }

    private static void InitImGuiWin32(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        if (_windowHandle == IntPtr.Zero)
            return;

        ImGuiWin32Impl.Init(_windowHandle);
        Log.Info($"ImGui.ImGui_ImplWin32_Init(), Window Handle: {_windowHandle:X}");

        _myWindowProc = new User32.WndProcDelegate(WndProcHandler);
        _originalWindowProc = User32.SetWindowLong(windowHandle, User32.GWL_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_myWindowProc));
    }

    private static void InitImGuiDX11(SwapChain swapChain)
    {
        _device = swapChain.GetDevice<Device>();
        _deviceContext = _device.ImmediateContext;

        using var backBuffer = swapChain.GetBackBuffer<Texture2D>(0);
        _renderTargetView = new RenderTargetView(_device, backBuffer);

        ImGuiDX11Impl.Init(_device.NativePointer, _deviceContext.NativePointer);
    }

    private static bool IsTargetWindowHandle(IntPtr windowHandle)
    {
        if (windowHandle != IntPtr.Zero)
            return windowHandle == _windowHandle || !DearImGuiInjectionCore.Initialized;
        return false;
    }

    private static IntPtr WndProcHandler(IntPtr hWnd, WindowMessage msg, IntPtr wParam, IntPtr lParam)
    {
        if (!DearImGuiInjectionCore.Initialized)
            return User32.CallWindowProc(_originalWindowProc, hWnd, msg, wParam, lParam);

        ImGuiWin32Impl.WndProcHandler(hWnd, msg, wParam, lParam);

        if (msg == WindowMessage.WM_KEYUP &&
            (VirtualKey)wParam == DearImGuiInjectionCore.CursorVisibility.Get())
        {
            if (DearImGuiInjectionCore.SaveOrRestoreCursorPosition.Get())
                SaveOrRestoreCursorPosition();

            DearImGuiInjectionCore.IsVisible = !DearImGuiInjectionCore.IsVisible;
        }

        if (!DearImGuiInjectionCore.IsVisible)
            return User32.CallWindowProc(_originalWindowProc, hWnd, msg, wParam, lParam);

        ImGuiIOPtr io = ImGui.GetIO();

        if (io.WantCaptureMouse)
        {
            switch (msg)
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

        if (io.WantCaptureKeyboard)
        {
            switch (msg)
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

        return User32.CallWindowProc(_originalWindowProc, hWnd, msg, wParam, lParam);
    }

    private static unsafe void SaveOrRestoreCursorPosition()
    {
        if (DearImGuiInjectionCore.IsVisible)
            User32.GetCursorPos(out _cursorCoords);
        else if (_cursorCoords.X + _cursorCoords.Y != 0)
            User32.SetCursorPos(_cursorCoords.X, _cursorCoords.Y);
    }

    private static void NewFrame()
    {
        ImGuiDX11Impl.NewFrame();
        ImGuiWin32Impl.NewFrame();
        ImGui.NewFrame();

        ImGui.ShowDemoWindow();

        if (DearImGuiInjectionCore.RenderAction != null)
        {
            foreach (Action item in DearImGuiInjectionCore.RenderAction.GetInvocationList())
            {
                try
                {
                    item();
                }
                catch (Exception e)
                {
                    Log.Error(e);
                }
            }
        }

        ImGui.EndFrame();
        ImGui.Render();
    }

    private static unsafe void OnPresent(SwapChain swapChain, uint syncInterval, uint flags)
    {
        var windowHandle = swapChain.Description.OutputHandle;

        if (!DearImGuiInjectionCore.Initialized)
        {
            DearImGuiInjectionCore.InitImGui();
            InitImGuiWin32(windowHandle);
            InitImGuiDX11(swapChain);
            DearImGuiInjectionCore.Initialized = true;
        }

        if (!IsTargetWindowHandle(windowHandle))
        {
            Log.Info($"[DX11] Discarding window handle {windowHandle:X} due to mismatch");
            return;
        }

        NewFrame();

        _deviceContext.OutputMerger.SetRenderTargets(_renderTargetView);

        ImGuiDX11Impl.RenderDrawData(ImGui.GetDrawData().Handle);
    }

    private static void PreResizeBuffers(SwapChain swapChain, uint bufferCount, uint width, uint height, Format newFormat, uint swapchainFlags)
    {
        if (!DearImGuiInjectionCore.Initialized)
            return;

        var windowHandle = swapChain.Description.OutputHandle;
        Log.Info($"[DX11 ResizeBuffers] Window Handle {windowHandle:X}");

        if (!IsTargetWindowHandle(windowHandle))
        {
            Log.Info($"[DX11 ResizeBuffers] Discarding window handle {windowHandle:X} due to mismatch");
            return;
        }

        _renderTargetView?.Dispose();
        _renderTargetView = null;

        ImGuiDX11Impl.InvalidateDeviceObjects();
    }

    private static void PostResizeBuffers(SwapChain swapChain, uint bufferCount, uint width, uint height, Format newFormat, uint swapchainFlags)
    {
        if (!DearImGuiInjectionCore.Initialized)
            return;

        var windowHandle = swapChain.Description.OutputHandle;
        if (!IsTargetWindowHandle(windowHandle))
        {
            Log.Info($"[DX11 ResizeBuffers] Discarding window handle {windowHandle:X} due to mismatch");
            return;
        }

        using var backBuffer = swapChain.GetBackBuffer<Texture2D>(0);
        _renderTargetView = new RenderTargetView(_device, backBuffer);

        // We invalidated device objects in PreResizeBuffers, so recreate them now.
        ImGuiDX11Impl.CreateDeviceObjects();
    }
}
