using DearImGuiInjection.Windows;
using Hexa.NET.ImGui;
using System;

namespace DearImGuiInjection;

[Flags]
public enum ModuleCreateOptions
{
    None = 0,
    IniFile = 1 << 0,
    DefaultFlags = 1 << 1,
    DefaultStyle = 1 << 2,
    IgnoreDPIScale = 1 << 3,
    Default = IniFile | DefaultFlags | DefaultStyle
}

public sealed class ImGuiModule
{
    public string Id { get; internal set; }

    internal bool IsInitialized;
    internal ModuleCreateOptions CreateOptions;

    public ImGuiContextPtr Context { get; internal set; }
    public ImGuiIOPtr IO { get; internal set; }
    public ImGuiPlatformIOPtr PlatformIO { get; internal set; }
    public ImGuiStylePtr Style { get; internal set; }

    private Action _onInit;
    public Action OnInit
    {
        internal get => _onInit;
        set
        {
            if (_onInit != null && value != null)
            {
                Log.Warning($"Module \"{Id}\" OnInit cannot be set because it is already assigned.");
                return;
            }
            _onInit = value;
        }
    }
    private Action _onDispose;
    public Action OnDispose
    {
        internal get => _onDispose;
        set
        {
            if (_onDispose != null && value != null)
            { 
                Log.Warning($"Module \"{Id}\" OnDispose cannot be set because it is already assigned.");
                return;
            }
            _onDispose = value;
        }
    }
    private Action _onRender;
    public Action OnRender
    {
        internal get => _onRender;
        set
        {
            if (_onRender != null && value != null)
            {
                Log.Warning($"Module \"{Id}\" OnRender cannot be set because it is already assigned.");
                return;
            }
            _onRender = value;
        }
    }
    private Func<IntPtr, WindowMessage, IntPtr, IntPtr, bool> _onWndProcHandler;
    public Func<IntPtr, WindowMessage, IntPtr, IntPtr, bool> OnWndProcHandler
    {
        internal get => _onWndProcHandler;
        set
        {
            if (_onWndProcHandler != null && value != null)
            {
                Log.Warning($"Module \"{Id}\" OnWndProc cannot be set because it is already assigned.");
                return;
            }
            _onWndProcHandler = value;
        }
    }

    internal ImGuiModule(string Id) => this.Id = Id;

    private bool Equals(ImGuiModule module) => module != null && module.Id == Id;
    public override bool Equals(object obj) => obj is ImGuiModule module && Equals(module);
    public override int GetHashCode() => Id.GetHashCode();
    public override string ToString() => Id;
}