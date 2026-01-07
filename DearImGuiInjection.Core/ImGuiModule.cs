using DearImGuiInjection.Windows;
using Hexa.NET.ImGui;
using System;

namespace DearImGuiInjection;

public sealed class ImGuiModule
{
    public string Id { get; internal set; }

    internal bool IsInitialized;

    public ImGuiContextPtr Context { get; internal set; }
    public ImGuiIOPtr IO { get; internal set; }
    public ImGuiPlatformIOPtr PlatformIO { get; internal set; }

    private Action _onInit;
    public Action OnInit
    {
        get => _onInit;
        set
        {
            if (_onInit != null && value != null)
                Log.Warning($"Module \"{Id}\" OnInit cannot be set because it is already assigned.");
            _onInit = value;
        }
    }
    private Action _onDispose;
    public Action OnDispose
    {
        get => _onDispose;
        set
        {
            if (_onDispose != null && value != null)
                Log.Warning($"Module \"{Id}\" OnDispose cannot be set because it is already assigned.");
            _onDispose = value;
        }
    }
    private Action _onRender;
    public Action OnRender
    {
        get => _onRender;
        set
        {
            if (_onRender != null && value != null)
                Log.Warning($"Module \"{Id}\" OnRender cannot be set because it is already assigned.");
            _onRender = value;
        }
    }
    private Func<IntPtr, WindowMessage, IntPtr, IntPtr, bool> _onWndProcHandler;
    public Func<IntPtr, WindowMessage, IntPtr, IntPtr, bool> OnWndProcHandler
    {
        get => _onWndProcHandler;
        set
        {
            if (_onWndProcHandler != null && value != null)
                Log.Warning($"Module \"{Id}\" OnWndProc cannot be set because it is already assigned.");
            _onWndProcHandler = value;
        }
    }

    internal ImGuiModule(string Id) => this.Id = Id;

    private bool Equals(ImGuiModule module) => module != null && module.Id == Id;
    public override bool Equals(object obj) => obj is ImGuiModule module && Equals(module);
    public override int GetHashCode() => Id.GetHashCode();
    public override string ToString() => Id;
}