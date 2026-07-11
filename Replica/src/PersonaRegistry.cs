using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Replica;

// In-memory cache + JSON-backed persistence for Persona records.
// Single owner: FakeClientManager. Single thread: main game thread.
// No concurrent access invariant — every call is on the same thread.
//
// Persistence model:
//   - Load on plugin Load() — if file exists, deserialize; else empty.
//   - Save on every mutation (atomic rename via temp+rename).
//   - File is small (10s of KB even at thousands of personas), and
//     mutations are infrequent (mapchange, spawn-new-persona).
//
// Persona.Id is monotonic: max(existing) + 1 on insert. Never reused
// even after deletion (prevents debug confusion / accidental aliasing
// of deleted personas to new ones).
public sealed class PersonaRegistry
{
    // Resolved at runtime against the CSSharp gameinfo root so the registry
    // file lives next to other plugin data. Override path explicitly via the
    // ctor parameter for tests or alternate locations.
    public static string DefaultPath =>
        System.IO.Path.Combine(
            CounterStrikeSharp.API.Server.GameDirectory,
            "csgo", "addons", "counterstrikesharp", "configs",
            "plugins", "Replica", "personas.json");

    private readonly string _path;
    private readonly Dictionary<int, Persona> _byId = new();
    private int _nextId = 1;

    public PersonaRegistry(string? path = null)
    {
        _path = path ?? DefaultPath;
    }

    public string Path => _path;

    /// <summary>Total persona count (active + dormant).</summary>
    public int Count => _byId.Count;

    /// <summary>Currently bound (ActiveOnSlot != null) personas.</summary>
    public IEnumerable<Persona> Active => _byId.Values.Where(p => p.IsActive);

    /// <summary>All personas, in Id order.</summary>
    public IEnumerable<Persona> All => _byId.Values.OrderBy(p => p.Id);

    /// <summary>
    /// Sentinel pattern for fallback-mint names from v0.5.1-beta era.
    /// Match-fill from engine_quota in v0.5.1-beta could exhaust the
    /// roster before the registry was populated, falling through to
    /// `player&lt;Id&gt;` synthesis. These records have no real human
    /// gameplay value (the persona never carried distinguishing
    /// behavioral state) — discard on load so v0.5.2-beta starts with
    /// only meaningful personas. Going forward, CFC PRE empty-FIFO
    /// supercede prevents engine_quota from ever reaching CSSharp
    /// AcquireForSpawn, so this regex should rarely match.
    /// </summary>
    private static readonly Regex SentinelPattern =
        new(@"^player\d+$", RegexOptions.Compiled);

    public void Load()
    {
        try {
            if (!File.Exists(_path)) {
                // The DefaultPath moved from a hardcoded developer absolute
                // path to a GameDirectory-relative path in 1d1d9a3 ("Prepare
                // for public release"). Upgraders running off an older build
                // will see this "starting empty" message even though their
                // old personas.json may still exist at the previous
                // location — surface that so the operator can `mv` it across
                // instead of silently re-minting from scratch.
                Log.Info($"PersonaRegistry: no file at {_path}, starting empty " +
                         $"— if upgrading from a pre-1d1d9a3 build, copy your " +
                         $"old personas.json (default was ~/cs2-server/replica/" +
                         $"personas.json) to this path; see README \"Upgrading\".");
                return;
            }
            var json = File.ReadAllText(_path);
            var arr = JsonSerializer.Deserialize<List<Persona>>(json) ?? new();
            _byId.Clear();
            int dropped = 0;
            foreach (var p in arr) {
                // Whitespace-only Name slips past IsNullOrEmpty but produces
                // the same downstream symptoms (empty pool write, persona
                // adopted with display name "   "). Drop those too. See #15.
                if (p.Id <= 0 || string.IsNullOrWhiteSpace(p.Name)) continue;
                // v0.5.2-beta migration: drop "playerN" sentinel garbage
                // from v0.5.1-beta's empty-FIFO fallback. Real personas
                // (LastSeenAt populated, name from roster, etc.) survive.
                if (SentinelPattern.IsMatch(p.Name)) {
                    dropped++;
                    continue;
                }
                _byId[p.Id] = p;
                if (p.Id >= _nextId) _nextId = p.Id + 1;
                // Volatile field — never trust ActiveOnSlot from disk.
                // (Could be left over from a non-clean shutdown.)
                p.ActiveOnSlot = null;
            }
            Log.Info($"PersonaRegistry: loaded {_byId.Count} personas " +
                     $"(nextId={_nextId}, dropped {dropped} sentinel) " +
                     $"from {_path}");
            if (dropped > 0) Save();  // persist migration immediately
        } catch (Exception ex) {
            Log.Error($"PersonaRegistry load failed: {ex.Message} — starting empty");
            _byId.Clear();
            _nextId = 1;
        }
    }

