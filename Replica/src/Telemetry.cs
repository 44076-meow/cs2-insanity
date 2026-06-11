using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Replica;

// JSONL sink. One thread (main game thread) writes; we hold a single
// FileStream and flush after every record so a crash never loses
// trailing records. Path placeholders {date}/{session} expanded once.
public sealed class Telemetry : IDisposable
{
    private readonly object _gate = new();
    private FileStream? _fs;
    private StreamWriter? _sw;
    private readonly string _sessionId;
    private readonly string _resolvedPath;
    private bool _disposed;

    public string Path => _resolvedPath;

    public Telemetry(string template)
    {
        _sessionId = Guid.NewGuid().ToString("N")[..8];
        var date = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        _resolvedPath = template
            .Replace("{date}", date)
            .Replace("{session}", _sessionId);

        try
        {
            var dir = System.IO.Path.GetDirectoryName(_resolvedPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _fs = new FileStream(_resolvedPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _sw = new StreamWriter(_fs, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            Log.Error($"telemetry open failed: {ex.Message}");
            _fs?.Dispose(); _fs = null;
        }
    }

    public string SessionId => _sessionId;

    public void Write(string kind, IReadOnlyDictionary<string, object?> fields)
    {
        if (_disposed || _sw == null) return;
        var sb = new StringBuilder(128);
        sb.Append('{');
        sb.Append("\"kind\":");
        sb.Append(JsonSerializer.Serialize(kind));
        sb.Append(",\"t\":\"");
        sb.Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        sb.Append('"');
        foreach (var kv in fields)
        {
            sb.Append(',');
            sb.Append(JsonSerializer.Serialize(kv.Key));
            sb.Append(':');
            sb.Append(JsonSerializer.Serialize(kv.Value));
        }
        sb.Append('}');
        lock (_gate)
        {
            try { _sw.WriteLine(sb.ToString()); }
            catch (Exception ex) { Log.Error($"telemetry write failed: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            try { _sw?.Flush(); _sw?.Dispose(); _fs?.Dispose(); } catch { }
            _sw = null; _fs = null;
        }
    }
}
