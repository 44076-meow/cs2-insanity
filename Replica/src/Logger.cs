using System;
using CounterStrikeSharp.API;

namespace Replica;

public enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

public static class Log
{
    public static LogLevel Level = LogLevel.Info;

    public static void Debug(string msg) { Emit(LogLevel.Debug, msg); }
    public static void Info(string msg)  { Emit(LogLevel.Info,  msg); }
    public static void Warn(string msg)  { Emit(LogLevel.Warn,  msg); }
    public static void Error(string msg) { Emit(LogLevel.Error, msg); }

    private static void Emit(LogLevel lvl, string msg)
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
        Server.PrintToConsole($"[Replica][{tag}] {msg}\n");
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
