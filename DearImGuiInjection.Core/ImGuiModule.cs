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

    public Action OnInit;
    public Action OnDispose;
    public Action OnRender;
    public Func<IntPtr, WindowMessage, IntPtr, IntPtr, bool> OnWndProc;

    internal ImGuiModule(string Id) => this.Id = Id;

    private bool Equals(ImGuiModule module) => Id == module.Id;
    public override bool Equals(object obj) => Equals(obj as ImGuiModule);
    public override int GetHashCode() => Id.GetHashCode();
}