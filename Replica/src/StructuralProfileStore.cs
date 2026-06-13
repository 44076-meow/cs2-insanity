// =============================================================================
// StructuralProfileStore.cs — loads recorded per-player structural aim biases
// and exposes them per persona name for the bot aim layer.
//
// Closes the record→profile→bot loop (issue #44 direction). The recorder
// (PlayerRecorder) captures human play; scripts/replay-structural-profile.py
// distills DETERMINISTIC per-weapon aim biases ("AK always +0.26° down,
// -0.14° yaw") into recordings/<session>/_meta/structural_<name>.json. Until
// now nothing consumed them — bot aim was pure RNG scatter. This store reads
// the distilled medians so AimController can apply them as fixed per-persona
// flaws (gated behind replica_aim_structural, OFF by default).
//
// We trust only `aim_bias_per_weapon` — the Python side already quality-gates
// it (low-n / outlier weapons land in `aim_bias_rejected`, which we ignore).
// Per persona name we keep the MOST RECENT session that has a file (median
// merging across sessions without raw samples is statistically unsound; newest
// is clean and defensible). Load failures are non-fatal: an empty store just
// means the feature no-ops when enabled.
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Replica;

public static class StructuralProfileStore
{
    // persona name → (weapon designer name → median (yaw, pitch) bias degrees)
    private static Dictionary<string, Dictionary<string, (float Yaw, float Pitch)>> _byName
        = new(StringComparer.Ordinal);
    public static int LoadedProfiles => _byName.Count;
    public static string? LoadedFrom { get; private set; }

    // Candidate recordings roots, first existing wins. The deployment keeps
    // recordings as a sibling of game/ under the server root; HOME path is the
    // symlinked equivalent. Kept as a probe list (not a hard constant) so a
    // moved install degrades to "feature no-ops" instead of a wrong path.
    private static IEnumerable<string> CandidateRoots()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home)) yield return Path.Combine(home, "cs2-server", "recordings");
        yield return "/mnt/storage/cs2-server/recordings";
    }

    /// <summary>Scan recordings, build the name→weapon→bias map. Idempotent;
    /// safe to call again to pick up new sessions (e.g. on mapstart).</summary>
    public static void Load()
    {
        var map = new Dictionary<string, Dictionary<string, (float, float)>>(StringComparer.Ordinal);
        string? root = CandidateRoots().FirstOrDefault(Directory.Exists);
        LoadedFrom = root;
        if (root == null) { _byName = map; Log.Info("StructuralProfileStore: no recordings dir found — structural aim will no-op"); return; }

        // Sessions are timestamp-prefixed dirs; sort ascending so the newest
        // overwrites older entries per name (last-writer-wins == most recent).
        IEnumerable<string> sessions;
        try { sessions = Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal); }
        catch (Exception ex) { Log.Warn($"StructuralProfileStore: enumerate {root}: {ex.Message}"); _byName = map; return; }

        int files = 0;
        foreach (var session in sessions)
        {
            var metaDir = Path.Combine(session, "_meta");
            if (!Directory.Exists(metaDir)) continue;
            string[] profiles;
            try { profiles = Directory.GetFiles(metaDir, "structural_*.json"); }
            catch { continue; }
            foreach (var path in profiles)
            {
                var parsed = TryParse(path);
                if (parsed.HasValue) { map[parsed.Value.Name] = parsed.Value.Weapons; files++; }
            }
        }
        _byName = map;
        Log.Info($"StructuralProfileStore: loaded {map.Count} persona profile(s) from {files} file(s) under {root}");
    }

    private static (string Name, Dictionary<string, (float, float)> Weapons)? TryParse(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var rootEl = doc.RootElement;
            if (!rootEl.TryGetProperty("name", out var nameEl)) return null;
            var name = nameEl.GetString();
            if (string.IsNullOrEmpty(name)) return null;
            if (!rootEl.TryGetProperty("aim_bias_per_weapon", out var weaponsEl)
                || weaponsEl.ValueKind != JsonValueKind.Object) return null;

            var weapons = new Dictionary<string, (float, float)>(StringComparer.Ordinal);
            foreach (var w in weaponsEl.EnumerateObject())
            {
                var o = w.Value;
                // Use median (robust to outliers) for the deterministic bias.
                float yaw = ReadFloat(o, "yaw_median");
                float pitch = ReadFloat(o, "pitch_median");
                // Skip degenerate / absurd entries defensively (the Python
                // gate should have caught these, but never trust >15° flaws).
                if (Math.Abs(yaw) > 15f || Math.Abs(pitch) > 15f) continue;
                weapons[w.Name] = (yaw, pitch);
            }
            return weapons.Count == 0 ? null : (name, weapons);
        }
        catch (Exception ex)
        {
            Log.Debug($"StructuralProfileStore: parse {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private static float ReadFloat(JsonElement o, string prop)
        => o.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number
            ? (float)el.GetDouble() : 0f;

    /// <summary>Per-weapon bias map for a persona, or null if none recorded.
    /// AimController caches this reference per bot at adopt time.</summary>
    public static Dictionary<string, (float Yaw, float Pitch)>? TryGet(string personaName)
        => _byName.TryGetValue(personaName, out var w) ? w : null;
}
