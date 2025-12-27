using DearImGuiInjection;
using DearImGuiInjection.Windows;
using Hexa.NET.ImGui;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace DearImGuiInjection.Backends;

internal static class ImGuiImplWin32
{
    private static int _nextId = 1;
    private static readonly Dictionary<int, Data> _map = new();

    private class Data
    {
        public IntPtr Hwnd;
        public IntPtr MouseHwnd;
        public int MouseTrackedArea;   // 0: not tracked, 1: client area, 2: non-client area
        public int MouseButtonsDown;
        public long Time;
        public long TicksPerSecond;
        public ImGuiMouseCursor LastMouseCursor;
        public uint KeyboardCodePage;
        public bool HasGamepad;
        public bool WantUpdateHasGamepad;
        public IntPtr XInputDLL;
        public XInputGetCapabilitiesDelegate XInputGetCapabilities;
        public XInputGetStateDelegate XInputGetState;
        public uint XInputPacketNumber;
    }

    // Backend data stored in io.BackendPlatformUserData to allow support for multiple Dear ImGui contexts
    // It is STRONGLY preferred that you use docking branch with multi-viewports (== single Dear ImGui context + multiple windows) instead of multiple Dear ImGui contexts.
    // FIXME: multi-context support is not well tested and probably dysfunctional in this backend.
    // FIXME: some shared resources (mouse cursor shape, gamepad) are mishandled when using multi-context.
    private unsafe static Data GetBackendData()
    {
        return GetBackendData(ImGui.GetIO());
    }
    private unsafe static Data GetBackendData(ImGuiIOPtr io)
    {
        if (io.BackendPlatformUserData == null)
            return null;
        int id = Marshal.ReadInt32((IntPtr)io.BackendPlatformUserData);
        return _map.TryGetValue(id, out var data) ? data : null;
    }

    private unsafe static Data InitBackendData()
    {
        var io = ImGui.GetIO();
        int id = _nextId++;
        Data data = new Data();
        _map[id] = data;
        IntPtr ptr = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(ptr, id);
        io.BackendPlatformUserData = (void*)ptr;
        return data;
    }

    private unsafe static void FreeBackendData()
    {
        var io = ImGui.GetIO();
        if (io.BackendPlatformUserData == null)
            return;
        IntPtr ptr = (IntPtr)io.BackendPlatformUserData;
        int id = Marshal.ReadInt32(ptr);
        _map.Remove(id);
        Marshal.FreeHGlobal(ptr);
        io.BackendPlatformUserData = null;
    }

    // Functions
    private unsafe static void UpdateKeyboardCodePage(ImGuiIOPtr io)
    {
        // Retrieve keyboard code page, required for handling of non-Unicode Windows.
        Data bd = GetBackendData(io);
        IntPtr keyboard_layout = User32.GetKeyboardLayout(0);
        uint keyboard_lcid = MAKELCID(HIWORD(keyboard_layout), User32.SORT_DEFAULT);
        if (User32.GetLocaleInfoA(keyboard_lcid, User32.LOCALE_RETURN_NUMBER | User32.LOCALE_IDEFAULTANSICODEPAGE, (IntPtr)bd.KeyboardCodePage, sizeof(uint)) == 0)
            bd.KeyboardCodePage = User32.CP_ACP; // Fallback to default ANSI code page when fails.
    }

    public unsafe static bool Init(IntPtr hwnd, bool platform_has_own_dc = false)
    {
        var io = ImGui.GetIO();
        Debug.Assert(io.BackendPlatformUserData == null, "Already initialized a platform backend!");

        if (!Kernel32.QueryPerformanceFrequency(out var perf_frequency))
            return false;
        if (!Kernel32.QueryPerformanceCounter(out var perf_counter))
            return false;

        // Setup backend capabilities flags
        Data bd = InitBackendData();
        io.BackendPlatformName = (byte*)Marshal.StringToHGlobalAnsi("imgui_impl_win32_c#");
        io.BackendFlags |= ImGuiBackendFlags.HasMouseCursors;         // We can honor GetMouseCursor() values (optional)
        io.BackendFlags |= ImGuiBackendFlags.HasSetMousePos;          // We can honor io.WantSetMousePos requests (optional, rarely used)

        bd.Hwnd = hwnd;
        bd.TicksPerSecond = perf_frequency;
        bd.Time = perf_counter;
        bd.LastMouseCursor = ImGuiMouseCursor.Count;
        UpdateKeyboardCodePage(io);

        // Set platform dependent data in viewport
        ImGuiViewportPtr main_viewport = ImGui.GetMainViewport();
        main_viewport.PlatformHandle = main_viewport.PlatformHandleRaw = (void*)bd.Hwnd;
        _ = platform_has_own_dc; // Used in 'docking' branch

        // Dynamically load XInput library
        bd.WantUpdateHasGamepad = true;
        var xinput_dll_names = new List<string>()
        {
            "xinput1_4.dll",   // Windows 8+
            "xinput1_3.dll",   // DirectX SDK
            "xinput9_1_0.dll", // Windows Vista, Windows 7
            "xinput1_2.dll",   // DirectX SDK
            "xinput1_1.dll"    // DirectX SDK
        };
        for (int n = 0; n < xinput_dll_names.Count; n++)
        {
            var dll = Kernel32.LoadLibrary(xinput_dll_names[n]);
            if (dll != IntPtr.Zero)
            {
                bd.XInputDLL = dll;
                bd.XInputGetCapabilities = Marshal.GetDelegateForFunctionPointer<XInputGetCapabilitiesDelegate>(Kernel32.GetProcAddress(dll, "XInputGetCapabilities"));
                bd.XInputGetState = Marshal.GetDelegateForFunctionPointer<XInputGetStateDelegate>(Kernel32.GetProcAddress(dll, "XInputGetState"));
                break;
            }
        }

        return true;
    }

