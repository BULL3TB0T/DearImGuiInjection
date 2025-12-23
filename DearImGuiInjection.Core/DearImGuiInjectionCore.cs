using DearImGuiInjection.Renderers;
using DearImGuiInjection.Windows;
using Hexa.NET.ImGui;
using HexaGen.Runtime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

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

    internal static HashSet<ImGuiModule> Modules = new HashSet<ImGuiModule>();

    private static ILoader Loader;
    private static IntPtr Library;

    internal unsafe static bool Init(ILoader loader, ILog log)
    {
        Log.Init(log);
        if (!RendererManager.Init())
            return false;
        Loader = loader;
        ConfigPath = Loader.ConfigPath;
        AssemblyPath = Loader.AssemblyPath;
        AssetsPath = Path.Combine(AssemblyPath, "Assets");
        string libraryPath = Path.Combine(AssemblyPath, $"cimgui-{(Environment.Is64BitProcess ? "x64" : "x86")}.dll");
        if (!File.Exists(libraryPath))
        {
            Log.Error("Cimgui not found. Expected path: " + libraryPath);
            return false;
        }
        LibraryLoader.ResolvePath += (string libraryName, out string pathToLibrary) =>
        {
            if (libraryName.StartsWith("cimgui"))
            {
                pathToLibrary = libraryPath;
                return true;
            }
            pathToLibrary = null;
            return false;
        };
        Library = Kernel32.LoadLibrary(libraryPath);
        ImGui.InitApi(new NativeLibraryContext(Library));
        Register("guid.test", onRender: () => { ImGui.ShowDemoWindow(); });
        Register("guid.hi", onRender: () => { ImGui.Begin("hi test"); ImGui.End(); });
        IsInitialized = true;
        return true;
    }

    internal unsafe static void Dispose()
    {
        if (!IsInitialized)
            return;
        foreach (ImGuiModule module in Modules)
        {
            module.OnInit = null;
            module.OnRender = null;
        }
        RendererManager.Shutdown();
        foreach (ImGuiModule module in Modules)
        {
            ImGui.SetCurrentContext(module.Context);
            module.OnDispose?.Invoke();
            module.OnDispose = null;
            ImGui.DestroyContext(module.Context);
            module.Context = null;
        }
        ImGui.FreeApi();
        Kernel32.FreeLibrary(Library);
    }

    public static void Register(string GUID, Action onInit = null, Action onDispose = null, Action onRender = null)
    {
        ImGuiModule module = new ImGuiModule(GUID);
        if (Modules.Add(module))
        {
            if (onRender == null)
            {
                Log.Error($"\"{GUID}\": OnRender is required.");
                return;
            }
            module.OnInit = onInit;
            module.OnDispose = onDispose;
            module.OnRender = onRender;
            module.Context = ImGui.CreateContext();
            ImGui.SetCurrentContext(module.Context);
            module.IO = ImGui.GetIO();
            return;
        }
        Log.Warning($"\"{GUID}\": Already been registered.");
    }
}