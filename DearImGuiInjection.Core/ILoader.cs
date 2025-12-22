using System.Reflection;

namespace DearImGuiInjection;

public enum LoaderKind
{
    BepInEx5,
    BepInExIL2CPP,
    MelonIL2CPP
}

internal interface ILoader
{
    internal LoaderKind Kind { get; }

    internal string ConfigPath { get; }
    internal string AssemblyPath { get; }
}