    public unsafe static void Shutdown()
    {
        Data bd = GetBackendData();
        Debug.Assert(bd != null, "No platform backend to shutdown, or already shutdown?");
        var io = ImGui.GetIO();

        // Unload XInput library
        if (bd.XInputDLL != IntPtr.Zero)
            Kernel32.FreeLibrary(bd.XInputDLL);

        Marshal.FreeHGlobal((IntPtr)io.BackendPlatformName);
        io.BackendFlags &= ~(ImGuiBackendFlags.HasMouseCursors | ImGuiBackendFlags.HasSetMousePos | ImGuiBackendFlags.HasGamepad);
        FreeBackendData();
    }

    private static bool UpdateMouseCursor(ImGuiIOPtr io, ImGuiMouseCursor imgui_cursor)
    {
        if ((io.ConfigFlags & ImGuiConfigFlags.NoMouseCursorChange) != 0)
            return false;

        if (imgui_cursor == ImGuiMouseCursor.None || io.MouseDrawCursor)
        {
            // Hide OS mouse cursor if imgui is drawing it or if it wants no cursor
            User32.SetCursor(IntPtr.Zero);
        }
        else
        {
            // Show OS mouse cursor
            const int
                IDC_ARROW = 32512,
                IDC_IBEAM = 32513,
                IDC_SIZEALL = 32646,
                IDC_SIZEWE = 32644,
                IDC_SIZENS = 32645,
                IDC_SIZENESW = 32643,
                IDC_SIZENWSE = 32642,
                IDC_HAND = 32649,
                IDC_WAIT = 32514,
                IDC_APPSTARTING = 32650,
                IDC_NO = 32648;
            var win32_cursor = IDC_ARROW;
            switch (imgui_cursor)
            {
                case ImGuiMouseCursor.Arrow: win32_cursor = IDC_ARROW; break;
                case ImGuiMouseCursor.TextInput: win32_cursor = IDC_IBEAM; break;
                case ImGuiMouseCursor.ResizeAll: win32_cursor = IDC_SIZEALL; break;
                case ImGuiMouseCursor.ResizeEw: win32_cursor = IDC_SIZEWE; break;
                case ImGuiMouseCursor.ResizeNs: win32_cursor = IDC_SIZENS; break;
                case ImGuiMouseCursor.ResizeNesw: win32_cursor = IDC_SIZENESW; break;
                case ImGuiMouseCursor.ResizeNwse: win32_cursor = IDC_SIZENWSE; break;
                case ImGuiMouseCursor.Hand: win32_cursor = IDC_HAND; break;
                case ImGuiMouseCursor.Wait: win32_cursor = IDC_WAIT; break;
                case ImGuiMouseCursor.Progress: win32_cursor = IDC_APPSTARTING; break;
                case ImGuiMouseCursor.NotAllowed: win32_cursor = IDC_NO; break;
            }
            User32.SetCursor(User32.LoadCursor(IntPtr.Zero, win32_cursor));
        }
        return true;
    }

    private static bool IsVkDown(VirtualKey vk) => (User32.GetKeyState(vk) & 0x8000) != 0;

    private static void AddKeyEvent(ImGuiIOPtr io, ImGuiKey key, bool down, VirtualKey native_keycode, int native_scancode = -1)
    {
        io.AddKeyEvent(key, down);
        io.SetKeyEventNativeData(key, (int)native_keycode, native_scancode); // To support legacy indexing (<1.87 user code)
    }

    private static void ProcessKeyEventsWorkarounds(ImGuiIOPtr io)
    {
        // Left & right Shift keys: when both are pressed together, Windows tend to not generate the WM_KEYUP event for the first released one.
        if (ImGui.IsKeyDown(ImGuiKey.LeftShift) && !IsVkDown(VirtualKey.VK_LSHIFT))
            AddKeyEvent(io, ImGuiKey.LeftShift, false, VirtualKey.VK_LSHIFT);
        if (ImGui.IsKeyDown(ImGuiKey.RightShift) && !IsVkDown(VirtualKey.VK_RSHIFT))
            AddKeyEvent(io, ImGuiKey.RightShift, false, VirtualKey.VK_RSHIFT);

        // Sometimes WM_KEYUP for Win key is not passed down to the app (e.g. for Win+V on some setups, according to GLFW).
        if (ImGui.IsKeyDown(ImGuiKey.LeftSuper) && !IsVkDown(VirtualKey.VK_LWIN))
            AddKeyEvent(io, ImGuiKey.LeftSuper, false, VirtualKey.VK_LWIN);
        if (ImGui.IsKeyDown(ImGuiKey.RightSuper) && !IsVkDown(VirtualKey.VK_RWIN))
            AddKeyEvent(io, ImGuiKey.RightSuper, false, VirtualKey.VK_RWIN);
    }

    private static void UpdateKeyModifiers(ImGuiIOPtr io)
    {
        io.AddKeyEvent(ImGuiKey.ModCtrl, IsVkDown(VirtualKey.VK_CONTROL));
        io.AddKeyEvent(ImGuiKey.ModShift, IsVkDown(VirtualKey.VK_SHIFT));
        io.AddKeyEvent(ImGuiKey.ModAlt, IsVkDown(VirtualKey.VK_MENU));
        io.AddKeyEvent(ImGuiKey.ModSuper, IsVkDown(VirtualKey.VK_LWIN) || IsVkDown(VirtualKey.VK_RWIN));
    }

    private static void UpdateMouseData(ImGuiIOPtr io)
    {
        Data bd = GetBackendData(io);
        Debug.Assert(bd.Hwnd != IntPtr.Zero);

        IntPtr focused_window = User32.GetForegroundWindow();
        bool is_app_focused = focused_window == bd.Hwnd;
        if (is_app_focused)
        {
            // (Optional) Set OS mouse position from Dear ImGui if requested (rarely used, only when ImGuiConfigFlags_NavEnableSetMousePos is enabled by user)
            if (io.WantSetMousePos)
            {
                POINT pos = new POINT((int)io.MousePos.X, (int)io.MousePos.Y);
                if (User32.ClientToScreen(bd.Hwnd, ref pos))
                    User32.SetCursorPos(pos.X, pos.Y);
            }

            // (Optional) Fallback to provide mouse position when focused (WM_MOUSEMOVE already provides this when hovered or captured)
            // This also fills a short gap when clicking non-client area: WM_NCMOUSELEAVE -> modal OS move -> gap -> WM_NCMOUSEMOVE
            if (!io.WantSetMousePos && bd.MouseTrackedArea == 0)
            {
                POINT pos;
                if (User32.GetCursorPos(out pos) && User32.ScreenToClient(bd.Hwnd, ref pos))
                    io.AddMousePosEvent(pos.X, pos.Y);
            }
        }
    }

