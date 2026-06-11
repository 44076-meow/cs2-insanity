using System;
using System.Collections.Generic;
using System.IO;
using CounterStrikeSharp.API;

namespace InsanityRevive;

// Plain key="value" parser for cfg/replica.cfg. We bypass cvars entirely
// — registering FakeConVars and then waiting for ExecuteCommand to apply
// them is async and order-dependent; reading a file is synchronous and
// deterministic. The cfg syntax is the same the user expects from the
// console, so they can keep editing the same file.
public sealed class Config
{
    private readonly Dictionary<string, string> _kv = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string LogsRoot =
        Path.Combine(Server.GameDirectory, "csgo", "addons", "counterstrikesharp", "logs");
    private static readonly string CfgPath =
        Path.Combine(Server.GameDirectory, "csgo", "cfg", "replica.cfg");

    public Config()
    {
        if (!File.Exists(CfgPath))
        {
            Log.Warn($"replica.cfg not found at {CfgPath} — using defaults");
            return;
        }

        foreach (var raw in File.ReadAllLines(CfgPath))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("//") || line.StartsWith("#")) continue;
            // Format: key "value"   or   key value
            var space = line.IndexOf(' ');
            if (space < 0) continue;
            var key = line[..space].Trim();
            var val = line[(space + 1)..].Trim().Trim('"');
            if (key.Length > 0) _kv[key] = val;
        }
    }

    public int    DefaultBotCount  => GetInt("replica_default_bot_count", 5);
    public string LogLevel         => Get("replica_log_level", "info");
    public bool   ApplyBotNavPatch => GetInt("replica_apply_botnav_patch", 0) != 0;
    public string TelemetryPath    => Resolve(Get("replica_telemetry_path",
        Path.Combine(LogsRoot, "insanity", "{date}_{session}.jsonl")));

    /// <summary>
    /// FleetManager target — number of fake-clients held resident on the
    /// server. Default 8, clamped 0..16. Edit replica.cfg + reload plugin
    /// for persistent change, or `replica_fleet_size N` for runtime override.
    /// 0 = empty fleet (kick everyone, hold empty). (v0.6.0+; runtime
    /// override + zero-allowed in v0.6.0.7-beta.)
    /// </summary>
    public int    FleetSize        => Math.Clamp(_fleetSizeOverride ?? GetInt("replica_fleet_size", 8), 0, 16);

    private int? _fleetSizeOverride;
    public int? FleetSizeOverride => _fleetSizeOverride;
    public bool HasFleetSizeOverride => _fleetSizeOverride.HasValue;
    /// <summary>
    /// Runtime override for FleetSize. Pass null to clear and fall back to
    /// cfg-file value. Clamped 0..16 inside FleetSize getter. Used by
    /// `replica_fleet_size` and `replica_kick_bots` (which sets to 0 to
    /// keep the fleet drained until the user restores it).
    /// </summary>
    public void SetFleetSizeOverride(int? n) => _fleetSizeOverride = n;

    /// <summary>
    /// Reveal Stage 2 trigger thresholds. Stage 2 fires when
    /// `min(stage2_time_seconds, stage2_kills)` is reached. Default 45s
    /// kills=ceil(fleet_size/2) — `kills` value -1 means "auto" (compute
    /// from FleetSize at runtime). (v0.6.0+)
    /// </summary>
    public int    Stage2TimeSeconds => Math.Max(1, GetInt("replica_reveal_stage2_time", 45));
    public int    Stage2Kills       => GetInt("replica_reveal_stage2_kills", -1);

    /// <summary>
    /// On Stage 3 cleanup, issue `mp_restartgame 1` to revive killed
    /// humans so the next `!reveal` has live targets. Default true
    /// (re-runnable spec). (v0.6.0+)
    /// </summary>
    public bool   RevealAutoRestart => GetInt("replica_reveal_auto_restart", 1) != 0;

    private string Get(string k, string fallback)
        => _kv.TryGetValue(k, out var v) && v.Length > 0 ? v : fallback;

    private int GetInt(string k, int fallback)
        => int.TryParse(Get(k, fallback.ToString()), out var v) ? v : fallback;

    private static string Resolve(string p)
        => Path.IsPathRooted(p) ? p : Path.Combine(Server.GameDirectory, p);
}
