using DearImGuiInjection.Backends;
using DearImGuiInjection.Renderers;
using DearImGuiInjection.Textures;
using DearImGuiInjection.Windows;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DearImGuiInjection.Handlers;

internal abstract class ImGuiHandler
{
    public bool IsInitialized;

    public IntPtr WindowHandle { get; private set; }
    private User32.WndProcDelegate WindowProc;
    private IntPtr OriginalWindowProc;

    public void Init(IntPtr windowHandle)
    {
        WindowHandle = windowHandle;
        WindowProc = new User32.WndProcDelegate((IntPtr hWnd, WindowMessage uMsg, IntPtr wParam, IntPtr lParam) =>
        {
            bool IsKeyUpMsg() => uMsg == WindowMessage.WM_KEYUP || uMsg == WindowMessage.WM_SYSKEYUP;
            bool IsMouseUpMsg() => uMsg == WindowMessage.WM_LBUTTONUP
                || uMsg == WindowMessage.WM_RBUTTONUP
                || uMsg == WindowMessage.WM_MBUTTONUP
                || uMsg == WindowMessage.WM_XBUTTONUP;
            bool IsKeyMsg() => uMsg == WindowMessage.WM_KEYDOWN
                || uMsg == WindowMessage.WM_KEYUP
                || uMsg == WindowMessage.WM_SYSKEYDOWN
                || uMsg == WindowMessage.WM_SYSKEYUP
                || uMsg == WindowMessage.WM_CHAR
                || uMsg == WindowMessage.WM_SYSCHAR
                || uMsg == WindowMessage.WM_IME_STARTCOMPOSITION
                || uMsg == WindowMessage.WM_IME_COMPOSITION
                || uMsg == WindowMessage.WM_IME_ENDCOMPOSITION;
            bool IsMouseMsg() => uMsg == WindowMessage.WM_MOUSEMOVE
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
            bool allowUpMessages = DearImGuiInjectionCore.AllowUpMessages.GetValue();
            IntPtr result = IntPtr.Zero;
            foreach (ImGuiModule module in DearImGuiInjectionCore.MultiContextCompositor.Modules)
            {
                var io = module.IO;
                IntPtr handlerResult = ImGuiImplWin32.WndProcHandler(hWnd, uMsg, wParam, lParam, io);
                bool modResult = false;
                try
                {
                    modResult = module.OnWndProc?.Invoke(hWnd, uMsg, wParam, lParam) ?? false;
                }
                catch (Exception e)
                {
                    Log.Error($"Module \"{module.Id}\" OnWndProc threw an exception: {e}");
                }
                if (result == IntPtr.Zero)
                {
                    if (handlerResult != IntPtr.Zero)
                        result = handlerResult;
                    if (modResult)
                        result = (IntPtr)1;
                    if (IsMouseMsg() && io.WantCaptureMouse && !(allowUpMessages && IsMouseUpMsg()))
                        result = (IntPtr)1;
                    if (IsKeyMsg() && io.WantCaptureKeyboard && !(allowUpMessages && IsKeyUpMsg()))
                        result = (IntPtr)1;
                }
            }
            if (result != IntPtr.Zero)
                return result;
            return User32.CallWindowProc(OriginalWindowProc, hWnd, uMsg, wParam, lParam);
        });
        OriginalWindowProc = User32.SetWindowLong(WindowHandle, User32.GWL_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(WindowProc));
    }

    public abstract void OnShutdown();

    public abstract void OnDispose();
    public void Dispose()
    {
        if (!IsInitialized)
            return;
        DearImGuiInjectionCore.TextureManager.Dispose();
        OnDispose();
        User32.SetWindowLong(WindowHandle, User32.GWL_WNDPROC, OriginalWindowProc);
        OriginalWindowProc = IntPtr.Zero;
        WindowProc = null;
        WindowHandle = IntPtr.Zero;
    }
}