    // Gamepad navigation mapping
    private static void UpdateGamepads(ImGuiIOPtr io)
    {
        if ((io.ConfigFlags & ImGuiConfigFlags.NavEnableGamepad) == 0)
            return;

        const uint ERROR_SUCCESS = 0;
        const uint XINPUT_FLAG_GAMEPAD = 1;
        const int XINPUT_GAMEPAD_TRIGGER_THRESHOLD = 30;
        const int XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE = 7849;

        Data bd = GetBackendData(io);

        // Calling XInputGetState() every frame on disconnected gamepads is unfortunately too slow.
        // Instead we refresh gamepad availability by calling XInputGetCapabilities() _only_ after receiving WM_DEVICECHANGE.
        if (bd.WantUpdateHasGamepad)
        {
            XINPUT_CAPABILITIES caps = default;
            bd.HasGamepad = bd.XInputGetCapabilities != null
                && (bd.XInputGetCapabilities(0, XINPUT_FLAG_GAMEPAD, out caps) == ERROR_SUCCESS);
            bd.WantUpdateHasGamepad = false;
        }

        io.BackendFlags &= ~ImGuiBackendFlags.HasGamepad;
        if (!bd.HasGamepad || bd.XInputGetState == null || bd.XInputGetState(0, out XINPUT_STATE xinput_state) != ERROR_SUCCESS)
            return;
        io.BackendFlags |= ImGuiBackendFlags.HasGamepad;
        XINPUT_GAMEPAD gamepad = xinput_state.Gamepad;
        if (bd.XInputPacketNumber != 0 && bd.XInputPacketNumber == xinput_state.dwPacketNumber)
            return;
        bd.XInputPacketNumber = xinput_state.dwPacketNumber;

        static float IM_SATURATE(float V) => V < 0.0f ? 0.0f : (V > 1.0f ? 1.0f : V);
        void MAP_BUTTON(ImGuiKey KEY_NO, XInputGamepad BUTTON_ENUM) => io.AddKeyEvent(KEY_NO, (gamepad.wButtons & (ushort)BUTTON_ENUM) != 0);
        void MAP_ANALOG(ImGuiKey KEY_NO, int VALUE, int V0, int V1) { float vn = (VALUE - V0) / (float)(V1 - V0); io.AddKeyAnalogEvent(KEY_NO, vn > 0.10f, IM_SATURATE(vn)); }
        MAP_BUTTON(ImGuiKey.GamepadStart, XInputGamepad.START);
        MAP_BUTTON(ImGuiKey.GamepadBack, XInputGamepad.BACK);
        MAP_BUTTON(ImGuiKey.GamepadFaceLeft, XInputGamepad.X);
        MAP_BUTTON(ImGuiKey.GamepadFaceRight, XInputGamepad.B);
        MAP_BUTTON(ImGuiKey.GamepadFaceUp, XInputGamepad.Y);
        MAP_BUTTON(ImGuiKey.GamepadFaceDown, XInputGamepad.A);
        MAP_BUTTON(ImGuiKey.GamepadDpadLeft, XInputGamepad.DPAD_LEFT);
        MAP_BUTTON(ImGuiKey.GamepadDpadRight, XInputGamepad.DPAD_RIGHT);
        MAP_BUTTON(ImGuiKey.GamepadDpadUp, XInputGamepad.DPAD_UP);
        MAP_BUTTON(ImGuiKey.GamepadDpadDown, XInputGamepad.DPAD_DOWN);
        MAP_BUTTON(ImGuiKey.GamepadL1, XInputGamepad.LEFT_SHOULDER);
        MAP_BUTTON(ImGuiKey.GamepadR1, XInputGamepad.RIGHT_SHOULDER);
        MAP_ANALOG(ImGuiKey.GamepadL2, gamepad.bLeftTrigger, XINPUT_GAMEPAD_TRIGGER_THRESHOLD, 255);
        MAP_ANALOG(ImGuiKey.GamepadR2, gamepad.bRightTrigger, XINPUT_GAMEPAD_TRIGGER_THRESHOLD, 255);
        MAP_BUTTON(ImGuiKey.GamepadL3, XInputGamepad.LEFT_THUMB);
        MAP_BUTTON(ImGuiKey.GamepadR3, XInputGamepad.RIGHT_THUMB);
        MAP_ANALOG(ImGuiKey.GamepadLStickLeft, gamepad.sThumbLX, -XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE, -32768);
        MAP_ANALOG(ImGuiKey.GamepadLStickRight, gamepad.sThumbLX, +XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE, +32767);
        MAP_ANALOG(ImGuiKey.GamepadLStickUp, gamepad.sThumbLY, +XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE, +32767);
        MAP_ANALOG(ImGuiKey.GamepadLStickDown, gamepad.sThumbLY, -XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE, -32768);
        MAP_ANALOG(ImGuiKey.GamepadRStickLeft, gamepad.sThumbRX, -XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE, -32768);
        MAP_ANALOG(ImGuiKey.GamepadRStickRight, gamepad.sThumbRX, +XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE, +32767);
        MAP_ANALOG(ImGuiKey.GamepadRStickUp, gamepad.sThumbRY, +XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE, +32767);
        MAP_ANALOG(ImGuiKey.GamepadRStickDown, gamepad.sThumbRY, -XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE, -32768);
    }

