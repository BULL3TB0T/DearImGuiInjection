using DearImGuiInjection.Backends;
using DearImGuiInjection.Renderers;
using DearImGuiInjection.Textures;
using DearImGuiInjection.Windows;
using Hexa.NET.ImGui;
using HexaGen.Runtime;
using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("DearImGuiInjection.BepInEx5")]
[assembly: InternalsVisibleTo("DearImGuiInjection.BepInEx6")]
[assembly: InternalsVisibleTo("DearImGuiInjection.BepInExIL2CPP")]

namespace DearImGuiInjection;

public static class DearImGuiInjectionCore
{
    internal const string BackendVersion = "unity_hexa_net (v2.2.11-pre)";

    public static ITextureManager TextureManager;
    public static ImGuiMultiContextCompositor MultiContextCompositor;

    public static IConfigEntry<bool> ShowDemoWindow;
    public static IConfigEntry<bool> EnableDpiAwareness;
    public static IConfigEntry<bool> AllowUpMessages;
    public static IConfigEntry<bool> MouseDrawCursor;

    private static ImGuiRenderer Renderer;
    private static RendererKind _rendererKind = RendererKind.None;
    public static RendererKind RendererKind => _rendererKind;

    private static ILoader Loader;
    public static LoaderKind LoaderKind => Loader?.Kind ?? LoaderKind.None;
    public static string ConfigPath { get; private set; }
    public static string AssemblyPath { get; private set; }
    public static string AssetsPath { get; private set; }

    private static float DPIScale = -1;

    internal static bool Init(ILoader loader, int graphicsDeviceType, string graphicsDeviceTypeName, bool isOnWindows)
    {
        Loader = loader;
        Log.Init(Loader);
        if (!isOnWindows)
        {
            Log.Error("Unsupported OS: Windows required.");
            return false;
        }
        ConfigPath = Path.Combine(Loader.ConfigPath, "DearImGuiInjection");
        AssemblyPath = Loader.AssemblyPath;
        AssetsPath = Path.Combine(AssemblyPath, "Assets");
        string libraryFileName = $"cimgui-{(IntPtr.Size == 8 ? "x64" : "x86")}.dll";
        string libraryPath = Path.Combine(AssemblyPath, libraryFileName);
        LibraryLoader.CustomLoadFolders.Add(AssemblyPath);
        LibraryLoader.InterceptLibraryName += (ref string libraryName) =>
        {
            if (libraryName == ImGui.GetLibraryName())
            {
                libraryName = libraryFileName;
                return true;
            }
            return false;
        };
        LibraryLoader.ResolvePath += (string libraryName, out string pathToLibrary) =>
        {
            if (libraryName == libraryFileName)
            {
                pathToLibrary = libraryPath;
                return true;
            }
            pathToLibrary = null;
            return false;
        };
        RendererKind rendererKind = graphicsDeviceType switch
        {
            2 => RendererKind.DX11,
            18 => RendererKind.DX12,
            21 => RendererKind.Vulkan,
            8 => RendererKind.OpenGLES2,
            11 => RendererKind.OpenGLES3,
            17 => RendererKind.OpenGLCore,
            _ => RendererKind.None
        };
        if (rendererKind == RendererKind.None)
        {
            Log.Error($"Unsupported graphics API: {graphicsDeviceTypeName} ({graphicsDeviceType}).");
            return false;
        }
        ImGuiRenderer renderer = rendererKind switch
        {
            RendererKind.DX11 => new ImGuiDX11Renderer(),
            RendererKind.DX12 => new ImGuiDX12Renderer(),
            RendererKind.Vulkan => new ImGuiVulkanRenderer(),
            RendererKind.OpenGLES2 => new ImGuiOpenGLRenderer(),
            RendererKind.OpenGLES3 => new ImGuiOpenGLRenderer(),
            RendererKind.OpenGLCore => new ImGuiOpenGLRenderer(),
            _ => null
        };
        if (renderer == null)
        {
            Log.Error($"Renderer {rendererKind} is supported but not implemented yet.");
            return false;
        }
        try
        {
            renderer.Init();
            Log.Info($"Renderer {rendererKind} Init()");
            Renderer = renderer;
            _rendererKind = rendererKind;
        }
        catch (Exception e)
        {
            Log.Error($"Renderer {rendererKind} Init() failed: {e}");
            renderer.Dispose();
            return false;
        }
        Loader.CreateConfig(ref ShowDemoWindow, "General", "Show Demo Window", false,
            "Displays the built-in Dear ImGui demo window, useful for testing and debugging the UI.");
        Loader.CreateConfig(ref EnableDpiAwareness, "General", "Enable DPI Awareness", false,
            "Enables DPI awareness for better UI scaling on high-DPI monitors.");
        Loader.CreateConfig(ref AllowUpMessages, "Input", "Allow Up Messages", true,
            "Allows key and mouse release events to pass through, preventing stuck keys when using the UI.");
        Loader.CreateConfig(ref MouseDrawCursor, "Input", "Mouse Draw Cursor", false,
            "Draws the Dear ImGui mouse cursor only while the mouse is hovering over the UI, otherwise the game cursor is used.");
        Loader.SaveConfig();
        if (EnableDpiAwareness.GetValue())
            DPIScale = ImGuiImplWin32.GetDpiScaleForMonitor(User32.MonitorFromPoint(new POINT(0, 0), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY));
        MultiContextCompositor = new();
        if (ShowDemoWindow.GetValue())
            CreateModule("DearImGuiInjection").OnRender = ImGui.ShowDemoWindow;
        return true;
    }

