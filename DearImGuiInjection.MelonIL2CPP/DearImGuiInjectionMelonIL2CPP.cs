using DearImGuiInjection;
using DearImGuiInjection.MelonIL2CPP;
using DearImGuiInjection.Windows;
using Il2CppInterop.Runtime.Injection;
using MelonLoader;
using MelonLoader.Utils;
using System.IO;
using System.Reflection;
using UnityEngine;

[assembly: MelonInfo(typeof(DearImGuiInjectionMelonIL2CPP), DearImGuiInjectionMetadata.Name, DearImGuiInjectionMetadata.Version, DearImGuiInjectionMetadata.Author, DearImGuiInjectionMetadata.DownloadLink)]

namespace DearImGuiInjection.MelonIL2CPP;

internal class DearImGuiInjectionMelonIL2CPP : MelonMod, ILoader, ILog
{
    public LoaderKind Kind => LoaderKind.MelonIL2CPP;

    public string ConfigPath => MelonEnvironment.UserDataDirectory;
    public string AssemblyPath => Path.GetDirectoryName(MelonAssembly.Location);

    public override void OnInitializeMelon()
    {
        if (!DearImGuiInjectionCore.Init(this, this))
            return;
        ClassInjector.RegisterTypeInIl2Cpp<UnityMainThreadDispatcher>();
        var gameObject = new GameObject(DearImGuiInjectionMetadata.Name);
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
