using System;
using System.Collections.Generic;
using System.IO;

namespace Replica;

public interface ISteamIdProvider
{
    ulong Generate(int botSlot);
    string Mode { get; }
}

// Reserved synthetic range: 76561198_900_000_000 .. 76561198_999_999_999.
// Real Steam accounts populate the lower part of this prefix space, so
// staying above 900M minimizes any chance of collision with a live
// account. Slot is folded into the seed so the same slot under the same
// session always yields the same ID — useful for telemetry diffing.
public sealed class SyntheticSteamIdProvider : ISteamIdProvider
{
    public string Mode => "synthetic";

    private const ulong RangeBase = 76561198_900_000_000UL;
    private const ulong RangeSpan =          99_999_999UL;
    private readonly ulong _sessionSeed;

    public SyntheticSteamIdProvider(string sessionId)
    {
        unchecked
        {
            ulong h = 0xCBF29CE484222325UL;
            foreach (var c in sessionId) { h = (h ^ c) * 0x100000001B3UL; }
            _sessionSeed = h;
        }
    }

    public ulong Generate(int botSlot)
    {
        unchecked
        {
            ulong x = _sessionSeed ^ ((ulong)botSlot * 0x9E3779B97F4A7C15UL);
            x ^= x >> 33; x *= 0xFF51AFD7ED558CCDUL;
            x ^= x >> 33; x *= 0xC4CEB9FE1A85EC53UL;
            x ^= x >> 33;
            return RangeBase + (x % (RangeSpan + 1));
        }
    }
}

// Round-robin issuer. File is one ID per line; lines starting with '#'
// (or blank) skipped. Fatal if the file is missing or empty AND mode is
// "real" — silent fallback is explicitly forbidden.
public sealed class RealPoolSteamIdProvider : ISteamIdProvider
{
    public string Mode => "real";

    private readonly List<ulong> _pool = new();
    private readonly HashSet<ulong> _issued = new();
    private int _cursor;

    public RealPoolSteamIdProvider(string filePath)
    {
        if (!File.Exists(filePath))
            throw new InvalidOperationException(
                $"replica_steamid_mode=real but file not found: {filePath}");

        foreach (var raw in File.ReadAllLines(filePath))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#")) continue;
            if (!ulong.TryParse(line, out var id)) continue;
            if (id < 76561197960265728UL) continue; // not a SteamID64
            _pool.Add(id);
        }

        if (_pool.Count == 0)
            throw new InvalidOperationException(
                $"replica_steamid_mode=real but pool is empty: {filePath}");
    }

    public ulong Generate(int botSlot)
    {
        for (var i = 0; i < _pool.Count; i++)
        {
            var idx = (_cursor + i) % _pool.Count;
            var id  = _pool[idx];
            if (_issued.Contains(id)) continue;
            _issued.Add(id);
            _cursor = (idx + 1) % _pool.Count;
            return id;
        }
        // Pool exhausted — recycle from the start. Better than failing.
        var fallback = _pool[_cursor % _pool.Count];
        _cursor = (_cursor + 1) % _pool.Count;
        return fallback;
    }

    public int PoolSize => _pool.Count;
}

public static class SteamIdProviderFactory
{
    public static ISteamIdProvider Create(Config cfg, string sessionId)
    {
        var mode = (cfg.SteamIdMode ?? "synthetic").Trim().ToLowerInvariant();
        return mode switch
        {
            "real" => new RealPoolSteamIdProvider(cfg.RealSteamIdsFile),
            _      => new SyntheticSteamIdProvider(sessionId),
        };
    }
}
