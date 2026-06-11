using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;

namespace ReplicaPaints;

// Per-SteamID loadout storage backed by a single JSON file. Each admin
// who configures their loadout via !ws / !knife / !gloves gets a
// PlayerLoadout entry keyed by their SteamID64 (string-encoded to
// preserve precision in the JSON).
//
// Bots don't go in here — their loadout is derived deterministically
// from persona name. See BotLoadoutResolver.
//
// File location: csgo/addons/counterstrikesharp/configs/plugins/
//                ReplicaPaints/players.json
//
// Format:
//   {
//     "76561198000000001": {
//       "weapons": {
//         "7":  { "paint": 1100, "seed": 0, "wear": 0.01, "stattrak": -1 },
//         "16": { "paint": 309,  "seed": 0, "wear": 0.05, "stattrak": -1 }
//       },
//       "knives_t":  515,
//       "knives_ct": 508,
//       "gloves_t":  { "defindex": 5032, "paint": 10037, "seed": 0, "wear": 0.05 },
//       "gloves_ct": { "defindex": 5034, "paint": 10037, "seed": 0, "wear": 0.05 }
//     }
//   }
public sealed class WeaponLoadout
{
    [JsonPropertyName("paint")]    public int   Paint    { get; set; }
    [JsonPropertyName("seed")]     public int   Seed     { get; set; }
    [JsonPropertyName("wear")]     public float Wear     { get; set; } = 0.01f;
    [JsonPropertyName("stattrak")] public int   StatTrak { get; set; } = -1; // -1 = off, >=0 = StatTrak with that count

    /// <summary>Custom nametag — applied via <c>CEconItemView.CustomName</c>.
    /// Empty string = no nametag. CS2 caps display length around 20 chars;
    /// longer strings get truncated server-side by the engine.</summary>
    [JsonPropertyName("nametag")]  public string Nametag  { get; set; } = "";

    /// <summary>Up to 4 sticker defindexes (slots 0..3). 0 = empty slot.
    /// Always serialized as a 4-element array — the apply path indexes
    /// directly by slot number, so the array shape matters.</summary>
    [JsonPropertyName("stickers")] public int[]  Stickers { get; set; } = new int[4];

    /// <summary>One keychain defindex; 0 = none. CS2 only exposes a
    /// single keychain slot per weapon (unlike the four sticker slots).</summary>
    [JsonPropertyName("keychain")] public int   Keychain { get; set; }

    /// <summary>Per-slot sticker placement (offset/scale/rotation/wear).
    /// Optional — null or missing entries fall back to 0/0/0/1/0
    /// (canonical centre, scale 1, rotation 0, wear 0) matching legacy
    /// behaviour. Issue #60: bots derive these deterministically from
    /// persona seed in BotLoadoutResolver so they aren't visibly uniform
    /// next to humans.</summary>
    [JsonPropertyName("sticker_placements")] public StickerPlacement[]? StickerPlacements { get; set; }

    /// <summary>Keychain placement (offset_x/y/z + seed for swing). When
    /// null, defaults to 0/0/0/0 — matches legacy behaviour. Bots get
    /// a deterministic non-zero seed from persona name; humans default
    /// to zero unless explicitly set via the API.</summary>
    [JsonPropertyName("keychain_placement")] public KeychainPlacement? KeychainPlacement { get; set; }
}

/// <summary>Per-sticker cosmetic placement on a weapon. CS2 attribute
/// names map 1:1: <c>sticker slot N offset x/y</c>, <c>wear</c>,
/// <c>scale</c>, <c>rotation</c>. Real-player loadouts routinely
/// non-zero these — bots that don't will look uniform on close
/// inspection. See issue #60.</summary>
public sealed class StickerPlacement
{
    [JsonPropertyName("offset_x")] public float OffsetX  { get; set; }
    [JsonPropertyName("offset_y")] public float OffsetY  { get; set; }
    [JsonPropertyName("wear")]     public float Wear     { get; set; }
    [JsonPropertyName("scale")]    public float Scale    { get; set; } = 1f;
    [JsonPropertyName("rotation")] public float Rotation { get; set; }
}

