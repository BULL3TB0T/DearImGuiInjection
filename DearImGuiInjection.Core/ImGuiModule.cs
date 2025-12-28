using DearImGuiInjection.Windows;
using Hexa.NET.ImGui;
using System;

namespace DearImGuiInjection;

public sealed class ImGuiModule
{
    public string GUID { get; internal set; }

    internal bool IsInitialized;

    public ImGuiContextPtr Context { get; internal set; }
    public ImGuiIOPtr IO { get; internal set; }
    public ImGuiPlatformIOPtr PlatformIO { get; internal set; }

    public Action OnInit;
    public Action OnDispose;
    public Action OnRender;
    public Func<IntPtr, WindowMessage, IntPtr, IntPtr, bool> OnWndProc;

    internal ImGuiModule(string GUID) => this.GUID = GUID;

    private bool Equals(ImGuiModule other) => GUID == other.GUID;
    public override bool Equals(object obj) => Equals(obj as ImGuiModule);
    public override int GetHashCode() => GUID.GetHashCode();
}