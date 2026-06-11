using System;
using CounterStrikeSharp.API;

namespace ReplicaPaints;

public enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

public static class Log
{
    public static LogLevel Level = LogLevel.Info;

    public static void Debug(string msg) { Emit(LogLevel.Debug, msg, /*bg*/false); }
    public static void Info(string msg)  { Emit(LogLevel.Info,  msg, /*bg*/false); }
    public static void Warn(string msg)  { Emit(LogLevel.Warn,  msg, /*bg*/false); }
    public static void Error(string msg) { Emit(LogLevel.Error, msg, /*bg*/false); }

    /// <summary>Logger variant that's safe to call from non-game
    /// threads. <see cref="Server.PrintToConsole"/> throws a
    /// NativeException("Invoked on a non-main thread") if used off the
    /// game tick — the WebServer worker thread learned this the hard
    /// way. These variants write to stdout instead, which the server's
    /// `tee` redirects into server.log alongside game output.</summary>
    public static void BgDebug(string msg) { Emit(LogLevel.Debug, msg, /*bg*/true); }
    public static void BgInfo(string msg)  { Emit(LogLevel.Info,  msg, /*bg*/true); }
    public static void BgWarn(string msg)  { Emit(LogLevel.Warn,  msg, /*bg*/true); }
    public static void BgError(string msg) { Emit(LogLevel.Error, msg, /*bg*/true); }

    private static void Emit(LogLevel lvl, string msg, bool bg)
    {
        if (lvl < Level) return;
        var tag = lvl switch
        {
            LogLevel.Debug => "DBUG",
            LogLevel.Info  => "INFO",
            LogLevel.Warn  => "WARN",
            LogLevel.Error => "EROR",
            _ => "????",
        };
        var line = $"[Paints][{tag}] {msg}";
        if (bg) Console.WriteLine(line);
        else    Server.PrintToConsole(line + "\n");
    }

    public static void SetLevel(string s)
    {
        switch ((s ?? "info").Trim().ToLowerInvariant())
        {
            case "debug": Level = LogLevel.Debug; break;
            case "info":  Level = LogLevel.Info;  break;
            case "warn":  Level = LogLevel.Warn;  break;
            case "error": Level = LogLevel.Error; break;
            default:      Level = LogLevel.Info;  break;
        }
    }
}
