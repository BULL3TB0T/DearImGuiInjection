using DearImGuiInjection;
using DearImGuiInjection.MelonIL2CPP;
using DearImGuiInjection.Windows;
using Il2CppInterop.Runtime.Injection;
using MelonLoader;
using MelonLoader.Utils;
using System.IO;
using UnityEngine;

[assembly: MelonInfo(typeof(DearImGuiInjectionMelonIL2CPP), DearImGuiInjectionMetadata.Name, DearImGuiInjectionMetadata.Version, DearImGuiInjectionMetadata.Author, DearImGuiInjectionMetadata.DownloadLink)]

namespace DearImGuiInjection.MelonIL2CPP;

internal class DearImGuiInjectionMelonIL2CPP : MelonMod, ILoader
{
    public LoaderKind Kind => LoaderKind.MelonIL2CPP;

    public string ConfigPath => MelonEnvironment.UserDataDirectory;
    public string AssemblyPath => Path.GetDirectoryName(MelonAssembly.Location);

    public override void OnInitializeMelon()
    {
        if (!DearImGuiInjectionCore.Init(this))
            return;
        ClassInjector.RegisterTypeInIl2Cpp<UnityMainThreadDispatcher>();
        GameObject gameObject = new GameObject(DearImGuiInjectionMetadata.Name);
        gameObject.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.DontDestroyOnLoad(gameObject);
        gameObject.AddComponent<UnityMainThreadDispatcher>();
    }

    public override void OnDeinitializeMelon() => DearImGuiInjectionCore.Dispose();

    public void CreateConfig<T>(ref IConfigEntry<T> configEntry, string category, string key, T defaultValue, string description) =>
        configEntry = new ConfigEntryMelon<T>(MelonPreferences.CreateCategory(category).CreateEntry(key, defaultValue,
            description: description));
    public void SaveConfig() => MelonPreferences.Save();

    public void Debug(object data) => LoggerInstance.Msg(data);
    public void Error(object data) => LoggerInstance.Error(data);
    public void Fatal(object data) => LoggerInstance.Error(data);
    public new void Info(object data) => LoggerInstance.MsgPastel(data);
    public void Message(object data) => LoggerInstance.Msg(data);
    public void Warning(object data) => LoggerInstance.Warning(data);
}
