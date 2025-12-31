using DearImGuiInjection;
using DearImGuiInjection.MelonMono;
using DearImGuiInjection.Windows;
using MelonLoader;
using MelonLoader.Utils;
using System.IO;
using System.Reflection;
using UnityEngine;

[assembly: MelonInfo(typeof(DearImGuiInjectionMelonMono), DearImGuiInjectionMetadata.Name, DearImGuiInjectionMetadata.Version, DearImGuiInjectionMetadata.Author, DearImGuiInjectionMetadata.DownloadLink)]

namespace DearImGuiInjection.MelonMono;

internal class DearImGuiInjectionMelonMono : MelonMod, ILoader, ILog
{
    public LoaderKind Kind => LoaderKind.MelonMono;

    public string ConfigPath => MelonEnvironment.UserDataDirectory;
    public string AssemblyPath => Path.GetDirectoryName(MelonAssembly.Location);

    public override void OnInitializeMelon()
    {
        if (!DearImGuiInjectionCore.Init(this, this))
            return;
        MelonPreferences.CreateCategory(
            DearImGuiInjectionCore.AllowUpMessagesCategory).CreateEntry(
            DearImGuiInjectionCore.AllowUpMessagesKey,
            DearImGuiInjectionCore.AllowUpMessagesDefaultValue,
            DearImGuiInjectionCore.AllowUpMessagesDescription);
        GameObject gameObject = new GameObject(DearImGuiInjectionMetadata.Name);
        gameObject.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.DontDestroyOnLoad(gameObject);
        gameObject.AddComponent<UnityMainThreadDispatcher>();
    }

    public override void OnDeinitializeMelon() => DearImGuiInjectionCore.Dispose();

    public void Debug(object data) => LoggerInstance.Msg(data);
    public void Error(object data) => LoggerInstance.Error(data);
    public void Fatal(object data) => LoggerInstance.Error(data);
    public new void Info(object data) => LoggerInstance.MsgPastel(data);
    public void Message(object data) => LoggerInstance.Msg(data);
    public void Warning(object data) => LoggerInstance.Warning(data);
}
