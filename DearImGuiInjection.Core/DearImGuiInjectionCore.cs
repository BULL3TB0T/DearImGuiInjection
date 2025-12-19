using DearImGuiInjection.Backends;
using DearImGuiInjection.Renderers;
using DearImGuiInjection.Windows;
using Hexa.NET.ImGui;
using HexaGen.Runtime;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("DearImGuiInjection.BepInEx5")]
[assembly: InternalsVisibleTo("DearImGuiInjection.BepInExIL2CPP")]
[assembly: InternalsVisibleTo("DearImGuiInjection.MelonIL2CPP")]

namespace DearImGuiInjection;

public static class DearImGuiInjectionCore
{
    public static bool Initialized { get; internal set; }

    public static string IniConfigPath { get; private set; }
    private const string IniFileName = "DearImGuiInjection_imgui.ini";

    public static string AssemblyFolderPath { get; internal set; }
    public static string AssetsFolderPath { get; internal set; }

    public static bool IsVisible { get; internal set; }

    internal static IConfigEntry<bool> UseDefaultTheme;
    internal const string UseDefaultThemeCategory = 
        "General";
    internal const string UseDefaultThemeKey =
        "Use Default Theme";
    internal const string UseDefaultThemeDescription = 
        "Uses ImGui's default theme instead of a custom theme.";
    internal const bool UseDefaultThemeDefaultValue = true;

    internal static IConfigEntry<VirtualKey> CursorVisibility;
    internal const string CursorVisibilityCategory = 
        "Keybinds";
    internal const string CursorVisibilityKey = 
        "Cursor Visibility";
    internal const string CursorVisibilityDescription = 
        "Key for switching the cursor visibility.";
    internal const VirtualKey CursorVisibilityDefaultValue = VirtualKey.VK_F2;

    internal static IConfigEntry<bool> SaveOrRestoreCursorPosition;
    internal const string SaveRestoreCursorPositionCategory = 
        "Input";
    internal const string SaveRestoreCursorPositionKey = 
        "Save Or Restore Cursor Position";
    internal const string SaveRestoreCursorPositionDescription =
        "Saves the mouse cursor position when the ImGui is closed and restores it when the ImGui is opened.";
    internal const bool SaveRestoreCursorPositionDefaultValue = true;
    
    public static event Action OnRender { add => Render += value; remove => Render -= value; }
    internal static Action Render;

    public static RendererKind RendererKind => RendererManager.Kind;

    private static ImGuiContextPtr Context;
    private static ImGuiIOPtr IO;

    internal static bool Init(string configPath, string assemblyFolderPath)
    {
        if (!RendererManager.Init())
            return false;
        IniConfigPath = Path.Combine(configPath, IniFileName);
        AssemblyFolderPath = assemblyFolderPath;
        AssetsFolderPath = Path.Combine(assemblyFolderPath, "Assets");
        return true;
    }

    internal static unsafe void InitImGui()
    {
        ImGui.InitApi(new NativeLibraryContext(Kernel32.LoadLibrary(Path.Combine(AssemblyFolderPath, "cimgui.dll"))));
        Context = ImGui.CreateContext();
        IO = ImGui.GetIO();
        IO.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        IO.ConfigFlags |= ImGuiConfigFlags.NavEnableGamepad;
        IO.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        IO.Handle->IniFilename = (byte*)Marshal.StringToHGlobalAnsi(IniConfigPath);
        if (UseDefaultTheme.Get())
            ImGui.StyleColorsDark();
        else
            DearImGuiInjectionTheme.Init();
        //IO.MouseDrawCursor = true;
    }

    internal static unsafe void Dispose()
    {
        if (!Initialized)
            return;
        Render = null;
        RendererManager.Shutdown();
        Marshal.FreeHGlobal((IntPtr)IO.Handle->IniFilename);
        ImGui.DestroyContext(Context);
        Context = null;
        Initialized = false;
    }
}