    public static void NewFrame()
    {
        Data bd = GetBackendData();
        Debug.Assert(bd != null, "Context or backend not initialized? Did you call ImGui_ImplWin32_Init()?");
        var io = ImGui.GetIO();

        // Setup display size (every frame to accommodate for window resizing)
        User32.GetClientRect(bd.Hwnd, out var rect);
        io.DisplaySize = new Vector2(rect.Right - rect.Left, rect.Bottom - rect.Top);

        // Setup time step
        Kernel32.QueryPerformanceCounter(out var current_time);
        io.DeltaTime = (float)(current_time - bd.Time) / bd.TicksPerSecond;
        bd.Time = current_time;

        // Update OS mouse position
        UpdateMouseData(io);

        // Process workarounds for known Windows key handling issues
        ProcessKeyEventsWorkarounds(io);

        // Update OS mouse cursor with the cursor requested by imgui
        ImGuiMouseCursor mouse_cursor = io.MouseDrawCursor ? ImGuiMouseCursor.None : ImGui.GetMouseCursor();
        if (bd.LastMouseCursor != mouse_cursor)
        {
            bd.LastMouseCursor = mouse_cursor;
            UpdateMouseCursor(io, mouse_cursor);
        }

        // Update game controllers (if enabled and available)
        UpdateGamepads(io);
    }