/// <summary>Per-keychain placement (single slot per weapon). Seed drives
/// the dangle/swing animation phase server-side; non-zero seeds make
/// bot loadouts visually distinguishable. See issue #60.</summary>
public sealed class KeychainPlacement
{
    [JsonPropertyName("offset_x")] public float OffsetX { get; set; }
    [JsonPropertyName("offset_y")] public float OffsetY { get; set; }
    [JsonPropertyName("offset_z")] public float OffsetZ { get; set; }
    [JsonPropertyName("seed")]     public int   Seed    { get; set; }
}

public sealed class GloveLoadout
{
    [JsonPropertyName("defindex")] public int   Defindex { get; set; }
    [JsonPropertyName("paint")]    public int   Paint    { get; set; }
    [JsonPropertyName("seed")]     public int   Seed     { get; set; }
    [JsonPropertyName("wear")]     public float Wear     { get; set; } = 0.05f;
}

public sealed class PlayerLoadout
{
    /// <summary>Weapon defindex (e.g. 7=AK-47) -> paint info.</summary>
    [JsonPropertyName("weapons")]
    public Dictionary<int, WeaponLoadout> Weapons { get; set; } = new();

    /// <summary>Chosen T-side knife defindex (0 = default/none).</summary>
    [JsonPropertyName("knives_t")]
    public int KnifeT { get; set; }

    /// <summary>Chosen CT-side knife defindex (0 = default/none).</summary>
    [JsonPropertyName("knives_ct")]
    public int KnifeCT { get; set; }

    [JsonPropertyName("gloves_t")]
    public GloveLoadout? GlovesT { get; set; }

    [JsonPropertyName("gloves_ct")]
    public GloveLoadout? GlovesCT { get; set; }

    /// <summary>Chosen T-side agent defindex (0 = stock model). Looked up
    /// in PaintsDatabase.AgentByDef at apply time.</summary>
    [JsonPropertyName("agent_t")]
    public int AgentT { get; set; }

    /// <summary>Chosen CT-side agent defindex (0 = stock model).</summary>
    [JsonPropertyName("agent_ct")]
    public int AgentCT { get; set; }

    /// <summary>Music kit defindex (0 = stock). Global, not per-team —
    /// CS2 only has one MusicKitID slot on the controller.</summary>
    [JsonPropertyName("music_kit")]
    public int MusicKit { get; set; }

    /// <summary>Pin / collectible defindex per side (0 = none). Shows up
    /// in the scoreboard next to the player name.</summary>
    [JsonPropertyName("pin_t")]
    public int PinT { get; set; }

    [JsonPropertyName("pin_ct")]
    public int PinCT { get; set; }
}

/// <summary>Named-collection wrapper around one or more loadouts for a
/// single SteamID. Lets admins maintain presets (`asiimov`, `fade`,
/// `cheap`) and switch the active one without losing the rest.</summary>
public sealed class PlayerLayouts
{
    /// <summary>Name of the layout currently in effect. Looked up in the
    /// <see cref="Layouts"/> dict; falls back to the first entry if the
    /// name doesn't match any key (defensive against hand-edits).</summary>
    [JsonPropertyName("active")]
    public string Active { get; set; } = DefaultName;

    [JsonPropertyName("layouts")]
    public Dictionary<string, PlayerLoadout> Layouts { get; set; } = new();

    public const string DefaultName = "default";

    /// <summary>Return the active loadout, or create + activate a default
    /// one if the wrapper is empty.</summary>
    public PlayerLoadout ResolveActive()
    {
        if (Layouts.Count == 0)
        {
            Layouts[DefaultName] = new PlayerLoadout();
            Active = DefaultName;
        }
        if (Layouts.TryGetValue(Active, out var ld)) return ld;
        // Active name doesn't match any layout — fall back to the first
        // entry and rewrite Active so the next load is consistent.
        foreach (var kv in Layouts)
        {
            Active = kv.Key;
            return kv.Value;
        }
        var fresh = new PlayerLoadout();
        Layouts[DefaultName] = fresh;
        Active = DefaultName;
        return fresh;
    }
}

