using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;

namespace ReplicaPaints;

// Persona-keyed durable store for bot StatTrak counters.
//
// PlayersStore is keyed by SteamID64 and carries the full loadout
// (paints/seed/wear/nametag/...). For Replica bots the loadout itself
// is *derived* from the persona name via BotLoadoutResolver — there
// is no per-bot loadout JSON. The only thing that genuinely needs to
// persist for bots is the kill counter (so `ropz` keeps his 1337
// frags through restarts and mapchanges).
//
// File layout (configs/plugins/ReplicaPaints/bots.json):
//
//   {
//     "ropz":  { "weapons": { "7": 42, "9": 17 } },
//     "ZywOo": { "weapons": { "9": 81 } }
//   }
//
// "7" / "9" are weapon defindexes (AK / AWP), values are kill counts.
// Persona name is the same one BotLoadoutResolver hashes — it's read
// from the Replica pool slot at the moment of the kill, *not* from
// CCSPlayerController.PlayerName (which can race with the rename
// pass-through and briefly read "Bot01").
public sealed class BotsStore
{
    private readonly ConcurrentDictionary<string, BotStats> _byName = new();
    private string _path = "";

    public static string DefaultPath =>
        Path.Combine(Server.GameDirectory,
            "csgo", "addons", "counterstrikesharp", "configs",
            "plugins", "ReplicaPaints", "bots.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    public void Load(string? path = null)
    {
        _path = path ?? DefaultPath;
        _byName.Clear();
        try
        {
            if (!File.Exists(_path))
            {
                Log.Info($"BotsStore: no bots.json yet at {_path} — starting empty");
                return;
            }
            var json = File.ReadAllText(_path);
            var data = JsonSerializer.Deserialize<Dictionary<string, BotStats>>(json, JsonOpts);
            if (data == null) return;
            foreach (var kv in data) _byName[kv.Key] = kv.Value;
            Log.Info($"BotsStore: loaded stats for {_byName.Count} bot personas from {_path}");
        }
        catch (Exception ex)
        {
            Log.Error($"BotsStore.Load: {ex.Message}");
        }
    }

    public void Save()
    {
        if (string.IsNullOrEmpty(_path)) return;
        try
        {
            var dst = new Dictionary<string, BotStats>(_byName.Count);
            foreach (var kv in _byName) dst[kv.Key] = kv.Value;
            File.WriteAllText(_path, JsonSerializer.Serialize(dst, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.BgError($"BotsStore.Save: {ex.Message}");
        }
    }

    /// <summary>Look up the persistent StatTrak count for this persona +
    /// weapon. Returns 0 if no kill has been recorded yet.</summary>
    public int GetKills(string personaName, int defindex)
    {
        if (string.IsNullOrEmpty(personaName)) return 0;
        if (!_byName.TryGetValue(personaName, out var stats)) return 0;
        return stats.Weapons.TryGetValue(defindex, out var n) ? n : 0;
    }

    /// <summary>Increment the kill counter for a given persona + weapon.
    /// Returns the new count. Caller decides when to .Save() to disk.</summary>
    public int Increment(string personaName, int defindex)
    {
        if (string.IsNullOrEmpty(personaName)) return 0;
        var stats = _byName.GetOrAdd(personaName, _ => new BotStats());
        lock (stats)
        {
            stats.Weapons.TryGetValue(defindex, out var n);
            n++;
            stats.Weapons[defindex] = n;
            return n;
        }
    }

    /// <summary>Snapshot for the web panel.</summary>
    public Dictionary<string, BotStats> Snapshot()
    {
        var dst = new Dictionary<string, BotStats>(_byName.Count);
        foreach (var kv in _byName) dst[kv.Key] = kv.Value;
        return dst;
    }
}

public sealed class BotStats
{
    [JsonPropertyName("weapons")]
    public Dictionary<int, int> Weapons { get; set; } = new();
}