    // Map VK_xxx to ImGuiKey_xxx.
    private static ImGuiKey KeyEventToImGuiKey(VirtualKey wParam, IntPtr lParam)
    {
        const int KF_EXTENDED = 0x0100;
        // There is no distinct VK_xxx for keypad enter, instead it is VK_RETURN + KF_EXTENDED.
        if ((wParam == VirtualKey.VK_RETURN) && ((HIWORD(lParam) & KF_EXTENDED) != 0))
            return ImGuiKey.KeypadEnter;

        int scancode = (int)LOBYTE(HIWORD(lParam));
        //Log.Debug(string.Format("scancode %3d, keycode = 0x%02X\n", scancode, wParam));
        switch (wParam)
        {
            case VirtualKey.VK_TAB: return ImGuiKey.Tab;
            case VirtualKey.VK_LEFT: return ImGuiKey.LeftArrow;
            case VirtualKey.VK_RIGHT: return ImGuiKey.RightArrow;
            case VirtualKey.VK_UP: return ImGuiKey.UpArrow;
            case VirtualKey.VK_DOWN: return ImGuiKey.DownArrow;
            case VirtualKey.VK_PRIOR: return ImGuiKey.PageUp;
            case VirtualKey.VK_NEXT: return ImGuiKey.PageDown;
            case VirtualKey.VK_HOME: return ImGuiKey.Home;
            case VirtualKey.VK_END: return ImGuiKey.End;
            case VirtualKey.VK_INSERT: return ImGuiKey.Insert;
            case VirtualKey.VK_DELETE: return ImGuiKey.Delete;
            case VirtualKey.VK_BACK: return ImGuiKey.Backspace;
            case VirtualKey.VK_SPACE: return ImGuiKey.Space;
            case VirtualKey.VK_RETURN: return ImGuiKey.Enter;
            case VirtualKey.VK_ESCAPE: return ImGuiKey.Escape;
            //case VirtualKey.VK_OEM_7: return ImGuiKey.Apostrophe;
            case VirtualKey.VK_OEM_COMMA: return ImGuiKey.Comma;
            //case VirtualKey.VK_OEM_MINUS: return ImGuiKey.Minus;
            case VirtualKey.VK_OEM_PERIOD: return ImGuiKey.Period;
            //case VirtualKey.VK_OEM_2: return ImGuiKey.Slash;
            //case VirtualKey.VK_OEM_1: return ImGuiKey.Semicolon;
            //case VirtualKey.VK_OEM_PLUS: return ImGuiKey.Equal;
            //case VirtualKey.VK_OEM_4: return ImGuiKey.LeftBracket;
            //case VirtualKey.VK_OEM_5: return ImGuiKey.Backslash;
            //case VirtualKey.VK_OEM_6: return ImGuiKey.RightBracket;
            //case VirtualKey.VK_OEM_3: return ImGuiKey.GraveAccent;
            case VirtualKey.VK_CAPITAL: return ImGuiKey.CapsLock;
            case VirtualKey.VK_SCROLL: return ImGuiKey.ScrollLock;
            case VirtualKey.VK_NUMLOCK: return ImGuiKey.NumLock;
            case VirtualKey.VK_SNAPSHOT: return ImGuiKey.PrintScreen;
            case VirtualKey.VK_PAUSE: return ImGuiKey.Pause;
            case VirtualKey.VK_NUMPAD0: return ImGuiKey.Keypad0;
            case VirtualKey.VK_NUMPAD1: return ImGuiKey.Keypad1;
            case VirtualKey.VK_NUMPAD2: return ImGuiKey.Keypad2;
            case VirtualKey.VK_NUMPAD3: return ImGuiKey.Keypad3;
            case VirtualKey.VK_NUMPAD4: return ImGuiKey.Keypad4;
            case VirtualKey.VK_NUMPAD5: return ImGuiKey.Keypad5;
            case VirtualKey.VK_NUMPAD6: return ImGuiKey.Keypad6;
            case VirtualKey.VK_NUMPAD7: return ImGuiKey.Keypad7;
            case VirtualKey.VK_NUMPAD8: return ImGuiKey.Keypad8;
            case VirtualKey.VK_NUMPAD9: return ImGuiKey.Keypad9;
            case VirtualKey.VK_DECIMAL: return ImGuiKey.KeypadDecimal;
            case VirtualKey.VK_DIVIDE: return ImGuiKey.KeypadDivide;
            case VirtualKey.VK_MULTIPLY: return ImGuiKey.KeypadMultiply;
            case VirtualKey.VK_SUBTRACT: return ImGuiKey.KeypadSubtract;
            case VirtualKey.VK_ADD: return ImGuiKey.KeypadAdd;
            case VirtualKey.VK_LSHIFT: return ImGuiKey.LeftShift;
            case VirtualKey.VK_LCONTROL: return ImGuiKey.LeftCtrl;
            case VirtualKey.VK_LMENU: return ImGuiKey.LeftAlt;
            case VirtualKey.VK_LWIN: return ImGuiKey.LeftSuper;
            case VirtualKey.VK_RSHIFT: return ImGuiKey.RightShift;
            case VirtualKey.VK_RCONTROL: return ImGuiKey.RightCtrl;
            case VirtualKey.VK_RMENU: return ImGuiKey.RightAlt;
            case VirtualKey.VK_RWIN: return ImGuiKey.RightSuper;
            case VirtualKey.VK_APPS: return ImGuiKey.Menu;
            case VirtualKey.VK_0: return ImGuiKey.Key0;
            case VirtualKey.VK_1: return ImGuiKey.Key1;
            case VirtualKey.VK_2: return ImGuiKey.Key2;
            case VirtualKey.VK_3: return ImGuiKey.Key3;
            case VirtualKey.VK_4: return ImGuiKey.Key4;
            case VirtualKey.VK_5: return ImGuiKey.Key5;
            case VirtualKey.VK_6: return ImGuiKey.Key6;
            case VirtualKey.VK_7: return ImGuiKey.Key7;
            case VirtualKey.VK_8: return ImGuiKey.Key8;
            case VirtualKey.VK_9: return ImGuiKey.Key9;
            case VirtualKey.VK_A: return ImGuiKey.A;
            case VirtualKey.VK_B: return ImGuiKey.B;
            case VirtualKey.VK_C: return ImGuiKey.C;
            case VirtualKey.VK_D: return ImGuiKey.D;
            case VirtualKey.VK_E: return ImGuiKey.E;
            case VirtualKey.VK_F: return ImGuiKey.F;
            case VirtualKey.VK_G: return ImGuiKey.G;
            case VirtualKey.VK_H: return ImGuiKey.H;
            case VirtualKey.VK_I: return ImGuiKey.I;
            case VirtualKey.VK_J: return ImGuiKey.J;
            case VirtualKey.VK_K: return ImGuiKey.K;
            case VirtualKey.VK_L: return ImGuiKey.L;
            case VirtualKey.VK_M: return ImGuiKey.M;
            case VirtualKey.VK_N: return ImGuiKey.N;
            case VirtualKey.VK_O: return ImGuiKey.O;
            case VirtualKey.VK_P: return ImGuiKey.P;
            case VirtualKey.VK_Q: return ImGuiKey.Q;
            case VirtualKey.VK_R: return ImGuiKey.R;
            case VirtualKey.VK_S: return ImGuiKey.S;
            case VirtualKey.VK_T: return ImGuiKey.T;
            case VirtualKey.VK_U: return ImGuiKey.U;
            case VirtualKey.VK_V: return ImGuiKey.V;
            case VirtualKey.VK_W: return ImGuiKey.W;
            case VirtualKey.VK_X: return ImGuiKey.X;
            case VirtualKey.VK_Y: return ImGuiKey.Y;
            case VirtualKey.VK_Z: return ImGuiKey.Z;
            case VirtualKey.VK_F1: return ImGuiKey.F1;
            case VirtualKey.VK_F2: return ImGuiKey.F2;
            case VirtualKey.VK_F3: return ImGuiKey.F3;
            case VirtualKey.VK_F4: return ImGuiKey.F4;
            case VirtualKey.VK_F5: return ImGuiKey.F5;
            case VirtualKey.VK_F6: return ImGuiKey.F6;
            case VirtualKey.VK_F7: return ImGuiKey.F7;
            case VirtualKey.VK_F8: return ImGuiKey.F8;
            case VirtualKey.VK_F9: return ImGuiKey.F9;
            case VirtualKey.VK_F10: return ImGuiKey.F10;
            case VirtualKey.VK_F11: return ImGuiKey.F11;
            case VirtualKey.VK_F12: return ImGuiKey.F12;
            case VirtualKey.VK_F13: return ImGuiKey.F13;
            case VirtualKey.VK_F14: return ImGuiKey.F14;
            case VirtualKey.VK_F15: return ImGuiKey.F15;
            case VirtualKey.VK_F16: return ImGuiKey.F16;
            case VirtualKey.VK_F17: return ImGuiKey.F17;
            case VirtualKey.VK_F18: return ImGuiKey.F18;
            case VirtualKey.VK_F19: return ImGuiKey.F19;
            case VirtualKey.VK_F20: return ImGuiKey.F20;
            case VirtualKey.VK_F21: return ImGuiKey.F21;
            case VirtualKey.VK_F22: return ImGuiKey.F22;
            case VirtualKey.VK_F23: return ImGuiKey.F23;
            case VirtualKey.VK_F24: return ImGuiKey.F24;
            case VirtualKey.VK_BROWSER_BACK: return ImGuiKey.AppBack;
            case VirtualKey.VK_BROWSER_FORWARD: return ImGuiKey.AppForward;
            default: break;
        }

        // Fallback to scancode
        // https://handmade.network/forums/t/2011-keyboard_inputs_-_scancodes,_raw_input,_text_input,_key_names
        switch (scancode)
        {
            case 41: return ImGuiKey.GraveAccent;  // VK_OEM_8 in EN-UK, VK_OEM_3 in EN-US, VK_OEM_7 in FR, VK_OEM_5 in DE, etc.
            case 12: return ImGuiKey.Minus;
            case 13: return ImGuiKey.Equal;
            case 26: return ImGuiKey.LeftBracket;
            case 27: return ImGuiKey.RightBracket;
            case 86: return ImGuiKey.Oem102;
            case 43: return ImGuiKey.Backslash;
            case 39: return ImGuiKey.Semicolon;
            case 40: return ImGuiKey.Apostrophe;
            case 51: return ImGuiKey.Comma;
            case 52: return ImGuiKey.Period;
            case 53: return ImGuiKey.Slash;
            default: break;
        }

        return ImGuiKey.None;
    }

    // Helper to obtain the source of mouse messages.
    // See https://learn.microsoft.com/en-us/windows/win32/tablet/system-events-and-mouse-messages
    // Prefer to call this at the top of the message handler to avoid the possibility of other Win32 calls interfering with this.
    private static ImGuiMouseSource GetMouseSourceFromMessageExtraInfo()
    {
        var extra_info = (uint)User32.GetMessageExtraInfo();
        if ((extra_info & 0xFFFFFF80) == 0xFF515700)
            return ImGuiMouseSource.Pen;
        if ((extra_info & 0xFFFFFF80) == 0xFF515780)
            return ImGuiMouseSource.TouchScreen;
        return ImGuiMouseSource.Mouse;
    }

