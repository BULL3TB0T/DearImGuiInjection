using BepInEx;
using BepInEx.Unity.Mono;
using DearImGuiInjection;
using DearImGuiInjection.Windows;
using System.IO;
using UnityEngine;

namespace DearImGuiInjection.BepInEx6;

[BepInPlugin(DearImGuiInjectionMetadata.GUID, DearImGuiInjectionMetadata.Name, DearImGuiInjectionMetadata.Version)]
internal class DearImGuiInjectionBepInEx6 : BaseUnityPlugin, ILoader
{ 
    public LoaderKind Kind => LoaderKind.BepInEx6;

    public string ConfigPath => Paths.ConfigPath;
    public string AssemblyPath => Path.GetDirectoryName(base.Info.Location);

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        gameObject.hideFlags = HideFlags.HideAndDontSave;
        if (!DearImGuiInjectionCore.Init(this))
            return;
        gameObject.AddComponent<UnityMainThreadDispatcher>();
    }

    private void OnDestroy() => DearImGuiInjectionCore.Dispose();

    public void CreateConfig<T>(ref IConfigEntry<T> configEntry, string category, string key, T defaultValue, string description) => 
        configEntry = new ConfigEntryBepInEx<T>(Config.Bind(category, key, defaultValue, description));
    public void SaveConfig() { }

    public void Debug(object data) => Logger.LogDebug(data);
    public void Error(object data) => Logger.LogError(data);
    public void Fatal(object data) => Logger.LogFatal(data);
    public new void Info(object data) => Logger.LogInfo(data);
    public void Message(object data) => Logger.LogMessage(data);
    public void Warning(object data) => Logger.LogWarning(data);
}