    internal static void Dispose()
    {
        //DearImGuiInjectionCore.TextureManager.Dispose();
        Renderer?.Dispose();
        Renderer = null;
        for (int i = 0; i < DearImGuiInjectionCore.MultiContextCompositor.Modules.Count; i++)
        {
            ImGuiModule module = DearImGuiInjectionCore.MultiContextCompositor.Modules[i];
            module.OnInit = null;
            module.OnRender = null;
            DestroyModule(module.Id);
        }
    }

    public unsafe static ImGuiModule CreateModule(string Id, ModuleCreateOptions createOptions = ModuleCreateOptions.Default, string iniFilePath = null)
    {
        if (string.IsNullOrWhiteSpace(Id) || MultiContextCompositor.Modules.Any(x => x.Id == Id))
        {
            Log.Warning($"Module \"{Id}\" already has been registered.");
            return null;
        }
        ImGuiModule module = new ImGuiModule(Id);
        module.CreateOptions = createOptions;
        module.Context = ImGui.CreateContext();
        ImGui.SetCurrentContext(module.Context);
        ImGuiIOPtr io = ImGui.GetIO();
        module.IO = io;
        if ((module.CreateOptions & ModuleCreateOptions.IniFile) != 0)
        {
            Directory.CreateDirectory(ConfigPath);
            if (iniFilePath == null)
                iniFilePath = Path.Combine(ConfigPath, $"{Id}.ini");
            io.IniFilename = (byte*)Marshal.StringToHGlobalAnsi(iniFilePath);
        }
        if ((module.CreateOptions & ModuleCreateOptions.DefaultFlags) != 0)
        {
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableGamepad;
        }
        module.PlatformIO = ImGui.GetPlatformIO();
        ImGuiStylePtr style = ImGui.GetStyle();
        module.Style = style;
        if ((module.CreateOptions & ModuleCreateOptions.DefaultStyle) != 0)
            ImGui.StyleColorsDark(module.Style);
        if ((createOptions & ModuleCreateOptions.IgnoreDPIScale) == 0 && DPIScale > 0)
        {
            style.ScaleAllSizes(DPIScale);
            style.FontScaleDpi = DPIScale;
        }
        ImGui.SetCurrentContext(null);
        MultiContextCompositor.AddModule(module);
        return module;
    }

    public unsafe static void DestroyModule(string Id)
    {
        ImGuiModule module = MultiContextCompositor.Modules.FirstOrDefault(x => x.Id == Id);
        if (string.IsNullOrWhiteSpace(Id) || module == null)
        {
            Log.Warning($"Module \"{Id}\" is not registered.");
            return;
        }
        MultiContextCompositor.RemoveModule(module);
        ImGui.SetCurrentContext(module.Context);
        Renderer?.Shutdown(module.IsInitialized);
        try
        {
            module.OnDispose?.Invoke();
        }
        catch (Exception e)
        {
            Log.Error($"Module \"{module.Id}\" OnDispose threw an exception: {e}");
        }
        module.OnDispose = null;
        if ((module.CreateOptions & ModuleCreateOptions.IniFile) != 0)
        {
            IntPtr iniFileName = (IntPtr)module.IO.IniFilename;
            if (iniFileName != IntPtr.Zero)
                Marshal.FreeHGlobal(iniFileName);
        }
        ImGui.DestroyContext();
    }
}