    private static uint MAKELCID(ushort lgid, uint srtid) => unchecked((uint)(((uint)(ushort)srtid << 16) | (uint)lgid));
    private static int GET_X_LPARAM(IntPtr lp) => unchecked((short)(long)lp);
    private static int GET_Y_LPARAM(IntPtr lp) => unchecked((short)((long)lp >> 16));

    private static ushort HIWORD(IntPtr dwValue) => unchecked((ushort)((long)dwValue >> 16));
    private static ushort HIWORD(UIntPtr dwValue) => unchecked((ushort)((ulong)dwValue >> 16));

    private static ushort LOWORD(IntPtr dwValue) => unchecked((ushort)(long)dwValue);

    private static ushort GET_XBUTTON_WPARAM(IntPtr wParam) => HIWORD(wParam);

    private static int GET_WHEEL_DELTA_WPARAM(IntPtr wParam) => (short)HIWORD(wParam);

    private static byte LOBYTE(ushort wValue) => (byte)(wValue & 0xff);

    // This version is in theory thread-safe in the sense that no path should access ImGui::GetCurrentContext().
    public unsafe static IntPtr WndProcHandler(IntPtr hwnd, WindowMessage msg, IntPtr wParam, IntPtr lParam, ImGuiIOPtr io)
    {
        Data bd = GetBackendData(io);
        if (bd == null)
            return IntPtr.Zero;
        const int DBT_DEVNODES_CHANGED = 0x0007;
        const int XBUTTON1 = 1;
        const int WHEEL_DELTA = 120;
        const int HTCLIENT = 1;
        switch (msg)
        {
            case WindowMessage.WM_MOUSEMOVE:
            case WindowMessage.WM_NCMOUSEMOVE:
                {
                    // We need to call TrackMouseEvent in order to receive WM_MOUSELEAVE events
                    ImGuiMouseSource mouse_source = GetMouseSourceFromMessageExtraInfo();
                    int area = msg == WindowMessage.WM_MOUSEMOVE ? 1 : 2;
                    bd.MouseHwnd = hwnd;
                    if (bd.MouseTrackedArea != area)
                    {
                        TRACKMOUSEEVENT tme_cancel = new(TMEFlags.TME_CANCEL, hwnd, 0);
                        TRACKMOUSEEVENT tme_track = new(area == 2 ? TMEFlags.TME_LEAVE | TMEFlags.TME_NONCLIENT : TMEFlags.TME_LEAVE, hwnd, 0);
                        if (bd.MouseTrackedArea != 0)
                            User32.TrackMouseEvent(ref tme_cancel);
                        User32.TrackMouseEvent(ref tme_track);
                        bd.MouseTrackedArea = area;
                    }
                    POINT mouse_pos = new(GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam));
                    if (msg == WindowMessage.WM_NCMOUSEMOVE && !User32.ScreenToClient(hwnd, ref mouse_pos)) // WM_NCMOUSEMOVE are provided in absolute coordinates.
                        break;
                    io.AddMouseSourceEvent(mouse_source);
                    io.AddMousePosEvent(mouse_pos.X, mouse_pos.Y);
                    return IntPtr.Zero;
                }
            case WindowMessage.WM_MOUSELEAVE:
            case WindowMessage.WM_NCMOUSELEAVE:
                {
                    int area = msg == WindowMessage.WM_MOUSELEAVE ? 1 : 2;
                    if (bd.MouseTrackedArea == area)
                    {
                        if (bd.MouseHwnd == hwnd)
                            bd.MouseHwnd = IntPtr.Zero;
                        bd.MouseTrackedArea = 0;
                        io.AddMousePosEvent(-float.MaxValue, -float.MaxValue);
                    }
                    return IntPtr.Zero;
                }
            case WindowMessage.WM_DESTROY:
                if (bd.MouseHwnd == hwnd && bd.MouseTrackedArea != 0)
                {
                    TRACKMOUSEEVENT tme_cancel = new(TMEFlags.TME_CANCEL, hwnd, 0);
                    User32.TrackMouseEvent(ref tme_cancel);
                    bd.MouseHwnd = IntPtr.Zero;
                    bd.MouseTrackedArea = 0;
                    io.AddMousePosEvent(-float.MaxValue, -float.MaxValue);
                }
                return IntPtr.Zero;
            case WindowMessage.WM_LBUTTONDOWN:
            case WindowMessage.WM_LBUTTONDBLCLK:
            case WindowMessage.WM_RBUTTONDOWN:
            case WindowMessage.WM_RBUTTONDBLCLK:
            case WindowMessage.WM_MBUTTONDOWN:
            case WindowMessage.WM_MBUTTONDBLCLK:
            case WindowMessage.WM_XBUTTONDOWN:
            case WindowMessage.WM_XBUTTONDBLCLK:
                {
                    ImGuiMouseSource mouse_source = GetMouseSourceFromMessageExtraInfo();
                    int button = 0;
                    if (msg == WindowMessage.WM_LBUTTONDOWN || msg == WindowMessage.WM_LBUTTONDBLCLK) { button = 0; }
                    if (msg == WindowMessage.WM_RBUTTONDOWN || msg == WindowMessage.WM_RBUTTONDBLCLK) { button = 1; }
                    if (msg == WindowMessage.WM_MBUTTONDOWN || msg == WindowMessage.WM_MBUTTONDBLCLK) { button = 2; }
                    if (msg == WindowMessage.WM_XBUTTONDOWN || msg == WindowMessage.WM_XBUTTONDBLCLK) { button = GET_XBUTTON_WPARAM(wParam) == XBUTTON1 ? 3 : 4; }
                    IntPtr hwnd_with_capture = User32.GetCapture();
                    if (bd.MouseButtonsDown != 0 && hwnd_with_capture != hwnd) // Did we externally lost capture?
                        bd.MouseButtonsDown = 0;
                    if (bd.MouseButtonsDown == 0 && User32.GetCapture() == IntPtr.Zero)
                        User32.SetCapture(hwnd); // Allow us to read mouse coordinates when dragging mouse outside of our window bounds.
                    bd.MouseButtonsDown |= 1 << button;
                    io.AddMouseSourceEvent(mouse_source);
                    io.AddMouseButtonEvent(button, true);
                    return IntPtr.Zero;
                }
            case WindowMessage.WM_LBUTTONUP:
            case WindowMessage.WM_RBUTTONUP:
            case WindowMessage.WM_MBUTTONUP:
            case WindowMessage.WM_XBUTTONUP:
                {
                    ImGuiMouseSource mouse_source = GetMouseSourceFromMessageExtraInfo();
                    int button = 0;
                    if (msg == WindowMessage.WM_LBUTTONUP) { button = 0; }
                    if (msg == WindowMessage.WM_RBUTTONUP) { button = 1; }
                    if (msg == WindowMessage.WM_MBUTTONUP) { button = 2; }
                    if (msg == WindowMessage.WM_XBUTTONUP) { button = GET_XBUTTON_WPARAM(wParam) == XBUTTON1 ? 3 : 4; }
                    bd.MouseButtonsDown &= ~(1 << button);
                    if (bd.MouseButtonsDown == 0 && User32.GetCapture() == hwnd)
                        User32.ReleaseCapture();
                    io.AddMouseSourceEvent(mouse_source);
                    io.AddMouseButtonEvent(button, false);
                    return IntPtr.Zero;
                }
            case WindowMessage.WM_MOUSEWHEEL:
                io.AddMouseWheelEvent(0.0f, GET_WHEEL_DELTA_WPARAM(wParam) / (float)WHEEL_DELTA);
                return IntPtr.Zero;
            case WindowMessage.WM_MOUSEHWHEEL:
                io.AddMouseWheelEvent(-(float)GET_WHEEL_DELTA_WPARAM(wParam) / WHEEL_DELTA, 0.0f);
                return IntPtr.Zero;
            case WindowMessage.WM_KEYDOWN:
            case WindowMessage.WM_KEYUP:
            case WindowMessage.WM_SYSKEYDOWN:
            case WindowMessage.WM_SYSKEYUP:
                {
                    bool is_key_down = msg == WindowMessage.WM_KEYDOWN || msg == WindowMessage.WM_SYSKEYDOWN;
                    if ((int)wParam < 256)
                    {
                        // Submit modifiers
                        UpdateKeyModifiers(io);

                        // Obtain virtual key code
                        ImGuiKey key = KeyEventToImGuiKey((VirtualKey)wParam, lParam);
                        VirtualKey vk = (VirtualKey)wParam;
                        int scancode = (int)LOBYTE(HIWORD(lParam));

                        // Special behavior for VK_SNAPSHOT / ImGuiKey_PrintScreen as Windows doesn't emit the key down event.
                        if (key == ImGuiKey.PrintScreen && !is_key_down)
                            AddKeyEvent(io, key, true, vk, scancode);

                        // Submit key event
                        if (key != ImGuiKey.None)
                            AddKeyEvent(io, key, is_key_down, vk, scancode);

                        // Submit individual left/right modifier events
                        if (vk == VirtualKey.VK_SHIFT)
                        {
                            // Important: Shift keys tend to get stuck when pressed together, missing key-up events are corrected in ImGui_ImplWin32_ProcessKeyEventsWorkarounds()
                            if (IsVkDown(VirtualKey.VK_LSHIFT) == is_key_down) { AddKeyEvent(io, ImGuiKey.LeftShift, is_key_down, VirtualKey.VK_LSHIFT, scancode); }
                            if (IsVkDown(VirtualKey.VK_RSHIFT) == is_key_down) { AddKeyEvent(io, ImGuiKey.RightShift, is_key_down, VirtualKey.VK_RSHIFT, scancode); }
                        }
                        else if (vk == VirtualKey.VK_CONTROL)
                        {
                            if (IsVkDown(VirtualKey.VK_LCONTROL) == is_key_down) { AddKeyEvent(io, ImGuiKey.LeftCtrl, is_key_down, VirtualKey.VK_LCONTROL, scancode); }
                            if (IsVkDown(VirtualKey.VK_RCONTROL) == is_key_down) { AddKeyEvent(io, ImGuiKey.RightCtrl, is_key_down, VirtualKey.VK_RCONTROL, scancode); }
                        }
                        else if (vk == VirtualKey.VK_MENU)
                        {
                            if (IsVkDown(VirtualKey.VK_LMENU) == is_key_down) { AddKeyEvent(io, ImGuiKey.LeftAlt, is_key_down, VirtualKey.VK_LMENU, scancode); }
                            if (IsVkDown(VirtualKey.VK_RMENU) == is_key_down) { AddKeyEvent(io, ImGuiKey.RightAlt, is_key_down, VirtualKey.VK_RMENU, scancode); }
                        }
                    }
                    return IntPtr.Zero;
                }
            case WindowMessage.WM_SETFOCUS:
            case WindowMessage.WM_KILLFOCUS:
                io.AddFocusEvent(msg == WindowMessage.WM_SETFOCUS);
                return IntPtr.Zero;
            case WindowMessage.WM_INPUTLANGCHANGE:
                UpdateKeyboardCodePage(io);
                return IntPtr.Zero;
            case WindowMessage.WM_CHAR:
                if (User32.IsWindowUnicode(hwnd))
                {
                    // You can also use ToAscii()+GetKeyboardState() to retrieve characters.
                    if ((int)wParam > 0 && (int)wParam < 0x10000)
                        io.AddInputCharacterUTF16((ushort)wParam);
                }
                else
                {
                    char wch = (char)0;
                    Kernel32.MultiByteToWideChar(bd.KeyboardCodePage, User32.MB_PRECOMPOSED, (byte*)&wParam, 1, &wch, 1);
                    io.AddInputCharacter(wch);
                }
                return IntPtr.Zero;
            case WindowMessage.WM_SETCURSOR:
                // This is required to restore cursor when transitioning from e.g resize borders to client area.
                if (LOWORD(lParam) == HTCLIENT && UpdateMouseCursor(io, bd.LastMouseCursor))
                    return (IntPtr)1;
                return IntPtr.Zero;
            case WindowMessage.WM_DEVICECHANGE:
                if ((uint)wParam == DBT_DEVNODES_CHANGED)
                    bd.WantUpdateHasGamepad = true;
                return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }


