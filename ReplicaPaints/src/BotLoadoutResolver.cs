using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace ReplicaPaints;

// Deterministic per-bot loadout derived from the bot's persona name.
//
// Why a stable hash, not Random / GetHashCode:
//   - Random is the obvious wrong answer (different every spawn).
//   - string.GetHashCode() is randomized per .NET process for security,
//     so the same bot would catch a different skin on every server
//     restart. SHA-256 of UTF-8(name) gives an identical 256-bit digest
//     across processes, machines, and .NET versions.
//
// All three "axes" — weapon paint, knife, gloves — use the same seed
// (the digest from the persona name), so a given bot's whole loadout
// is one coherent identity. Per-axis sub-seeding is achieved by mixing
// in a small ascii tag ("w7", "kT", "gCT") before hashing, so different
// weapons on the same bot don't all collapse to the same index.
public sealed class BotLoadoutResolver
{
    private readonly PaintsDatabase _db;

    // Cache per-name resolution. Tiny (one Loadout per active bot) so a
    // ConcurrentDictionary is overkill — but cheap and thread-safe.
    private readonly ConcurrentDictionary<string, ResolvedBotLoadout> _cache = new();

    public BotLoadoutResolver(PaintsDatabase db) { _db = db; }

    public void Clear() => _cache.Clear();

    public ResolvedBotLoadout Resolve(string personaName)
    {
        if (string.IsNullOrEmpty(personaName)) personaName = "_anon";
        return _cache.GetOrAdd(personaName, BuildLoadout);
    }

    private ResolvedBotLoadout BuildLoadout(string name)
    {
        var loadout = new ResolvedBotLoadout();

        // Weapons: one paint per weapon defindex the catalog knows about.
        // Stickers + keychain are also rolled deterministically per-weapon
        // so a bot's whole loadout is one coherent identity.
        foreach (var defindex in _db.WeaponDefindexes)
        {
            var paints = _db.ForWeapon(defindex);
            if (paints.Count == 0) continue;
            int idx = StableIndex($"w{defindex}:{name}", paints.Count);
            var chosen = paints[idx];

            // 4 sticker slots. Sub-seed per slot so the same bot doesn't
            // wear four copies of one sticker. 0 = empty so it can roll
            // an "empty" slot on a random subset (any miss in [0..len*2)
            // becomes "no sticker", keeping bots from being a sticker
            // wall everywhere).
            int[] stickers = new int[4];
            if (_db.Stickers.Count > 0)
            {
                int range = _db.Stickers.Count * 2;
                for (int s = 0; s < 4; s++)
                {
                    int pick = StableIndex($"sk{defindex}.{s}:{name}", range);
                    stickers[s] = pick < _db.Stickers.Count ? _db.Stickers[pick].Defindex : 0;
                }
            }
            int keychain = 0;
            if (_db.Keychains.Count > 0)
            {
                // 1-in-3 odds of a keychain per weapon — full coverage would
                // visually overload, and not every player has one on every
                // gun in real CS2 either.
                int pool = _db.Keychains.Count * 3;
                int pick = StableIndex($"kc{defindex}:{name}", pool);
                if (pick < _db.Keychains.Count) keychain = _db.Keychains[pick].Defindex;
            }

            // Issue #60 — per-loadout sticker/keychain placement, derived
            // from the same persona seed so a bot's weapon presentation is
            // internally coherent and stable across reconnects. Ranges
            // chosen to mirror real-player loadouts:
            //   sticker offset ±2 units (centred-ish but visibly drifted)
            //   sticker scale  0.85..1.15 (subtle resize)
            //   sticker rotation ±10° (~±0.1745 rad, canted)
            //   sticker wear   0..0.30 (light-to-medium scuff)
            //   keychain xy   ±0.6, z ±1.5 (lateral nudge, vertical drift)
            // Empty sticker slots keep a default StickerPlacement — the
            // apply path skips slots whose sticker id is 0 anyway.
            var stickerPlacements = new StickerPlacement[4];
            for (int s = 0; s < 4; s++)
            {
                if (stickers[s] == 0) { stickerPlacements[s] = new StickerPlacement(); continue; }
                stickerPlacements[s] = new StickerPlacement
                {
                    OffsetX  = SignedUnit($"skox{defindex}.{s}:{name}") * 2f,
                    OffsetY  = SignedUnit($"skoy{defindex}.{s}:{name}") * 2f,
                    Wear     = UnitFloat($"skw{defindex}.{s}:{name}") * 0.30f,
                    Scale    = 1f + SignedUnit($"sks{defindex}.{s}:{name}") * 0.15f,
                    Rotation = SignedUnit($"skr{defindex}.{s}:{name}") * 0.1745f,
                };
            }
            KeychainPlacement? keychainPlacement = null;
            if (keychain > 0)
            {
                keychainPlacement = new KeychainPlacement
                {
                    OffsetX = SignedUnit($"kcx{defindex}:{name}") * 0.6f,
                    OffsetY = SignedUnit($"kcy{defindex}:{name}") * 0.6f,
                    OffsetZ = SignedUnit($"kcz{defindex}:{name}") * 1.5f,
                    // Seed drives swing phase; distinct seeds = distinct
                    // dangle behaviour. int range covers the engine's uint
                    // attribute once ApplyService reinterprets via ViewAsFloat.
                    Seed    = StableIndex($"kcs{defindex}:{name}", int.MaxValue),
                };
            }

            loadout.Weapons[defindex] = new WeaponLoadout
            {
                Paint              = chosen.Paint,
                Seed               = 0,
                Wear               = 0.01f,
                StatTrak           = -1,
                Stickers           = stickers,
                Keychain           = keychain,
                StickerPlacements  = stickerPlacements,
                KeychainPlacement  = keychainPlacement,
            };
        }

        // Knife: pick a defindex from the knives catalog. T and CT use
        // different sub-seeds so a bot may carry different knives on the
        // two teams even if the catalog overlaps.
        if (_db.Knives.Count > 0)
        {
            loadout.KnifeT  = _db.Knives[StableIndex($"kT:{name}",  _db.Knives.Count)].Defindex;
            loadout.KnifeCT = _db.Knives[StableIndex($"kCT:{name}", _db.Knives.Count)].Defindex;
        }

        // Gloves: same dance.
        if (_db.Gloves.Count > 0)
        {
            var gT  = _db.Gloves[StableIndex($"gT:{name}",  _db.Gloves.Count)];
            var gCT = _db.Gloves[StableIndex($"gCT:{name}", _db.Gloves.Count)];
            loadout.GlovesT  = new GloveLoadout { Defindex = gT.Defindex,  Paint = gT.Paint,  Seed = 0, Wear = 0.05f };
            loadout.GlovesCT = new GloveLoadout { Defindex = gCT.Defindex, Paint = gCT.Paint, Seed = 0, Wear = 0.05f };
        }

        // Agents: per-team pick, deterministic from persona name. T and CT
        // sides use different sub-seeds so a bot can carry distinct
        // characters when seen on either side.
        if (_db.AgentsT.Count > 0)
        {
            var aT = _db.AgentsT[StableIndex($"aT:{name}", _db.AgentsT.Count)];
            loadout.AgentT = aT.Defindex;
        }
        if (_db.AgentsCT.Count > 0)
        {
            var aCT = _db.AgentsCT[StableIndex($"aCT:{name}", _db.AgentsCT.Count)];
            loadout.AgentCT = aCT.Defindex;
        }

        // Music kit + pins per persona. Music kit is global (no team
        // axis on the controller). Pins per side so a bot can show a
        // CS:GO 5yr coin on T and an Esports 2014 pin on CT, say.
        if (_db.MusicKits.Count > 0)
            loadout.MusicKit = _db.MusicKits[StableIndex($"mk:{name}", _db.MusicKits.Count)].Defindex;
        if (_db.Pins.Count > 0)
        {
            loadout.PinT  = _db.Pins[StableIndex($"pT:{name}",  _db.Pins.Count)].Defindex;
            loadout.PinCT = _db.Pins[StableIndex($"pCT:{name}", _db.Pins.Count)].Defindex;
        }
        return loadout;
    }

