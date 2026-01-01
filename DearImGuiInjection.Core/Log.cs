using System.Runtime.CompilerServices;

namespace DearImGuiInjection;

internal static class Log
{
    private static ILoader _loader;

    public static void Init(ILoader log) => _loader = log;

    public static void Debug(object data) => _loader.Debug(data);
    public static void Error(object data) => _loader.Error(data);
    public static void Fatal(object data) => _loader.Fatal(data);
    public static void Info(object data) => _loader.Info(data);
    public static void Message(object data) => _loader.Message(data);
    public static void Warning(object data) => _loader.Warning(data);
}