    //--------------------------------------------------------------------------------------------------------
    // DPI-related helpers (optional)
    //--------------------------------------------------------------------------------------------------------
    // - Use to enable DPI awareness without having to create an application manifest.
    // - Your own app may already do this via a manifest or explicit calls. This is mostly useful for our examples/ apps.
    // - In theory we could call simple functions from Windows SDK such as SetProcessDPIAware(), SetProcessDpiAwareness(), etc.
    //   but most of the functions provided by Microsoft require Windows 8.1/10+ SDK at compile time and Windows 8/10+ at runtime,
    //   neither we want to require the user to have. So we dynamically select and load those functions to avoid dependencies.
    //---------------------------------------------------------------------------------------------------------
    // This is the scheme successfully used by GLFW (from which we borrowed some of the code) and other apps aiming to be highly portable.
    // ImGui_ImplWin32_EnableDpiAwareness() is just a helper called by main.cpp, we don't call it automatically.
    // If you are trying to implement your own backend for your own engine, you may ignore that noise.
    //---------------------------------------------------------------------------------------------------------

    // Perform our own check with RtlVerifyVersionInfo() instead of using functions from <VersionHelpers.h> as they
    // require a manifest to be functional for checks above 8.1. See https://github.com/ocornut/imgui/issues/4200
    private unsafe static bool _IsWindowsVersionOrGreater(short major, short minor, short unused)
    {
        const uint VER_MAJORVERSION = 0x0000002;
        const uint VER_MINORVERSION = 0x0000001;
        const byte VER_GREATER_EQUAL = 3;

        var versionInfo = OSVERSIONINFOEX.Create();
        ulong conditionMask = 0;
        versionInfo.dwOSVersionInfoSize = Marshal.SizeOf<OSVERSIONINFOEX>();
        versionInfo.dwMajorVersion = major;
        versionInfo.dwMinorVersion = minor;
        Kernel32.VER_SET_CONDITION(ref conditionMask, VER_MAJORVERSION, VER_GREATER_EQUAL);
        Kernel32.VER_SET_CONDITION(ref conditionMask, VER_MINORVERSION, VER_GREATER_EQUAL);
        return Ntdll.RtlVerifyVersionInfo(&versionInfo, VER_MASK.VER_MAJORVERSION | VER_MASK.VER_MINORVERSION, (long)conditionMask) == 0 ? true : false;
    }

