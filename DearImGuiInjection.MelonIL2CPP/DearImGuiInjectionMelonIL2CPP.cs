using DearImGuiInjection;
using DearImGuiInjection.MelonIL2CPP;
using DearImGuiInjection.Windows;
using MelonLoader;
using MelonLoader.Utils;
using System.IO;
using System.Reflection;

[assembly: MelonInfo(typeof(DearImGuiInjectionMelonIL2CPP), Metadata.Name, Metadata.Version, Metadata.Author, Metadata.DownloadLink)]

namespace DearImGuiInjection.MelonIL2CPP;

internal class DearImGuiInjectionMelonIL2CPP : MelonMod
{
    public override void OnInitializeMelon()
    {
        Log.Init(new LogMelon(LoggerInstance));
        if (!DearImGuiInjectionCore.Init(MelonEnvironment.UserDataDirectory, Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)))
            return;
        DearImGuiInjectionCore.UseDefaultTheme = new ConfigEntryMelon<bool>(
            MelonPreferences.CreateCategory(
                DearImGuiInjectionCore.UseDefaultThemeCategory).CreateEntry(
                DearImGuiInjectionCore.UseDefaultThemeKey,
                DearImGuiInjectionCore.UseDefaultThemeDefaultValue,
                DearImGuiInjectionCore.UseDefaultThemeDescription));
        DearImGuiInjectionCore.CursorVisibility = new ConfigEntryMelon<VirtualKey>(
            MelonPreferences.CreateCategory(
                DearImGuiInjectionCore.CursorVisibilityCategory).CreateEntry(
                DearImGuiInjectionCore.CursorVisibilityKey,
                DearImGuiInjectionCore.CursorVisibilityDefaultValue,
                DearImGuiInjectionCore.CursorVisibilityDescription));
        DearImGuiInjectionCore.SaveOrRestoreCursorPosition = new ConfigEntryMelon<bool>(
            MelonPreferences.CreateCategory(
                DearImGuiInjectionCore.SaveRestoreCursorPositionCategory).CreateEntry(
                DearImGuiInjectionCore.SaveRestoreCursorPositionKey,
                DearImGuiInjectionCore.SaveRestoreCursorPositionDefaultValue,
                DearImGuiInjectionCore.SaveRestoreCursorPositionDescription));
        MelonPreferences.Save();
    }

    public override void OnDeinitializeMelon() => DearImGuiInjectionCore.Dispose();
}
