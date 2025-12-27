using DearImGuiInjection.Backends;
using DearImGuiInjection.Renderers;
using DearImGuiInjection.Windows;
using Hexa.NET.ImGui;
using HexaGen.Runtime;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

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

    internal static ImGuiMultiContextCompositor MultiContext = new();

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
        {
            Log.Error("Cimgui not found. Expected path: " + libraryPath);
            return false;
        }
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
        foreach (ImGuiModule module in MultiContext.Modules)
        {
            module.OnInit = null;
            module.OnRender = null;
        }
        RendererManager.Shutdown();
        foreach (ImGuiModule module in MultiContext.Modules)
        {
            ImGui.SetCurrentContext(module.Context);
            module.OnDispose?.Invoke();
            module.OnDispose = null;
            MultiContext.RemoveModule(module);
            ImGui.DestroyContext(module.Context);
            module.Context = null;
        }
    }

    public unsafe static ImGuiModule RegisterModule(string GUID, Action onInit = null, Action onDispose = null, Action onRender = null)
    {
        if (onRender == null)
        {
            Log.Error($"\"{GUID}\": OnRender is required.");
            return null;
        }
        if (string.IsNullOrEmpty(GUID) || MultiContext.Modules.Any(x => x.GUID == GUID))
        {
            Log.Warning($"\"{GUID}\": Already been registered.");
            return null;
        }
        ImGuiModule module = new ImGuiModule(GUID);
        module.OnInit = onInit;
        module.OnDispose = onDispose;
        module.OnRender = onRender;
        module.Context = ImGui.CreateContext();
        ImGui.SetCurrentContext(module.Context);
        var io = ImGui.GetIO();
        module.IO = io;
        io.IniFilename = (byte*)Marshal.StringToHGlobalAnsi($"imgui_{GUID}.ini");
        MultiContext.AddModule(module);
        return module;
    }
}