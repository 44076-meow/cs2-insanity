using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;

namespace ReplicaPaints;

// Catalogs of every paintkit / knife / glove this plugin knows about.
// Loaded from three JSON files in configs/. The plugin ships with a
// starter set covering common paintkits — extending it is just appending
// entries; the chat menus paginate so any size works.
//
// Format (weapons_paints.json):
//   [
//     { "weapon_defindex": 7, "paint": 1100, "name": "AK-47 | Vulcan" },
//     ...
//   ]
//
// Format (knives.json):
//   [
//     { "defindex": 515, "weapon_name": "weapon_knife_butterfly",
//       "name": "Butterfly Knife" },
//     ...
//   ]
//
// Format (gloves.json):
//   [
//     { "defindex": 5032, "paint": 10037,
//       "name": "Specialist Gloves | Field Agent" },
//     ...
//   ]
public sealed class PaintEntry
{
    [JsonPropertyName("weapon_defindex")] public int    WeaponDefindex { get; set; }
    [JsonPropertyName("paint")]           public int    Paint          { get; set; }
    [JsonPropertyName("name")]            public string Name           { get; set; } = "";
    // Image URL is shipped per-row in the catalog file so the web UI can
    // render previews without a cross-origin fetch to a public CDN —
    // bymykel.github.io 301s to bymykel.com, which doesn't send a
    // permissive Access-Control-Allow-Origin, so browser fetches there
    // are blocked.
    [JsonPropertyName("image")]           public string Image          { get; set; } = "";
    // True for skins that ship on the pre-CS2 weapon mesh (most older
    // Operation skins). False for modern skins that use the new Source 2
    // model with a secondary design layer (Printstream / Doppler /
    // Marble Fade et al.). The apply path uses this to pick the right
    // bodygroup — body=1 for legacy, body=0 for modern. Without that
    // toggle, modern skins render only their base coat with no overlay,
    // hence the washed-out look.
    [JsonPropertyName("legacy_model")]    public bool   LegacyModel    { get; set; }
}

public sealed class KnifeEntry
{
    [JsonPropertyName("defindex")]    public int    Defindex   { get; set; }
    [JsonPropertyName("weapon_name")] public string WeaponName { get; set; } = "";
    [JsonPropertyName("name")]        public string Name       { get; set; } = "";
}

public sealed class GloveEntry
{
    [JsonPropertyName("defindex")] public int    Defindex { get; set; }
    [JsonPropertyName("paint")]    public int    Paint    { get; set; }
    [JsonPropertyName("name")]     public string Name     { get; set; } = "";
    [JsonPropertyName("image")]    public string Image    { get; set; } = "";
}

// Character model. `model` is the .vmdl path relative to the csgo content
// root (e.g. "agents/models/ctm_st6/ctm_st6_variantj.vmdl"). `team` is
// "T" or "CT" — agents are team-locked in CS2.
public sealed class AgentEntry
{
    [JsonPropertyName("defindex")] public int    Defindex { get; set; }
    [JsonPropertyName("team")]     public string Team     { get; set; } = "";
    [JsonPropertyName("model")]    public string Model    { get; set; } = "";
    [JsonPropertyName("name")]     public string Name     { get; set; } = "";
    [JsonPropertyName("image")]    public string Image    { get; set; } = "";
}

// Music kit (MVP anthem + round-start/end music override).
// Applied via CCSPlayerController.MusicKitID — global per-player, not
// per-team (CS2 doesn't expose a per-team kit slot).
public sealed class MusicKitEntry
{
    [JsonPropertyName("defindex")] public int    Defindex { get; set; }
    [JsonPropertyName("name")]     public string Name     { get; set; } = "";
    [JsonPropertyName("image")]    public string Image    { get; set; } = "";
}

// Collectible pin (5-year veteran coin, championship pin, etc.).
// Applied via CCSPlayerController.InventoryServices.Rank[5] — per-team
// slot, separate from rank/badges in other indexes.
public sealed class PinEntry
{
    [JsonPropertyName("defindex")] public int    Defindex { get; set; }
    [JsonPropertyName("name")]     public string Name     { get; set; } = "";
    [JsonPropertyName("image")]    public string Image    { get; set; } = "";
}

// Sticker (printed on weapons in slots 0..3). Applied via attribute
// injection — needs the CAttributeList_SetOrAddAttributeValueByName
// gamedata signature.
public sealed class StickerEntry
{
    [JsonPropertyName("defindex")] public int    Defindex { get; set; }
    [JsonPropertyName("name")]     public string Name     { get; set; } = "";
    [JsonPropertyName("image")]    public string Image    { get; set; } = "";
}

// Keychain / charm. Hangs from the weapon at slot 0 — CS2 only exposes
// one keychain slot per weapon, not four like stickers.
public sealed class KeychainEntry
{
    [JsonPropertyName("defindex")] public int    Defindex { get; set; }
    [JsonPropertyName("name")]     public string Name     { get; set; } = "";
    [JsonPropertyName("image")]    public string Image    { get; set; } = "";
}

