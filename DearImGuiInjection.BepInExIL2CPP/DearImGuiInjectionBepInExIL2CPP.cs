using BepInEx;
using BepInEx.Unity.IL2CPP;
using DearImGuiInjection;
using DearImGuiInjection.Windows;

namespace DearImGuiInjection.BepInExIL2CPP;

[BepInPlugin(Metadata.GUID, Metadata.Name, Metadata.Version)]
internal class DearImGuiInjectionBepInExI2LCPP : BasePlugin
{
    public override void Load()
    {
        DearImGuiInjection.Log.Init(new LogBepInEx(Log));
        AddComponent<UnityMainThreadDispatcher>();
        if (!DearImGuiInjectionCore.Init(Paths.ConfigPath, Path.GetDirectoryName(IL2CPPChainloader.Instance.Plugins[Metadata.GUID].Location)))
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

    public override bool Unload()
    {
        DearImGuiInjectionCore.Dispose();
        return true;
    }
}