    public void Save()
    {
        try {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            var arr = _byId.Values.OrderBy(p => p.Id).ToList();
            var json = JsonSerializer.Serialize(arr,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        } catch (Exception ex) {
            Log.Error($"PersonaRegistry save failed: {ex.Message}");
        }
    }

    public Persona? GetById(int id) =>
        _byId.TryGetValue(id, out var p) ? p : null;

    public Persona? GetActiveOnSlot(int slot) =>
        _byId.Values.FirstOrDefault(p => p.ActiveOnSlot == slot);

    /// <summary>
    /// LRU + Id tie-break ordering for dormant persona selection. Dormant
    /// personas with empty LastSeenAt sort first (treated as oldest); ties
    /// resolved by ascending Id for stable behavior on a fresh registry.
    /// </summary>
    private static IEnumerable<Persona> OrderForReuse(IEnumerable<Persona> source) =>
        source
            .OrderBy(p => string.IsNullOrEmpty(p.LastSeenAt)
                          ? "0001-01-01T00:00:00.0000000Z"
                          : p.LastSeenAt, StringComparer.Ordinal)
            .ThenBy(p => p.Id);

    /// <summary>
    /// Pick (or create) a persona to spawn. Selection policy:
    /// 1. Prefer a dormant (ActiveOnSlot==null) persona whose Name is
    ///    not in <paramref name="reservedNames"/>. LRU + Id tie-break
    ///    so on a fresh registry the order is deterministic by Id, and
    ///    on a mature registry it spreads load across all personas.
    /// 2. If all dormant ones are reserved, fall through to fallback
    ///    roster — pick the first roster name not yet in registry and
    ///    not in reservedNames; create + persist a new Persona.
    /// 3. Last resort: synthesize "player&lt;Id&gt;" if even the
    ///    fallback roster is exhausted.
    ///
    /// Does NOT bind to a slot — caller is responsible for BindToSlot
    /// after the engine assigns a slot via OCC/CPiS.
    /// </summary>
    public Persona AcquireForSpawn(
        IReadOnlyList<string> fallbackRoster,
        ISet<string> reservedNames)
    {
        var nowIso = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        // CONTRACT: `reservedNames` is a set of NORMALIZED keys produced
        // by NameKey.Normalize (NFKC + lowercase + Cyrillic→
        // Latin transliteration + leetspeak digit strip). Display names
        // in the registry preserve original case; we canonicalize for
        // lookup so 'kennyS' (registry) collides with 'KennyS' (human)
        // and 'Нагибатор' (registry) collides with 'Nagibator' (human).
        static string Norm(string s) => NameKey.Normalize(s);

        // (1) Reuse a dormant persona.
        var dormant = OrderForReuse(_byId.Values.Where(p => !p.IsActive))
            .FirstOrDefault(p => !reservedNames.Contains(Norm(p.Name)));
        if (dormant != null) {
            dormant.LastSeenAt = nowIso;
            Save();
            return dormant;
        }

        // (2) Mint from fallback roster.
        var existingNorm = new HashSet<string>(
            _byId.Values.Select(p => Norm(p.Name)), StringComparer.Ordinal);
        // Filter empty / whitespace-only roster entries before name matching.
        // Without this, a caller passing { "" } or { "   " } could select an
        // empty-string newName, then create + persist Persona { Name = "" }.
        // The Load filter would drop it on next start, but the in-memory
        // registry would carry it for the current session — and a
        // FakeClient bound to that persona would write an empty pool name
        // (CFC PRE's name override falls back to engine default). See #15.
        var newName = fallbackRoster
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .FirstOrDefault(n => !reservedNames.Contains(Norm(n))
                              && !existingNorm.Contains(Norm(n)));

        // (3) Synthesize as last resort.
        // POST-v0.5.2: this branch should be unreachable in normal flow —
        // CFC PRE empty-FIFO supercede prevents engine_quota cascades that
        // exhaust the roster. If we hit it, log loudly: it means batch
        // size > roster size (32 entries), which is a config/usage issue.
        //
        // Belt-and-suspenders against #15: IsNullOrWhiteSpace covers null,
        // empty, and whitespace-only — anything else passing through here
        // would persist as a broken Persona record and the Load filter
        // would silently drop it on next start.
        if (string.IsNullOrWhiteSpace(newName)) {
            Log.Warn($"AcquireForSpawn: roster exhausted (reserved={reservedNames.Count}, " +
                     $"existing={existingNorm.Count}). Synthesizing player{_nextId} sentinel — " +
                     $"will be GC'd on next Load() per migration regex.");
            newName = $"player{_nextId}";
        }

        var persona = new Persona(_nextId++, newName, nowIso);
        _byId[persona.Id] = persona;
        Save();
        return persona;
    }

    /// <summary>
    /// Bind a persona to a player slot — called after successful adoption
    /// in OnClientPutInServer. Updates ActiveOnSlot + LastSeenAt and saves.
    /// </summary>
    public void BindToSlot(int personaId, int slot)
    {
        if (!_byId.TryGetValue(personaId, out var p)) return;
        p.ActiveOnSlot = slot;
        p.LastSeenAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        Save();
    }

    /// <summary>
    /// Release a persona's slot binding without deleting the persona.
    /// Called from OnClientDisconnect (real-disconnect path) and from
    /// Despawn flows. Mapchange path doesn't call this — it preserves
    /// ActiveOnSlot until OnMapStart's snapshot.
    /// </summary>
    public void ReleaseSlot(int personaId)
    {
        if (!_byId.TryGetValue(personaId, out var p)) return;
        p.ActiveOnSlot = null;
        Save();
    }

    /// <summary>
    /// Wipe ActiveOnSlot for all personas — called from OnMapStart
    /// AFTER snapshot. Single Save() at the end instead of per-clear.
    /// </summary>
    public void ClearAllActiveSlots()
    {
        bool any = false;
        foreach (var p in _byId.Values) {
            if (p.ActiveOnSlot != null) { p.ActiveOnSlot = null; any = true; }
        }
        if (any) Save();
    }

    /// <summary>
    /// Hard delete — removes persona by id. Doesn't reuse the id.
    /// Use sparingly; normal flow is just ReleaseSlot.
    /// </summary>
    public bool Remove(int personaId)
    {
        if (!_byId.Remove(personaId)) return false;
        Save();
        return true;
    }
}
