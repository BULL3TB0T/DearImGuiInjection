using BepInEx;
using BepInEx.Logging;
using DearImGuiInjection;
using DearImGuiInjection.Windows;
using System.IO;

namespace DearImGuiInjection.BepInEx5;

[BepInPlugin(DearImGuiInjectionMetadata.GUID, DearImGuiInjectionMetadata.Name, DearImGuiInjectionMetadata.Version)]
internal class DearImGuiInjectionBepInEx5 : BaseUnityPlugin, ILoader
{ 
    public LoaderKind Kind => LoaderKind.BepInEx5;

    public string ConfigPath => Paths.ConfigPath;
    public string AssemblyPath => Path.GetDirectoryName(base.Info.Location);

    private void Awake()
    {
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