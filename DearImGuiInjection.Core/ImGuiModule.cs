using Hexa.NET.ImGui;
using System;

namespace DearImGuiInjection;

public sealed class ImGuiModule
{
    internal string GUID;

    internal bool IsInitialized;

    public ImGuiContextPtr Context;
    public ImGuiIOPtr IO;

    public bool UnfocusNextFrame = true;
    internal bool DragDropActive;

    internal Action OnInit;
    internal Action OnRender;
    internal Action OnDispose;

    internal ImGuiModule(string GUID) => this.GUID = GUID;

    private bool Equals(ImGuiModule other) => GUID == other.GUID;
    public override bool Equals(object obj) => Equals(obj as ImGuiModule);
    public override int GetHashCode() => GUID.GetHashCode();
}