public sealed class PlayersStore
{
    // SteamID64 -> wrapper holding all named layouts + which one is active.
    // ConcurrentDictionary because chat commands fire on the main game
    // thread but our save path queues onto a worker; the read path can
    // run from ApplyService on the same thread.
    private readonly ConcurrentDictionary<ulong, PlayerLayouts> _byId = new();
    private string _path = "";
    private readonly object _saveLock = new();

    public static string DefaultPath =>
        Path.Combine(Server.GameDirectory, "csgo", "addons", "counterstrikesharp",
                     "configs", "plugins", "ReplicaPaints", "players.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Load(string? path = null)
    {
        _path = path ?? DefaultPath;
        _byId.Clear();
        try
        {
            if (!File.Exists(_path))
            {
                Log.Info($"players.json not found at {_path}; starting empty");
                return;
            }
            var json = File.ReadAllText(_path);
            // Two on-disk formats coexist:
            //   v1 (legacy): { "<steamid>": <PlayerLoadout fields...> }
            //   v2 (current): { "<steamid>": { "active": "...", "layouts": { ... } } }
            // We detect per-entry: if the value has a "layouts" property,
            // it's v2; otherwise the whole object is a single PlayerLoadout
            // that we wrap into a v2 with one "default" layout. This keeps
            // existing players.json files working without a one-shot
            // migration step.
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!ulong.TryParse(prop.Name, out var id))
                {
                    Log.Warn($"players.json: skipping non-numeric key '{prop.Name}'");
                    continue;
                }
                PlayerLayouts entry;
                if (prop.Value.ValueKind == JsonValueKind.Object
                    && prop.Value.TryGetProperty("layouts", out var layoutsEl)
                    && layoutsEl.ValueKind == JsonValueKind.Object)
                {
                    entry = prop.Value.Deserialize<PlayerLayouts>(JsonOpts)
                            ?? new PlayerLayouts();
                }
                else
                {
                    var legacy = prop.Value.Deserialize<PlayerLoadout>(JsonOpts)
                                 ?? new PlayerLoadout();
                    entry = new PlayerLayouts
                    {
                        Active  = PlayerLayouts.DefaultName,
                        Layouts = { [PlayerLayouts.DefaultName] = legacy },
                    };
                }
                entry.ResolveActive();  // backfill empty wrapper if needed
                _byId[id] = entry;
            }
            Log.Info($"PlayersStore: loaded {_byId.Count} player layouts from {_path}");
        }
        catch (Exception ex)
        {
            Log.Error($"PlayersStore load failed: {ex.Message}");
        }
    }

    public void Save()
    {
        if (string.IsNullOrEmpty(_path)) _path = DefaultPath;
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                // Serialize as string-keyed dict (System.Text.Json doesn't
                // do non-string Dictionary keys without extra plumbing).
                var raw = new Dictionary<string, PlayerLayouts>(_byId.Count);
                foreach (var kv in _byId) raw[kv.Key.ToString()] = kv.Value;
                File.WriteAllText(_path, JsonSerializer.Serialize(raw, JsonOpts));
            }
            catch (Exception ex)
            {
                Log.Error($"PlayersStore save failed: {ex.Message}");
            }
        }
    }

    private PlayerLayouts GetOrCreateWrapper(ulong steamId64)
    {
        return _byId.GetOrAdd(steamId64, _ =>
        {
            var w = new PlayerLayouts();
            w.ResolveActive();
            return w;
        });
    }

    /// <summary>Returns the currently active loadout, creating an empty
    /// default if the SteamID has never been seen. Used by chat commands
    /// when admins set a paint mid-game.</summary>
    public PlayerLoadout GetOrCreate(ulong steamId64)
    {
        return GetOrCreateWrapper(steamId64).ResolveActive();
    }

    /// <summary>Returns the active loadout if the SteamID is known. This
    /// is the ApplyService entrypoint — bots skip past this anyway, and
    /// for humans only the active layout matters.</summary>
    public PlayerLoadout? TryGet(ulong steamId64)
    {
        return _byId.TryGetValue(steamId64, out var v) ? v.ResolveActive() : null;
    }

    /// <summary>Returns the full layout wrapper (all named layouts + which
    /// one is active). Used by the web panel's layouts management API.
    /// Returns null if the SteamID is unknown — caller decides whether to
    /// auto-create.</summary>
    public PlayerLayouts? TryGetLayouts(ulong steamId64)
    {
        return _byId.TryGetValue(steamId64, out var v) ? v : null;
    }

    public PlayerLayouts GetOrCreateLayouts(ulong steamId64) => GetOrCreateWrapper(steamId64);

    /// <summary>Replace the *active* layout's loadout at this SteamID.
    /// Used by the existing PUT /api/players/{id} endpoint, which the UI
    /// still treats as a single-loadout edit (it doesn't have to know
    /// about layouts when editing the currently-active one).</summary>
    public void Put(ulong steamId64, PlayerLoadout loadout)
    {
        var w = GetOrCreateWrapper(steamId64);
        w.Layouts[w.Active] = loadout;
    }

    /// <summary>Create or replace a named layout. If <paramref name="activate"/>
    /// is true, also flip <see cref="PlayerLayouts.Active"/> to this name.</summary>
    public void SetLayout(ulong steamId64, string name, PlayerLoadout loadout, bool activate)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var w = GetOrCreateWrapper(steamId64);
        w.Layouts[name] = loadout;
        if (activate) w.Active = name;
    }

    /// <summary>Switch which named layout is active. Returns false if the
    /// name doesn't exist — caller should treat that as a 404.</summary>
    public bool ActivateLayout(ulong steamId64, string name)
    {
        if (!_byId.TryGetValue(steamId64, out var w)) return false;
        if (!w.Layouts.ContainsKey(name)) return false;
        w.Active = name;
        return true;
    }

    /// <summary>Remove a named layout. The "default" name is protected so
    /// the wrapper always has at least one entry to fall back to. If the
    /// removed layout was active, falls back to "default".</summary>
    public bool RemoveLayout(ulong steamId64, string name)
    {
        if (name == PlayerLayouts.DefaultName) return false;
        if (!_byId.TryGetValue(steamId64, out var w)) return false;
        if (!w.Layouts.Remove(name)) return false;
        if (w.Active == name) w.Active = PlayerLayouts.DefaultName;
        // ResolveActive will repopulate "default" if it was removed too.
        w.ResolveActive();
        return true;
    }

    public bool Remove(ulong steamId64) => _byId.TryRemove(steamId64, out _);

    /// <summary>Snapshot of every stored player's *active* loadout, for
    /// the existing GET /api/players endpoint. The UI's roster rendering
    /// only needs the active loadout's fields, so we flatten the layouts
    /// wrapper here to keep the v1 response shape and avoid a UI rewrite
    /// for the simple list view. Layout management lives on the separate
    /// /api/players/{id}/layouts endpoint.</summary>
    public Dictionary<string, PlayerLoadout> Snapshot()
    {
        var dst = new Dictionary<string, PlayerLoadout>(_byId.Count);
        foreach (var kv in _byId) dst[kv.Key.ToString()] = kv.Value.ResolveActive();
        return dst;
    }

    /// <summary>Full layout-wrapper snapshot for the new
    /// /api/players-with-layouts endpoint (currently unused but exposed
    /// for completeness / debugging). Per-player layouts are fetched
    /// individually via GET /api/players/{id}/layouts in the UI flow.</summary>
    public Dictionary<string, PlayerLayouts> LayoutsSnapshot()
    {
        var dst = new Dictionary<string, PlayerLayouts>(_byId.Count);
        foreach (var kv in _byId) dst[kv.Key.ToString()] = kv.Value;
        return dst;
    }
}
