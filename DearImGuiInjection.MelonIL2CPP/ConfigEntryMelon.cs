using System;
using DearImGuiInjection;
using MelonLoader;

namespace DearImGuiInjection.MelonIL2CPP;

internal class ConfigEntryMelon<T> : IConfigEntry<T>
{
    private MelonPreferences_Entry<T> _configEntry;

    public ConfigEntryMelon(MelonPreferences_Entry<T> configEntry) => _configEntry = configEntry;

    public T Get() => _configEntry.Value;
    public void Set(T value) => _configEntry.Value = value;
}
