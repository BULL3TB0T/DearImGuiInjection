using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using DearImGuiInjection;
using DearImGuiInjection.Renderers;
using DearImGuiInjection.Windows;

namespace DearImGuiInjection.BepInExIL2CPP;

[BepInPlugin(DearImGuiInjectionMetadata.GUID, DearImGuiInjectionMetadata.Name, DearImGuiInjectionMetadata.Version)]
internal class DearImGuiInjectionBepInExI2LCPP : BasePlugin, ILoader, ILog
{
    public LoaderKind Kind => LoaderKind.BepInExIL2CPP;

    public string ConfigPath => Paths.ConfigPath;
    public string AssemblyPath => 
        Path.GetDirectoryName(IL2CPPChainloader.Instance.Plugins[DearImGuiInjectionMetadata.GUID].Location);

    public override void Load()
    {
        if (!DearImGuiInjectionCore.Init(this, this))
            return;
        DearImGuiInjectionCore.AllowUpMessages = new ConfigEntryBepInEx<bool>(Config.Bind(
            DearImGuiInjectionCore.AllowUpMessagesCategory,
            DearImGuiInjectionCore.AllowUpMessagesKey,
            DearImGuiInjectionCore.AllowUpMessagesDefaultValue,
            DearImGuiInjectionCore.AllowUpMessagesDescription));
        AddComponent<UnityMainThreadDispatcher>();
    }

    public override bool Unload()
    {
        DearImGuiInjectionCore.Dispose();
        return true;
    }

    public void Debug(object data) => Log.LogDebug(data);
    public void Error(object data) => Log.LogError(data);
    public void Fatal(object data) => Log.LogFatal(data);
    public void Info(object data) => Log.LogInfo(data);
    public void Message(object data) => Log.LogMessage(data);
    public void Warning(object data) => Log.LogWarning(data);
}