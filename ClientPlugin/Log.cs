using System;
using VRage.Utils;

namespace ClientPlugin;

internal static class Log
{
    private const string Prefix = Plugin.Name + ": ";

    public static void Info(string message) => MyLog.Default.WriteLine(Prefix + message);

    // Not the const itself: the compiler would flag the code behind it as unreachable
    public static readonly bool DebugEnabled = Diagnostics.DebugLog;

    public static void Debug(string message)
    {
        if (DebugEnabled)
            MyLog.Default.WriteLine(Prefix + message);
    }

    public static void Error(Exception e) => MyLog.Default.Error(Prefix + e);
}
