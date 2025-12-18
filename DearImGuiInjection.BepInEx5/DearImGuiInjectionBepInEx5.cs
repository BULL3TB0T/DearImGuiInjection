using BepInEx;
using DearImGuiInjection;
using DearImGuiInjection.Windows;

namespace DearImGuiInjection.BepInEx5;

[BepInPlugin(Metadata.GUID, Metadata.Name, Metadata.Version)]
internal class DearImGuiInjectionBepInEx5 : BaseUnityPlugin
{
    private void Awake()
    {
        Log.Init(new LogBepInEx(Logger));
        gameObject.AddComponent<UnityMainThreadDispatcher>();
        if (!DearImGuiInjectionCore.Init(Paths.ConfigPath, Path.GetDirectoryName(Info.Location)))
            return;
        DearImGuiInjectionCore.UseDefaultTheme = new ConfigEntryBepInEx<bool>(Config.Bind(
            DearImGuiInjectionCore.UseDefaultThemeCategory,
            DearImGuiInjectionCore.UseDefaultThemeKey,
            DearImGuiInjectionCore.UseDefaultThemeDefaultValue,
            DearImGuiInjectionCore.UseDefaultThemeDescription));
        DearImGuiInjectionCore.CursorVisibility = new ConfigEntryBepInEx<VirtualKey>(Config.Bind(
            DearImGuiInjectionCore.CursorVisibilityCategory,
            DearImGuiInjectionCore.CursorVisibilityKey,
            DearImGuiInjectionCore.CursorVisibilityDefaultValue,
            DearImGuiInjectionCore.CursorVisibilityDescription));
        DearImGuiInjectionCore.SaveOrRestoreCursorPosition = new ConfigEntryBepInEx<bool>(Config.Bind(
            DearImGuiInjectionCore.SaveRestoreCursorPositionCategory,
            DearImGuiInjectionCore.SaveRestoreCursorPositionKey,
            DearImGuiInjectionCore.SaveRestoreCursorPositionDefaultValue,
            DearImGuiInjectionCore.SaveRestoreCursorPositionDescription));
    }

    private void OnDestroy() => DearImGuiInjectionCore.Dispose();
}