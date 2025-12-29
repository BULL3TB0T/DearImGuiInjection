using BepInEx;
using BepInEx.Logging;
using DearImGuiInjection;
using DearImGuiInjection.Windows;

namespace DearImGuiInjection.BepInEx5;

[BepInPlugin(DearImGuiInjectionMetadata.GUID, DearImGuiInjectionMetadata.Name, DearImGuiInjectionMetadata.Version)]
internal class DearImGuiInjectionBepInEx5 : BaseUnityPlugin, ILoader, ILog
{ 
    public LoaderKind Kind => LoaderKind.BepInEx5;

    public string ConfigPath => Paths.ConfigPath;
    public string AssemblyPath => Path.GetDirectoryName(base.Info.Location);

    private void Awake()
    {
        if (!DearImGuiInjectionCore.Init(this, this))
            return;
        DearImGuiInjectionCore.AllowUpMessages = new ConfigEntryBepInEx<bool>(Config.Bind(
            DearImGuiInjectionCore.AllowUpMessagesCategory,
            DearImGuiInjectionCore.AllowUpMessagesKey,
            DearImGuiInjectionCore.AllowUpMessagesDefaultValue,
            DearImGuiInjectionCore.AllowUpMessagesDescription));
        gameObject.AddComponent<UnityMainThreadDispatcher>();
    }

    private void OnDestroy() => DearImGuiInjectionCore.Dispose();

    public void Debug(object data) => Logger.LogDebug(data);
    public void Error(object data) => Logger.LogError(data);
    public void Fatal(object data) => Logger.LogFatal(data);
    public new void Info(object data) => Logger.LogInfo(data);
    public void Message(object data) => Logger.LogMessage(data);
    public void Warning(object data) => Logger.LogWarning(data);
}