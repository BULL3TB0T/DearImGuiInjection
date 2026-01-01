using System.Reflection;

namespace DearImGuiInjection;

public enum LoaderKind
{
    None,
    BepInEx5,
    BepInExIL2CPP,
    MelonIL2CPP,
    MelonMono
}

internal interface ILoader
{
    public LoaderKind Kind { get; }

    public string GUID { get; }

    public string ConfigPath { get; }
    public string AssemblyPath { get; }

    public void CreateConfig<T>(ref IConfigEntry<T> configEntry, string section, string key, T defaultValue, string description);
    public void SaveConfig();

    public void Debug(object data);
    public void Error(object data);
    public void Fatal(object data);
    public void Info(object data);
    public void Message(object data);
    public void Warning(object data);
}