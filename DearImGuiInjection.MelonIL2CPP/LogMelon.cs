using DearImGuiInjection;
using MelonLoader;

namespace DearImGuiInjection.MelonIL2CPP;

internal class LogMelon : ILog
{
    private MelonLogger.Instance _logSource;

    internal LogMelon(MelonLogger.Instance logSource) => _logSource = logSource;

    void ILog.Debug(object data, string file, string member, int line) => _logSource.Msg(Log.Format(data, file, member, line));
    void ILog.Error(object data, string file, string member, int line) => _logSource.Error(Log.Format(data, file, member, line));
    void ILog.Fatal(object data, string file, string member, int line) => _logSource.BigError(Log.Format(data, file, member, line));
    void ILog.Info(object data, string file, string member, int line) => _logSource.MsgPastel(Log.Format(data, file, member, line));
    void ILog.Message(object data, string file, string member, int line) => _logSource.Msg(Log.Format(data, file, member, line));
    void ILog.Warning(object data, string file, string member, int line) => _logSource.Warning(Log.Format(data, file, member, line));
}
