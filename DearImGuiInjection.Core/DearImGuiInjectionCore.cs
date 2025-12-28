using DearImGuiInjection.Backends;
using DearImGuiInjection.Renderers;
using Hexa.NET.ImGui;
using HexaGen.Runtime;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("DearImGuiInjection.BepInEx5")]
[assembly: InternalsVisibleTo("DearImGuiInjection.BepInExIL2CPP")]
[assembly: InternalsVisibleTo("DearImGuiInjection.MelonIL2CPP")]

namespace DearImGuiInjection;

public static class DearImGuiInjectionCore
{
    public static LoaderKind LoaderKind => Loader.Kind;
    public static RendererKind RendererKind => RendererManager.Kind;

    public static bool IsInitialized { get; internal set; }

    public static string ConfigPath { get; private set; }
    public static string AssemblyPath { get; private set; }
    public static string AssetsPath { get; private set; }

    internal static string HexaVersion = "hexa_net (v2.2.11-pre)";

    internal static ImGuiMultiContextCompositor MultiContextCompositor = new();

    private static ILoader Loader;

    internal unsafe static bool Init(ILoader loader, ILog log)
    {
        Log.Init(log);
        if (!RendererManager.Init())
            return false;
        Loader = loader;
        ConfigPath = Loader.ConfigPath;
        AssemblyPath = Loader.AssemblyPath;
        AssetsPath = Path.Combine(AssemblyPath, "Assets");
        string libraryFileName = $"cimgui-{(Environment.Is64BitProcess ? "x64" : "x86")}.dll";
        string libraryPath = Path.Combine(AssemblyPath, libraryFileName);
        if (!File.Exists(libraryPath))
            throw new FileNotFoundException("Cimgui not found. Expected path: " + libraryPath);
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
        IsInitialized = true;
        return true;
    }

    internal static void Dispose()
    {
        if (!IsInitialized)
            return;
        foreach (ImGuiModule module in MultiContextCompositor.Modules)
        {
            module.OnInit = null;
            module.OnRender = null;
        }
        RendererManager.Shutdown();
        foreach (ImGuiModule module in MultiContextCompositor.Modules)
        {
            ImGui.SetCurrentContext(module.Context);
            module.OnDispose?.Invoke();
            module.OnDispose = null;
            ImGui.DestroyContext();
        }
    }

    public static void MultiContextCompositorShowDebugWindow() => MultiContextCompositor.ShowDebugWindow();

    public unsafe static ImGuiModule RegisterModule(string GUID)
    {
        if (string.IsNullOrEmpty(GUID) || MultiContextCompositor.Modules.Any(x => x.GUID == GUID))
        {
            Log.Warning($"Module \"{GUID}\" already has been registered.");
            return null;
        }
        ImGuiModule module = new ImGuiModule(GUID);
        module.Context = ImGui.CreateContext();
        ImGui.SetCurrentContext(module.Context);
        var io = ImGui.GetIO();
        module.IO = io;
        io.IniFilename = (byte*)Marshal.StringToHGlobalAnsi($"imgui_{GUID}.ini");
        module.PlatformIO = ImGui.GetPlatformIO();
        MultiContextCompositor.AddModule(module);
        return module;
    }

    public static void UnregisterModule(string GUID)
    {
        ImGuiModule module = MultiContextCompositor.Modules.FirstOrDefault(x => x.GUID == GUID);
        if (string.IsNullOrEmpty(GUID) || module == null)
        {
            Log.Warning($"Module \"{GUID}\" is not registered.");
            return;
        }
        MultiContextCompositor.RemoveModule(module);
        ImGui.SetCurrentContext(module.Context);
        // Implement this differently later once we have more renderers.
        ImGuiImplDX11.Shutdown();
        ImGuiImplWin32.Shutdown();
        module.OnDispose?.Invoke();
        module.OnDispose = null;
        ImGui.DestroyContext();
    }
}