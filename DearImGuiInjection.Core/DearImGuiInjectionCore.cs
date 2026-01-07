using DearImGuiInjection.Renderers;
using DearImGuiInjection.Textures;
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
[assembly: InternalsVisibleTo("DearImGuiInjection.MelonIL2CPP")]
[assembly: InternalsVisibleTo("DearImGuiInjection.MelonMono")]

namespace DearImGuiInjection;

public static class DearImGuiInjectionCore
{
    internal const string HexaVersion = "unity_hexa_net (v2.2.11-pre)";

    public static LoaderKind LoaderKind => Loader?.Kind ?? LoaderKind.None;
    public static RendererKind RendererKind => Renderer?.Kind ?? RendererKind.None;

    public static string ConfigPath { get; private set; }
    public static string AssemblyPath { get; private set; }
    public static string AssetsPath { get; private set; }

    public static IConfigEntry<bool> ShowDemoWindow;
    public static IConfigEntry<bool> AllowUpMessages;
    public static IConfigEntry<bool> MouseDrawCursor;

    public static ITextureManager TextureManager;
    public static ImGuiMultiContextCompositor MultiContextCompositor;

    internal static ImGuiRenderer Renderer;

    private static ILoader Loader;

    internal static bool Init(ILoader loader)
    {
        Loader = loader;
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
        Log.Init(Loader);
        foreach (RendererKind kind in Enum.GetValues(typeof(RendererKind)))
        {
            ImGuiRenderer renderer = kind switch
            {
                RendererKind.DX11 => new ImGuiDX11Renderer(),
                _ => null
            };
            if (renderer == null)
                continue;
            bool isSupported = false;
            try
            {
                isSupported = renderer.IsSupported();
            }
            catch (Exception e)
            {
                Log.Error($"Renderer {renderer.Kind} IsSupported() failed: {e}");
                isSupported = false;
            }
            if (!isSupported)
                continue;
            try
            {
                renderer.Init();
                Log.Info($"Renderer {renderer.Kind} Init()");
                Renderer = renderer;
                break;
            }
            catch (Exception e)
            {
                Log.Error($"Renderer {renderer.Kind} Init() failed: {e}");
                renderer.Dispose();
                break;
            }
        }
        if (Renderer == null)
        {
            Log.Error($"Could not find the right renderer.");
            return false;
        }
        Loader.CreateConfig(ref ShowDemoWindow, "General", "Show Demo Window", false,
            "Displays the built-in Dear ImGui demo window, useful for testing and debugging the UI.");
        Loader.CreateConfig(ref AllowUpMessages, "Input", "Allow Up Messages", true,
            "Allows key and mouse release events to pass through, preventing stuck keys when using the UI.");
        Loader.CreateConfig(ref MouseDrawCursor, "Input", "Mouse Draw Cursor", false,
            "Draws the Dear ImGui mouse cursor only while the mouse is hovering over the UI, otherwise the game cursor is used.");
        Loader.SaveConfig();
        MultiContextCompositor = new();
        if (ShowDemoWindow.GetValue())
            CreateModule("DearImGuiInjection").OnRender = () => { ImGui.ShowDemoWindow(); };
        return true;
    }

    internal static void Dispose()
    {
        foreach (ImGuiModule module in MultiContextCompositor.Modules)
        {
            module.OnInit = null;
            module.OnRender = null;
        }
        Renderer?.Dispose();
        Renderer = null;
        foreach (ImGuiModule module in MultiContextCompositor.Modules)
            DestroyModule(module.Id);
    }

    public unsafe static ImGuiModule CreateModule(string Id)
    {
        if (string.IsNullOrWhiteSpace(Id) || MultiContextCompositor.Modules.Any(x => x.Id == Id))
        {
            Log.Warning($"Module \"{Id}\" already has been registered.");
            return null;
        }
        ImGuiModule module = new ImGuiModule(Id);
        module.Context = ImGui.CreateContext();
        ImGui.SetCurrentContext(module.Context);
        var io = ImGui.GetIO();
        module.IO = io;
        Directory.CreateDirectory(ConfigPath);
        io.IniFilename = (byte*)Marshal.StringToHGlobalAnsi(Path.Combine(ConfigPath, $"{Id}.ini"));
        module.PlatformIO = ImGui.GetPlatformIO();
        MultiContextCompositor.AddModule(module);
        return module;
    }

    public static void DestroyModule(string Id)
    {
        ImGuiModule module = MultiContextCompositor.Modules.FirstOrDefault(x => x.Id == Id);
        if (string.IsNullOrWhiteSpace(Id) || module == null)
        {
            Log.Warning($"Module \"{Id}\" is not registered.");
            return;
        }
        MultiContextCompositor.RemoveModule(module);
        ImGui.SetCurrentContext(module.Context);
        Renderer?.Handler.OnShutdown();
        try
        {
            module.OnDispose?.Invoke();
        }
        catch (Exception e)
        {
            Log.Error($"Module \"{module.Id}\" OnDispose threw an exception: {e}");
        }
        module.OnDispose = null;
        ImGui.DestroyContext();
    }
}