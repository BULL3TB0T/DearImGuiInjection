using System.Runtime.CompilerServices;

namespace DearImGuiInjection;

internal interface ILog
{
    public void Debug(object data);
    public void Error(object data);
    public void Fatal(object data);
    public void Info(object data);
    public void Message(object data);
    public void Warning(object data);
}

internal static class Log
{
    private static ILog _log;

    public static void Init(ILog log) => _log = log;

    public static void Debug(object data) => _log.Debug(data);
    public static void Error(object data) => _log.Error(data);
    public static void Fatal(object data) => _log.Fatal(data);
    public static void Info(object data) => _log.Info(data);
    public static void Message(object data) => _log.Message(data);
    public static void Warning(object data) => _log.Warning(data);
}