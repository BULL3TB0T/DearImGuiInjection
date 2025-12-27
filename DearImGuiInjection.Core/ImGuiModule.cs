using Hexa.NET.ImGui;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace DearImGuiInjection;

public sealed class ImGuiModule
{
    internal string GUID;

    internal bool IsInitialized;

    public ImGuiContextPtr Context;
    public ImGuiIOPtr IO;

    internal int ZIndex;

    public bool UnfocusNextFrame = true;

    internal Action OnInit;
    internal Action OnRender;
    internal Action OnDispose;

    internal ImGuiModule(string GUID) => this.GUID = GUID;
}