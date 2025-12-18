using BepInEx.Configuration;
using DearImGuiInjection;

namespace DearImGuiInjection.BepInEx5;

internal class ConfigEntryBepInEx<T> : IConfigEntry<T>
{
    private ConfigEntry<T> _configEntry;

    public ConfigEntryBepInEx(ConfigEntry<T> configEntry) => _configEntry = configEntry;

    public T Get() => _configEntry.Value;
    public void Set(T value) => _configEntry.Value = value;
}