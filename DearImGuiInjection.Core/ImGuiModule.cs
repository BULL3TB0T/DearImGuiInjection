using Hexa.NET.ImGui;
using System;
using System.Collections.Generic;
using System.Text;

namespace DearImGuiInjection;

public sealed class ImGuiModule
{
    internal string GUID;

    internal bool IsInitialized;

    internal ImGuiContextPtr Context;
    internal ImGuiIOPtr IO;

    internal Action OnInit;
    internal Action OnRender;
    internal Action OnDispose;

    internal ImGuiModule(string GUID) => this.GUID = GUID;

    private bool Equals(ImGuiModule other) => GUID == other.GUID;
    public override bool Equals(object obj) => obj is ImGuiModule other && Equals(other);
    public override int GetHashCode() => GUID.GetHashCode();
}