public sealed class PaintsDatabase
{
    public List<PaintEntry>     Weapons   { get; private set; } = new();
    public List<KnifeEntry>     Knives    { get; private set; } = new();
    public List<GloveEntry>     Gloves    { get; private set; } = new();
    public List<AgentEntry>     Agents    { get; private set; } = new();
    public List<MusicKitEntry>  MusicKits { get; private set; } = new();
    public List<PinEntry>       Pins      { get; private set; } = new();
    public List<StickerEntry>   Stickers  { get; private set; } = new();
    public List<KeychainEntry>  Keychains { get; private set; } = new();

    // Per-team agent indexes — built once after load so the resolver and
    // web UI don't have to filter the flat list every spawn.
    public List<AgentEntry> AgentsT  { get; private set; } = new();
    public List<AgentEntry> AgentsCT { get; private set; } = new();

    // Defindex -> agent for O(1) lookup when applying a saved selection.
    private readonly Dictionary<int, AgentEntry> _agentByDef = new();
    public AgentEntry? AgentByDef(int defindex)
        => _agentByDef.TryGetValue(defindex, out var a) ? a : null;

    // Index: defindex -> list of paint entries for that weapon. Built once
    // after load so chat menus and bot loadout resolution are O(1) instead
    // of repeatedly scanning the full list.
    private readonly Dictionary<int, List<PaintEntry>> _byWeapon = new();

    public IReadOnlyList<PaintEntry> ForWeapon(int defindex)
    {
        return _byWeapon.TryGetValue(defindex, out var list)
            ? list
            : (IReadOnlyList<PaintEntry>)Array.Empty<PaintEntry>();
    }

    /// <summary>True if the (defindex, paint) pair is a legacy-mesh
    /// skin. Defaults to true on unknown pairs because the legacy
    /// bodygroup is the safer default — most older skins are legacy and
    /// applying body=0 to them shows only the base coat. Modern skins
    /// (Printstream / Doppler) override to false in the catalog.</summary>
    public bool IsLegacyPaint(int defindex, int paint)
    {
        var list = ForWeapon(defindex);
        for (int i = 0; i < list.Count; i++)
            if (list[i].Paint == paint) return list[i].LegacyModel;
        return true;
    }

    public IReadOnlyCollection<int> WeaponDefindexes => _byWeapon.Keys;

    public static string DefaultRoot =>
        Path.Combine(Server.GameDirectory, "csgo", "addons", "counterstrikesharp",
                     "configs", "plugins", "ReplicaPaints");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public void LoadFrom(string? root = null)
    {
        root ??= DefaultRoot;
        Weapons   = LoadList<PaintEntry>(Path.Combine(root, "weapons_paints.json"));
        Knives    = LoadList<KnifeEntry>(Path.Combine(root, "knives.json"));
        Gloves    = LoadList<GloveEntry>(Path.Combine(root, "gloves.json"));
        Agents    = LoadList<AgentEntry>(Path.Combine(root, "agents.json"));
        MusicKits = LoadList<MusicKitEntry>(Path.Combine(root, "music_kits.json"));
        Pins      = LoadList<PinEntry>(Path.Combine(root, "pins.json"));
        Stickers  = LoadList<StickerEntry>(Path.Combine(root, "stickers.json"));
        Keychains = LoadList<KeychainEntry>(Path.Combine(root, "keychains.json"));

        _byWeapon.Clear();
        foreach (var e in Weapons)
        {
            if (!_byWeapon.TryGetValue(e.WeaponDefindex, out var list))
            {
                list = new List<PaintEntry>();
                _byWeapon[e.WeaponDefindex] = list;
            }
            list.Add(e);
        }

        AgentsT.Clear();
        AgentsCT.Clear();
        _agentByDef.Clear();
        foreach (var a in Agents)
        {
            if (a.Defindex <= 0 || string.IsNullOrEmpty(a.Model)) continue;
            _agentByDef[a.Defindex] = a;
            if      (a.Team == "T")  AgentsT.Add(a);
            else if (a.Team == "CT") AgentsCT.Add(a);
        }

        Log.Info($"PaintsDatabase: {Weapons.Count} paintkits across {_byWeapon.Count} weapons, "
               + $"{Knives.Count} knives, {Gloves.Count} gloves, "
               + $"{Agents.Count} agents ({AgentsT.Count}T / {AgentsCT.Count}CT), "
               + $"{MusicKits.Count} music kits, {Pins.Count} pins, "
               + $"{Stickers.Count} stickers, {Keychains.Count} keychains");
    }

    private static List<T> LoadList<T>(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                Log.Warn($"catalog missing: {path} — that section will be empty");
                return new List<T>();
            }
            var json = File.ReadAllText(path);
            var list = JsonSerializer.Deserialize<List<T>>(json, JsonOpts);
            return list ?? new List<T>();
        }
        catch (Exception ex)
        {
            Log.Error($"catalog parse failed {path}: {ex.Message}");
            return new List<T>();
        }
    }
}
