using System;
using DearImGuiInjection;
using MelonLoader;

namespace DearImGuiInjection.MelonMono;

internal class ConfigEntryMelon<T> : IConfigEntry<T>
{
    private MelonPreferences_Entry<T> _melonPreferenceEntry;

    public ConfigEntryMelon(MelonPreferences_Entry<T> configEntry) => _melonPreferenceEntry = configEntry;

    public T GetValue() => _melonPreferenceEntry.Value;
}