    private static bool _IsWindowsVistaOrGreater() => _IsWindowsVersionOrGreater((short)Kernel32.HiByte(0x0600), LOBYTE(0x0600), 0); // _WIN32_WINNT_VISTA
    private static bool _IsWindows8OrGreater() => _IsWindowsVersionOrGreater((short)Kernel32.HiByte(0x0602), LOBYTE(0x0602), 0); // _WIN32_WINNT_WIN8
    private static bool _IsWindows8Point1OrGreater() => _IsWindowsVersionOrGreater((short)Kernel32.HiByte(0x0603), LOBYTE(0x0603), 0); // _WIN32_WINNT_WINBLUE
    private static bool _IsWindows10OrGreater() => _IsWindowsVersionOrGreater((short)Kernel32.HiByte(0x0A00), LOBYTE(0x0A00), 0); // _WIN32_WINNT_WINTHRESHOLD / _WIN32_WINNT_WIN10

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    // Helper function to enable DPI awareness without setting up a manifest
    private static void EnableDpiAwareness()
    {
        if (_IsWindows10OrGreater())
        {
            User32.SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            return;
        }
        if (_IsWindows8Point1OrGreater())
        {
            ShCore.SetProcessDpiAwareness(PROCESS_DPI_AWARENESS.PROCESS_PER_MONITOR_DPI_AWARE);
            return;
        }

        User32.SetProcessDPIAware();
    }

    private unsafe static float GetDpiScaleForMonitor(void* monitor)
    {
        uint xdpi, ydpi = 96;
        if (_IsWindows8Point1OrGreater())
        {
            ShCore.GetDpiForMonitor((IntPtr)monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out xdpi, out ydpi);
            Debug.Assert(xdpi == ydpi);
            return xdpi / 96.0f;
        }

        const int LOGPIXELSX = 88;
        const int LOGPIXELSY = 90;

        var dc = User32.GetDC(IntPtr.Zero);
        xdpi = (uint)Gdi32.GetDeviceCaps(dc, LOGPIXELSX);
        ydpi = (uint)Gdi32.GetDeviceCaps(dc, LOGPIXELSY);
        Debug.Assert(xdpi == ydpi);
        User32.ReleaseDC(IntPtr.Zero, dc);
        return xdpi / 96.0f;
    }

    private unsafe static float GetDpiScaleForHwnd(void* hwnd)
    {
        const int MONITOR_DEFAULTTONEAREST = 2;
        var monitor = User32.MonitorFromWindow((IntPtr)hwnd, MONITOR_DEFAULTTONEAREST);
        return GetDpiScaleForMonitor((void*)monitor);
    }

    private unsafe static void EnableAlphaCompositing(void* hwnd)
    {
        if (!_IsWindowsVistaOrGreater())
            return;

        var hres = Dwmapi.DwmIsCompositionEnabled(out bool composition);
        if (hres != 0 || !composition)
            return;

        hres = Dwmapi.DwmGetColorizationColor(out var color, out var opaque);
        if (_IsWindows8OrGreater() || hres == 0 && !opaque)
        {
            var region = Gdi32.CreateRectRgn(0, 0, -1, -1);
            DwmBlurBehind bb = new(true);
            bb.Flags |= DwmBlurBehindFlags.Enable;
            bb.Flags |= DwmBlurBehindFlags.BlurRegion;
            bb.BlurRegion = region;
            Dwmapi.DwmEnableBlurBehindWindow((IntPtr)hwnd, ref bb);
            Gdi32.DeleteObject(region);
        }
        else
        {
            DwmBlurBehind bb = new(true);
            Dwmapi.DwmEnableBlurBehindWindow((IntPtr)hwnd, ref bb);
        }
    }
}
