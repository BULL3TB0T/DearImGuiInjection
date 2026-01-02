using BepInEx.Configuration;
using DearImGuiInjection;

namespace DearImGuiInjection.BepInEx5;

internal class ConfigEntryBepInEx<T> : IConfigEntry<T>
{
    private ConfigEntry<T> _configEntry;

    public ConfigEntryBepInEx(ConfigEntry<T> configEntry) => _configEntry = configEntry;

    public T GetValue() => _configEntry.Value;
    public T SetValue(T value) => _configEntry.Value = value;
}