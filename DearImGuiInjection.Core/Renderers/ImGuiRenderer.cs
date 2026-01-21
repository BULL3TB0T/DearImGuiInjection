using DearImGuiInjection.Backends;
using DearImGuiInjection.Windows;
using Hexa.NET.ImGui;
using System;
using System.Runtime.InteropServices;

namespace DearImGuiInjection.Renderers;

public enum RendererKind
{
    None,
    DX11,
    DX12,
    Vulkan,
    OpenGLES2,
    OpenGLES3,
    OpenGLCore
}

internal abstract class ImGuiRenderer
{
    private User32.WndProcDelegate _windowProc;
    public IntPtr WindowHandle { get; private set; }
    private IntPtr _currentWindowProc;
    private IntPtr _originalWindowProc;

    public abstract void Init();
    public abstract void Dispose();
    public abstract void Shutdown(bool isInitialized);

    internal bool CanAttachWindowHandle()
    {
        if (WindowHandle != IntPtr.Zero)
            return false;
        _windowProc = new User32.WndProcDelegate((IntPtr hWnd, WindowMessage uMsg, IntPtr wParam, IntPtr lParam) =>
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
                || uMsg == WindowMessage.WM_CHAR;
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
                || uMsg == WindowMessage.WM_MOUSEHWHEEL
                || uMsg == WindowMessage.WM_SETCURSOR;
            bool allowUpMessages = DearImGuiInjectionCore.AllowUpMessages.GetValue();
            IntPtr result = IntPtr.Zero;
            for (int i = 0; i < DearImGuiInjectionCore.MultiContextCompositor.ModulesMouseOwnerLast.Count; i++)
            {
                ImGuiModule module = DearImGuiInjectionCore.MultiContextCompositor.ModulesMouseOwnerLast[i];
                ImGuiIOPtr io = module.IO;
                IntPtr handlerResult = ImGuiImplWin32.WndProcHandler(hWnd, uMsg, wParam, lParam, io);
                bool modResult = false;
                try
                {
                    modResult = module.OnWndProcHandler?.Invoke(hWnd, uMsg, wParam, lParam) ?? false;
                }
                catch (Exception e)
                {
                    DearImGuiInjectionCore.DestroyModule(module.Id);
                    Log.Error($"Module \"{module.Id}\" OnWndProc threw an exception: {e}");
                    continue;
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
            IntPtr original = _originalWindowProc;
            if (uMsg == WindowMessage.WM_CLOSE || uMsg == WindowMessage.WM_DESTROY || uMsg == WindowMessage.WM_NCDESTROY)
            {
                IntPtr current = _currentWindowProc;
                if (User32.GetWindowLong(hWnd, User32.GWL_WNDPROC) == current)
                    User32.SetWindowLong(hWnd, User32.GWL_WNDPROC, original);
            }
            return User32.CallWindowProc(original, hWnd, uMsg, wParam, lParam);
        });
        WindowHandle = GetMainWindowHandle();
        _currentWindowProc = Marshal.GetFunctionPointerForDelegate(_windowProc);
        _originalWindowProc = User32.SetWindowLong(WindowHandle, User32.GWL_WNDPROC, _currentWindowProc);
        return true;
    }

    private static IntPtr GetMainWindowHandle()
    {
        uint pid = Kernel32.GetCurrentProcessId();
        IntPtr best = IntPtr.Zero;
        int bestArea = -1;
        User32.EnumWindows((hwnd, _) =>
        {
            User32.GetWindowThreadProcessId(hwnd, out uint wpid);
            if (wpid != pid)
                return true;
            if (!User32.IsWindowVisible(hwnd) || !User32.GetClientRect(hwnd, out RECT r))
                return true;
            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;
            if (w == 0 || h == 0)
                return true;
            int area = w * h;
            if (area > bestArea)
            {
                bestArea = area;
                best = hwnd;
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }
}