    // SHA-256 over UTF-8(seed) -> first 8 bytes -> ulong -> modulo. Stable
    // across processes (unlike string.GetHashCode), portable across .NET
    // versions, and good-enough uniformity for catalog selection.
    private static int StableIndex(string seed, int size)
    {
        if (size <= 0) return 0;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        ulong u = BitConverter.ToUInt64(hash, 0);
        return (int)(u % (ulong)size);
    }

    // Deterministic float in [0, 1) derived from the same SHA-256 stable
    // hash. Used by issue-#60 placement to produce smooth-but-stable
    // per-bot cosmetic offsets.
    private static float UnitFloat(string seed)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        // Take 24 bits → mantissa-grade resolution, avoids the +1 ulp
        // tie at the upper edge.
        uint u = ((uint)hash[0] << 16) | ((uint)hash[1] << 8) | hash[2];
        return u / 16_777_216f;  // 2^24
    }

    /// <summary>Deterministic float in (-1, 1). Same uniformity as UnitFloat,
    /// just shifted to a signed range.</summary>
    private static float SignedUnit(string seed) => UnitFloat(seed) * 2f - 1f;
}

public sealed class ResolvedBotLoadout
{
    // Same shape as PlayerLoadout — kept separate so future divergence
    // (e.g. psychology-tier filtering in Phase 2) doesn't have to retrofit
    // PlayerLoadout.
    public System.Collections.Generic.Dictionary<int, WeaponLoadout> Weapons { get; } = new();
    public int            KnifeT   { get; set; }
    public int            KnifeCT  { get; set; }
    public GloveLoadout?  GlovesT  { get; set; }
    public GloveLoadout?  GlovesCT { get; set; }
    public int            AgentT   { get; set; }
    public int            AgentCT  { get; set; }
    public int            MusicKit { get; set; }
    public int            PinT     { get; set; }
    public int            PinCT    { get; set; }
}
