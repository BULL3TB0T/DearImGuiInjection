using Hexa.NET.ImGui;
using System;
using System.Collections.Generic;
using System.Text;

namespace DearImGuiInjection;

internal sealed class ImGuiModule
{
    public string GUID;

    public bool IsInitialized;

    public ImGuiContextPtr Context;
    public ImGuiIOPtr IO;

    public int ZIndex;
    public bool IsHoveredThisFrame;

    public Action OnInit;
    public Action OnRender;
    public Action OnDispose;

    public void Unfocus()
    {
        var oldContext = ImGui.GetCurrentContext();
        ImGui.SetCurrentContext(Context);
        ImGuiP.FocusWindow(null);
        ImGui.SetCurrentContext(oldContext);
    }

    public ImGuiModule(string GUID) => this.GUID = GUID;

    private bool Equals(ImGuiModule other) => GUID == other.GUID;
    public override bool Equals(object obj) => obj is ImGuiModule other && Equals(other);
    public override int GetHashCode() => GUID.GetHashCode();
}