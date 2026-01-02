using BepInEx.Configuration;
using DearImGuiInjection;

namespace DearImGuiInjection.BepInEx6;

internal class ConfigEntryBepInEx<T> : IConfigEntry<T>
{
    private ConfigEntry<T> _configEntry;

    public ConfigEntryBepInEx(ConfigEntry<T> configEntry) => _configEntry = configEntry;

    public T GetValue() => _configEntry.Value;
}