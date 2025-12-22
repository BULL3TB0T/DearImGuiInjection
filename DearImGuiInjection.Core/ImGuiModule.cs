using Hexa.NET.ImGui;
using System;
using System.Collections.Generic;
using System.Text;

namespace DearImGuiInjection;

public sealed class ImGuiModule
{
    internal string GUID;

    public bool IsInitialized { get; internal set; }

    public ImGuiContextPtr Context { get; internal set; }
    public ImGuiIOPtr IO { get; internal set; }

    internal Action<ImGuiModule> OnInit;
    internal Action<ImGuiModule> OnRender;
    internal Action<ImGuiModule> OnDispose;

    internal ImGuiModule(string GUID) => this.GUID = GUID;

    private bool Equals(ImGuiModule other) => GUID == other.GUID;
    public override bool Equals(object obj) => obj is ImGuiModule other && Equals(other);
    public override int GetHashCode() => GUID.GetHashCode();
}