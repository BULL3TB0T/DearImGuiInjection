using BepInEx.Logging;
using DearImGuiInjection;

namespace DearImGuiInjection.BepInExIL2CPP;

internal class LogBepInEx : ILog
{
    private ManualLogSource _logSource;

    internal LogBepInEx(ManualLogSource logSource) => _logSource = logSource;

    void ILog.Debug(object data, string file, string member, int line) => _logSource.LogDebug(Log.Format(data, file, member, line));
    void ILog.Error(object data, string file, string member, int line) => _logSource.LogError(Log.Format(data, file, member, line));
    void ILog.Fatal(object data, string file, string member, int line) => _logSource.LogFatal(Log.Format(data, file, member, line));
    void ILog.Info(object data, string file, string member, int line) => _logSource.LogInfo(Log.Format(data, file, member, line));
    void ILog.Message(object data, string file, string member, int line) => _logSource.LogMessage(Log.Format(data, file, member, line));
    void ILog.Warning(object data, string file, string member, int line) => _logSource.LogWarning(Log.Format(data, file, member, line));
}