using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace Replica;

// Singleton. Owns lifecycle, the BotNavIgnore patch, the detour, the
// per-tick fan-out, and the PersonaRegistry-driven mapchange survival.
//
// v0.5.1+ identity model:
//   - Persona (in PersonaRegistry, JSON-backed) — STABLE across mapchange,
//     plugin reload, and server restart. Carries Name + SteamId + future-
//     phase fields. Identified by monotonic int Id.
//   - FakeClient (in _byId) — VOLATILE, scoped to the current spawn. Holds
//     Slot + per-bot subsystems (Simulator, Buffer, etc.). Links back to
//     its Persona by PersonaId.
//   - _pendingPersonaIds — FIFO of persona ids issued via Spawn(personaId)
//     but not yet adopted. AdoptController dequeues to match the engine's
//     bot_add → CFC PRE → OCC → CPiS arrival order.
public sealed class FakeClientManager : IDisposable
{
    // Fallback name corpus. Used when AcquireForSpawn needs to mint a new
    // persona and the registry is empty (or all personas are reserved).
    private static readonly string[] NamePool = new[]
    {
        "kennyS","Magisk","ZywOo","s1mple","NiKo","device","electronic","blameF",
        "frozen","huNter","rain","Twistzz","ropz","Brollan","Aleksib","broky",
        "FaNg","donk","sh1ro","jks","Boombl4","Ax1Le","Hobbit","n0rb3r7",
        "Spinx","torzsi","stavn","TeSeS","kyxsan","snappi","k1to","sjuush",
    };

    public Telemetry Telemetry { get; }
    public Config Config { get; }
    public ISteamIdProvider SteamIds { get; }
    public ProcessUsercmdsDetour Detour { get; }
    public bool DetourInstalled => Detour.Installed;
    public PersonaRegistry Registry => _registry;

    private readonly MemoryPatch _navPatch = new("BotNavIgnore");
    private readonly PoolMmap _pool = new();
    private readonly Dictionary<int, FakeClient> _byId = new();
    private readonly HashSet<ulong> _usedSteamIds = new();
    private readonly PersonaRegistry _registry;
    // Personas issued via Spawn() but not yet adopted via AdoptController.
    // FIFO order matches engine's bot_add processing — first push, first adopt.
    //
    // Each entry carries the enqueue tick so we can timeout-drain stale
    // entries (engine refused bot_add silently → OCC/CPiS never fires →
    // pending ID stays forever and Reconcile stops growing the fleet).
    // Drained at 1Hz from OnTick by DrainStalePending. Issue #9.
    private readonly Queue<(int Id, int EnqueueTick, FakeTeam Team)> _pendingPersonaIds = new();
    private const int PendingTimeoutTicks = 64 * 5;  // 5 sec @ 64 Hz
    // Counter for source="command" vs "engine_quota" tagging. Spawn()
    // bumps it; AdoptController consumes one. Refused bot_adds may leak
    // a count → next engine bot is mis-tagged. Accepted as race-free.
    //
    // NOTE post-v0.5.2-beta: with ReplicaHider's CFC PRE empty-FIFO
    // SUPERCEDE, "engine_quota" should be effectively unreachable —
    // every CreateFakeClient that lacks a CSSharp Spawn() is blocked at
    // the C++ layer. Counter retained for race-window diagnostics.
    private int _commandSpawnsPending;
    private int _nextId = 1;
    private int _tick;
    private int _ticksSinceSummary;
    private bool _navPatched;

    // Slot → normalized name of currently connected human players.
    // Maintained at OnClientPutInServer (humans only) and torn down at
    // OnClientDisconnect. AcquireForSpawn unions Values with the
    // active-persona name set when minting/picking a persona, so a bot
    // never gets the same display name as a live human.
    //
    // Slot-keyed (not just a HashSet) because human names can change
    // mid-session via Steam (rare); on disconnect we look up by slot
    // to remove the right entry without ambiguity.
    //
    // KNOWN RACE (P/03 step 5+ TODO): if a human connects between our
    // PopFifo failure (engine FIFO empty) and CSSharp's listener firing,
    // a bot Spawn() may have already minted with the conflicting name.
    // Mitigations available but deferred:
    //   (a) re-check in CFC PRE (C++ side) against CServerSideClient[]
    //       names before issuing the override
    //   (b) accept rare collision; it self-resolves on next mapchange
    //       (registry refreshes via AcquireForSpawn at respawn)
    // Low impact in practice: humans don't connect mid-batch-spawn.
    private readonly Dictionary<int, string> _humanNamesBySlot = new();

    /// <summary>
    /// Canonical compare key for name collision detection. Pipeline:
    /// NFKC → trim → lowercase → Cyrillic→Latin transliteration →
    /// leetspeak digit strip in letter-bearing runs. Display name
    /// preserves original case; only the lookup key is canonical.
    /// 'Нагибатор' / 'Nagibator' / 'NaGiBaToR' collapse to the same key;
    /// 'Tr1ck5t3r' / 'trickster' collapse; 'killer_2010' keeps its year
    /// suffix (digits in pure-numeric runs are NOT folded).
    /// </summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var folded = s.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        folded = TransliterateCyrillic(folded);
        folded = StripLeetspeak(folded);
        return folded;
    }

    private static string TransliterateCyrillic(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                'а' => "a",  'б' => "b",  'в' => "v",  'г' => "g",  'д' => "d",
                'е' => "e",  'ё' => "e",  'ж' => "zh", 'з' => "z",  'и' => "i",
                'й' => "y",  'к' => "k",  'л' => "l",  'м' => "m",  'н' => "n",
                'о' => "o",  'п' => "p",  'р' => "r",  'с' => "s",  'т' => "t",
                'у' => "u",  'ф' => "f",  'х' => "h",  'ц' => "ts", 'ч' => "ch",
                'ш' => "sh", 'щ' => "shch", 'ъ' => "", 'ы' => "y",  'ь' => "",
                'э' => "e",  'ю' => "yu", 'я' => "ya",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }

    // Substitute leet digits with their letter equivalents only inside
    // alphanumeric runs that contain at least one ASCII letter. A run is
    // a maximal contiguous span of [a-z0-9]. This keeps numeric-suffix
    // patterns ('killer_2010', 'lol_228') intact while collapsing
    // letter-mixed leet ('Tr1ck5t3r' → 'trickster', 'n00b' → 'noob').
    private static string StripLeetspeak(string s)
    {
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            bool alnum = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            if (!alnum) { sb.Append(c); i++; continue; }
            int j = i;
            bool hasLetter = false;
            while (j < s.Length)
            {
                char cj = s[j];
                bool ja = (cj >= 'a' && cj <= 'z') || (cj >= '0' && cj <= '9');
                if (!ja) break;
                if (cj >= 'a' && cj <= 'z') hasLetter = true;
                j++;
            }
            for (int k = i; k < j; k++)
            {
                char ch = s[k];
                if (hasLetter && ch >= '0' && ch <= '9')
                {
                    ch = ch switch
                    {
                        '0' => 'o', '1' => 'i', '3' => 'e',
                        '4' => 'a', '5' => 's', '7' => 't',
                        _ => ch,
                    };
                }
                sb.Append(ch);
            }
            i = j;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Re-assert engine `bot_quota` to match (active + pending) bot count.
    /// Without this, engine sees default `bot_quota=10` (or anything > our
    /// actual count) and hammers CreateFakeClient at ~64 attempts/sec/slot
    /// missing — ReplicaHider supercedes them all (correctness preserved),
    /// but the retry loop wastes CPU and floods server.log with "Unable to
    /// create bot" lines. Setting quota = actual_count satisfies the
    /// engine's appetite and quiets the loop.
    ///
    /// Called after Spawn / Despawn / OnMapStart respawn batch / once per
    /// second from Tick (defensive — engine resets quota at warmup_end and
    /// possibly other phase transitions). Idempotent.
    /// </summary>
    private void EnforceBotQuota()
    {
        int target = _byId.Count + _pendingPersonaIds.Count;
        Server.ExecuteCommand($"bot_quota {target}");
    }

    public IReadOnlyCollection<FakeClient> All => _byId.Values;

    /// <summary>FleetManager-friendly accessors (v0.6.0+).</summary>
    public int PendingPersonaCount => _pendingPersonaIds.Count;
    public bool IsMapchangeInProgress => _pool.IsMapchangeInProgress();
    /// <summary>Direct access to the shared mmap pool — used by AimHook.cs
    /// to write override values that the C++ ReplicaHider reads on each
    /// CCSBot::UpdateLookAngles fire.</summary>
    public PoolMmap GetPool() => _pool;
    public FleetManager Fleet { get; private set; } = null!;
    public RevealController Reveal { get; private set; } = null!;
    public BotDamagePatch DamagePatch { get; private set; } = null!;

    public FakeClientManager(BasePlugin plugin, Config cfg, Telemetry telemetry)
    {
        Plugin = plugin;
        Config = cfg;
        Telemetry = telemetry;
        SteamIds = new SyntheticSteamIdProvider(telemetry.SessionId);
        Detour = new ProcessUsercmdsDetour();
        _registry = new PersonaRegistry();
        Fleet = new FleetManager(this);
        Reveal = new RevealController(this);
        DamagePatch = new BotDamagePatch(plugin, this);
    }

    /// <summary>The owning plugin instance — used by BotDamagePatch and any
    /// future component that needs RegisterListener / RemoveListener.</summary>
    public BasePlugin Plugin { get; }

    /// <summary>FakeClient lookup by slot — used by RevealController.</summary>
    public FakeClient? FindBySlot(int slot)
        => _byId.Values.FirstOrDefault(b => b.Slot == slot);

    /// <summary>Single source of truth for "is this controller a real human
    /// we must never touch". Two signals: the connect-time human registry
    /// and the engine's AuthorizedSteamID (set only for authenticated
    /// clients, never for fakes). Was inlined in 4+ places (enforcer, kick
    /// sweep, adopt, husk sweep) — centralized here so a future tweak can't
    /// drift between call sites (#9 refactor).</summary>
    public bool IsHuman(CCSPlayerController c)
        => _humanNamesBySlot.ContainsKey(c.Slot) || c.AuthorizedSteamID != null;

    /// <summary>One-shot ground-truth diagnostic (replica_doctor). Walks
    /// EVERY controller entity and classifies it against our managed map,
    /// so phantom team-roster members (ghosts: live but unmanaged, or
    /// husks: disconnected-but-present) are counted directly instead of
    /// inferred from the lying `status`/`scored with N` numbers. Read-only.</summary>
    public List<string> BuildDoctorReport()
    {
        var lines = new List<string>();
        string[] teamName = { "Unassigned", "Spectator", "T", "CT" };
        var liveByTeam  = new int[4];   // managed fakes per team
        var ghostByTeam = new int[4];   // valid, connected, NOT managed, NOT human
        int humans = 0, husks = 0, total = 0;
        var flagged = new List<string>();

        // Scan all 64 slots directly (not Utilities.GetPlayers, which can
        // skip mid-disconnect controllers) so the doctor finds husks the
        // same way SweepRosterHusks/DespawnAll do — it must catch exactly
        // what the cleanup paths target, or it can't be trusted as a gauge.
        for (int slot = 0; slot < 64; slot++)
        {
            CCSPlayerController? c = null;
            try { c = Utilities.GetPlayerFromSlot(slot); } catch { }
            if (c == null || !c.IsValid || c.IsHLTV) continue;
            total++;
            int team = (int)c.TeamNum;
            if (team < 0 || team > 3) team = 0;

            string nm; try { nm = c.PlayerName ?? "<null>"; } catch { nm = "<throw>"; }

            bool disconnected = false;
            try { disconnected = c.Connected == PlayerConnectedState.Disconnected; } catch { }

            if (IsHuman(c)) { humans++; continue; }

            if (disconnected)
            {
                husks++;
                flagged.Add($"  HUSK   slot={c.Slot,-2} team={teamName[team],-10} name='{nm}' (disconnected but present)");
                continue;
            }

            bool managed = _byId.Values.Any(b => b.Slot == c.Slot);
            if (managed) { liveByTeam[team]++; }
            else
            {
                ghostByTeam[team]++;
                string conn; try { conn = c.Connected.ToString(); } catch { conn = "?"; }
                flagged.Add($"  GHOST  slot={c.Slot,-2} team={teamName[team],-10} name='{nm}' conn={conn} (live but unmanaged)");
            }
        }

        int managedTotal = _byId.Count;
        int ghostTotal = ghostByTeam.Sum();
        bool clean = ghostTotal == 0 && husks == 0;

        lines.Add($"[doctor] {(clean ? "PASS" : "WARN")} — managed={managedTotal} "
                + $"live-controllers={total} humans={humans} ghosts={ghostTotal} husks={husks}");
        lines.Add($"[doctor] managed-by-team: T={liveByTeam[2]} CT={liveByTeam[3]} "
                + $"Spec={liveByTeam[1]} Unassigned={liveByTeam[0]}");
        if (ghostTotal > 0)
            lines.Add($"[doctor] ghost-by-team:   T={ghostByTeam[2]} CT={ghostByTeam[3]} "
                    + $"Spec={ghostByTeam[1]} Unassigned={ghostByTeam[0]}");
        lines.Add($"[doctor] pending={_pendingPersonaIds.Count} mapchange={IsMapchangeInProgress} "
                + $"hider={IsHiderActive()} fleetTarget={Config.FleetSize}"
                + (Config.HasFleetSizeOverride ? $" override={Config.FleetSizeOverride}" : ""));
        foreach (var f in flagged.Take(24)) lines.Add(f);
        if (flagged.Count > 24) lines.Add($"  … +{flagged.Count - 24} more");
        if (clean) lines.Add("[doctor] roster is clean: every non-human controller is a managed bot, no husks.");
        else lines.Add("[doctor] ACTION: ghosts/husks present — run `replica_kick_bots respawn` or investigate flagged slots.");
        return lines;
    }

    public void OnLoad(string csVersion)
    {
        // PoolMmap drives the ReplicaHider C++ side.
        if (!_pool.Open("/tmp/replica_fake_slots.bin"))
            Log.Warn("PoolMmap not opened — bots will keep BOT-icon");

        // Persistent persona registry — stable identity across server
        // restarts, mapchanges, and plugin reloads.
        _registry.Load();

        // Recorded structural aim profiles (record→profile→bot loop, #44).
        // Read here so AimController can pull a bot's deterministic per-weapon
        // flaw at adopt time. No-op when no recordings exist or the feature
        // stays off (replica_aim_structural).
        StructuralProfileStore.Load();

        var detourOk = Detour.Install();
        Log.Info($"detour ProcessUsercmds: {(detourOk ? "ok" : Detour.InstallError)}");
        Telemetry.Write("detour_install", new Dictionary<string, object?> {
            { "target", "ProcessUsercmds" }, { "success", detourOk },
            { "reason", detourOk ? "ok" : (Detour.InstallError ?? "unknown") },
            { "behavior", "accounting_only" } });

        bool patchOk = false; string patchReason = "disabled_in_cfg";
        if (Config.ApplyBotNavPatch)
        {
            Log.Warn("BotNavIgnore patch ENABLED — version-fragile, may crash");
            patchOk = _navPatch.Apply("BotNavIgnore", new byte[] { 0xEB });
            _navPatched = patchOk;
            patchReason = patchOk ? "ok" : (_navPatch.Error ?? "unknown");
            Log.Info($"patch BotNavIgnore: {patchReason}");
        }
        Telemetry.Write("patch_install", new Dictionary<string, object?> {
            { "target", "BotNavIgnore" }, { "success", patchOk }, { "reason", patchReason } });
        Telemetry.Write("boot", new Dictionary<string, object?> {
            { "mode", "A+" }, { "variant", "accounting_only" },
            { "steamIdMode", SteamIds.Mode }, { "csVersion", csVersion },
            { "personaRegistryCount", _registry.Count },
            { "personaRegistryPath", _registry.Path } });

        // BotDamagePatch INTENTIONALLY NOT INSTALLED in v0.6.0.2 path.
        // User clarified: damage masking is the wrong axis — bots still
        // VISUALLY engage each other (chase, shoot, miss humans), which
        // ruins the "horde focused on you" reveal feel. Need AI-level
        // targeting override so bots don't even SEE other bots as
        // candidates. Investigation in progress.
    }

    public void OnUnload(bool hotReload = false)
    {
        // Restored from fd9a478 after the rebrand restructure dropped it
        // (#196). Despawn issues `kickid {slot}` via the engine command
        // buffer (async) and clears pool[slot]=0 (sync). On a hot-reload
        // the async kicks lag the new instance's Load by hundreds of ms,
        // so its adopt sweep runs while the engine still holds the stale
        // slots — they float as ghosts until kickid lands, while
        // Reconcile spawns a fresh fleet alongside (inflated alive
        // counters). Keeping slots + pool intact across the hot-reload
        // boundary lets the new instance re-adopt them cleanly.
        if (!hotReload)
        {
            foreach (var id in _byId.Keys.ToArray()) Despawn(id, "shutdown");
        }
        // Detour + nav patch live in libserver memory — they MUST be
        // uninstalled regardless of hotReload: Load() re-installs on the
        // next instance, and two detours on one address is chaos.
        Detour.Uninstall(); _navPatch.Undo();
        // Defensive save — every mutation already flushes, but a final
        // pass guards against a race where the last mutation didn't reach
        // disk before shutdown / reload.
        _registry.Save();
        // Pool.Close just closes the OS handle; the mmap file persists
        // and the next instance reopens it on Load.
        _pool.Close();
    }

    // Spawn flow:
    //   personaId == null  → AcquireForSpawn picks a dormant persona
    //                        (LRU + Id tie-break) or mints a new one.
    //   personaId != null  → restore-style: explicit persona by id, used
    //                        from OnMapStart respawn batch.
    // Pushes persona.Name to FIFO (consumed by C++ Hider's CFC PRE
    // override) and enqueues persona.Id into _pendingPersonaIds so the
    // upcoming OCC/CPiS can bind the correct persona.
    public void Spawn(FakeTeam team, int? personaId = null)
    {
        Persona persona;
        if (personaId.HasValue)
        {
            persona = _registry.GetById(personaId.Value)
                      ?? throw new InvalidOperationException(
                          $"Spawn: persona id={personaId.Value} not in registry");
            persona.LastSeenAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            _registry.Save();
        }
        else
        {
            // Reserve names that:
            //   - currently active personas (already on a slot)
            //   - in-flight as pending (Spawn issued, not yet adopted)
            //   - currently connected humans (collision-free vs live players)
            // All keys NORMALIZED via Normalize() — case-insensitive + NFKC
            // unicode-fold so 'ZyWoO' == 'zywoo' == 'ZYWOO'. Display name
            // preserves original case in the registry; only the lookup key
            // is normalized.
            var reserved = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in _registry.Active) reserved.Add(Normalize(p.Name));
            foreach (var (pid, _, _) in _pendingPersonaIds)
            {
                var p2 = _registry.GetById(pid);
                if (p2 != null) reserved.Add(Normalize(p2.Name));
            }
            foreach (var humanName in _humanNamesBySlot.Values)
                reserved.Add(humanName);  // already normalized
            persona = _registry.AcquireForSpawn(NamePool, reserved);
        }

        // Sanity cap on pending queue. If the engine is stuck refusing
        // bot_add (gamemode lock, warmup transition, full server), we'd
        // otherwise enqueue persona IDs forever. The 1Hz DrainStalePending
        // sweep handles steady-state, but capping at schedule time
        // prevents a fast Spawn loop (e.g. operator script) from racing
        // past the drain. Min-floor 16 so FleetSize=0/low values don't
        // brick manual `replica_spawn_bots N`. Issue #9.
        int pendingCap = Math.Max(Config.FleetSize * 2, 16);
        if (_pendingPersonaIds.Count >= pendingCap)
        {
            Log.Warn($"Spawn: pending queue at cap ({_pendingPersonaIds.Count}/{pendingCap}), " +
                     $"refusing — engine may be declining bot_add. DrainStalePending will recover in ≤5s.");
            return;
        }

        if (!_pool.PushFifo(persona.Name))
        {
            Log.Warn($"Spawn: FIFO full ({PoolMmap.FifoCapacity}), drop persona='{persona.Name}'");
            return;
        }
        _pendingPersonaIds.Enqueue((persona.Id, Server.TickCount, team));
        _commandSpawnsPending++;
        Server.ExecuteCommand(team == FakeTeam.CT ? "bot_add ct" : "bot_add t");
        Telemetry.Write("spawn_request", new Dictionary<string, object?> {
            { "personaId", persona.Id }, { "name", persona.Name },
            { "team", team.ToString() }, { "explicit", personaId.HasValue } });

        // v0.5.2-beta: cap engine quota at our actual+pending count so it
        // doesn't try to auto-fill above what we've explicitly issued.
        // Without this, engine sees default-or-bumped bot_quota and fires
        // CreateFakeClient ~64Hz per missing slot — ReplicaHider supercedes
        // them all (correctness preserved), but the retry loop wastes CPU.
        EnforceBotQuota();
    }

    public void SetHiderActive(bool active)
    {
        _pool.WriteActive(active);
        Log.Info($"ReplicaHider {(active ? "enabled" : "disabled")} (pool kill-switch)");
    }

    public bool IsHiderActive() => _pool.ReadActive();

    public void OnClientConnected(int slot)
    {
        // Pool managed/name marking is owned by C++ Hider (CFC PRE → OCC mark).
        // This listener is a no-op for the pool path; kept registered so
        // future per-connect logic has a plug-in point.
    }

    public void OnClientPutInServer(int slot)
    {
        try {
            var c = Utilities.GetPlayerFromSlot(slot);
            if (c == null || !c.IsValid || c.IsHLTV) return;

            // Real human guard — they have a Steam-authorized SteamID even
            // after our byte-160 flip. Bots never have one. Defends against
            // orphaned pool[slot]=1 marks from previous bots whose Despawn
            // didn't fire.
            if (c.AuthorizedSteamID != null) {
                // Track human name for AcquireForSpawn collision-avoidance.
                // Stored normalized (NFKC + lowercase) so 'kennyS' (human)
                // blocks bot mint of 'kennys' or 'KennyS' alike.
                var key = Normalize(c.PlayerName ?? "");
                if (!string.IsNullOrEmpty(key))
                    _humanNamesBySlot[slot] = key;
                return;
            }

            // Pool flag is the source of truth. By CPiS time, C++ Hider's
            // CPiS post-hook has already flipped byte 160 for managed slots
            // — c.IsBot would return False here.
            if (_pool.Read(slot) == 0) return;
            if (_byId.Values.Any(b => b.Slot == slot)) return;

            AdoptController(c);
        } catch (Exception ex) { Log.Error($"OnClientPutInServer slot={slot}: {ex.Message}"); }
    }

    public void OnClientDisconnect(int slot)
    {
        try {
            // Drop human-name tracking unconditionally — humans should be
            // forgotten across mapchange (their PutInServer fires fresh
            // on the new map). Bot-managed slots aren't in this map, so
            // a Remove() that misses is a no-op.
            _humanNamesBySlot.Remove(slot);

            // Mapchange survival (v0.5.1+): C++ Hider sets the pool's
            // mapchange flag at IMetamodListener::OnLevelShutdown — earlier
            // than the synthetic OnClientDisconnect cascade fired by
            // PlayerManager::OnLevelEnd inside the StartupServer hook chain
            // for the new map. With the flag set, this is NOT a real
            // disconnect: the engine carries CServerSideClient through as
            // a zombie that will be reactivated on the new map. Preserving
            // _byId, pool[managed]/[name], and registry ActiveOnSlot lets
            // OnMapStart snapshot and re-spawn fresh bots with matching
            // personas.
            if (_pool.IsMapchangeInProgress()) {
                Telemetry.Write("disconnect_skipped_mapchange", new Dictionary<string, object?> {
                    { "slot", slot } });
                return;
            }

            var fc = _byId.Values.FirstOrDefault(b => b.Slot == slot);
            if (fc != null) {
                Despawn(fc.Id, "client_disconnect");
            } else {
                // Orphan cleanup: pool[slot] may have been marked in earlier
                // session (mapchange, plugin reload, etc.) without a matching
                // _byId entry. Without this, the next client to land on this
                // slot — possibly a real human — would inherit the mark and
                // get adopted as a bot. Belt-and-braces with the Authorized
                // SteamID gate in OnClientPutInServer.
                if (_pool.Read(slot) != 0) {
                    _pool.Write(slot, 0);
                    _pool.WriteName(slot, "");
                    Log.Info($"orphan pool cleanup slot={slot}");
                }
            }
        } catch (Exception ex) { Log.Error($"OnClientDisconnect slot={slot}: {ex.Message}"); }
    }

    public void AdoptExistingBots()
    {
        // Hot reload: adopt EVERY connected non-human — no pool/IsBot gate.
        // Both old signals are unreliable here (#196 post-mortem, idle
        // css_plugins-reload repro): the Hider flips m_bFakePlayer so
        // c.IsBot reads false, and the dying instance's Despawn may have
        // zeroed the pool marks while its queued kickid commands were
        // DROPPED with the plugin context — leaving live, unmarked fakes.
        // Gating on those signals skipped all of them; Reconcile then
        // spawned a fresh fleet alongside → ghost roster (8 managed,
        // 16 connected). A connected controller that is neither a tracked
        // human nor Steam-authorized can only be a fake — adopt it.
        // Human gate: connect-time registry + AuthorizedSteamID (#18/#98),
        // the same pair the DespawnAll kick sweep uses.
        foreach (var c in Utilities.GetPlayers())
        {
            if (c == null || !c.IsValid || c.IsHLTV) continue;
            if (IsHuman(c)) continue;
            if (_byId.Values.Any(b => b.Slot == c.Slot)) continue;
            // Make sure pool reflects management before AdoptController.
            if (_pool.Read(c.Slot) == 0) _pool.Write(c.Slot, 1);
            AdoptController(c);
        }
    }

    private void AdoptController(CCSPlayerController ctrl)
    {
        var slot = ctrl.Slot;
        var poolName = _pool.ReadName(slot);

        // Resolve persona:
        //  (1) If we have a pending persona id from a recent Spawn() call,
        //      dequeue it. The pool name SHOULD match persona.Name — log if not.
        //  (2) Else (engine_quota path), look up registry by pool name —
        //      a previous-session persona with this name may already exist.
        //  (3) Else mint via AcquireForSpawn using poolName as preferred.
        Persona? persona = null;
        FakeTeam? requestedTeam = null;
        if (_pendingPersonaIds.Count > 0)
        {
            var (pid, _, reqTeam) = _pendingPersonaIds.Dequeue();
            requestedTeam = reqTeam;
            persona = _registry.GetById(pid);
            if (persona != null && !string.IsNullOrEmpty(poolName)
                && !string.Equals(poolName, persona.Name, StringComparison.Ordinal))
            {
                Log.Warn($"AdoptController: pool name '{poolName}' != pending persona " +
                         $"'{persona.Name}' (id={persona.Id}, slot={slot}) — using persona");
            }
        }
        if (persona == null && !string.IsNullOrEmpty(poolName))
        {
            persona = _registry.All.FirstOrDefault(p =>
                p.Name == poolName && !p.IsActive);
        }
        if (persona == null)
        {
            // Engine_quota path with no prior persona record. Mint via
            // registry — preferring the name C++ Hider chose from its
            // fallback roster (visible in pool name).
            var reserved = new HashSet<string>(
                _registry.Active.Select(p => p.Name), StringComparer.Ordinal);
            var preferred = !string.IsNullOrEmpty(poolName)
                ? new[] { poolName }
                : NamePool;
            persona = _registry.AcquireForSpawn(preferred, reserved);
        }

        // Persona's stable SteamId — synthesize on first bind, persist.
        if (persona.SteamId64 == 0)
        {
            persona.SteamId64 = SteamIds.Generate(slot);
            _registry.Save();
        }

        // Bind to slot in registry (also updates LastSeenAt).
        _registry.BindToSlot(persona.Id, slot);

        // Build live FakeClient — volatile slot-bound state.
        var id = _nextId++;
        // Requested team (from Spawn) wins over whatever the engine actually
        // did at connect: `bot_add ct|t` is advisory and the engine freely
        // refuses/rebalances (ChangeTeam `willSwitch 0` in the game log).
        // The fleet-team enforcer below keeps it honest from here on.
        var actualTeam = (ctrl.TeamNum == 3 ? FakeTeam.CT : FakeTeam.T);
        var team = requestedTeam ?? actualTeam;
        var profile = BotProfile.Generate(persona.SteamId64);
        var source = _commandSpawnsPending > 0 ? "command" : "engine_quota";
        if (source == "command") _commandSpawnsPending--;

        var fc = new FakeClient(id, persona.Id, persona.Name, persona.SteamId64, team, profile)
            { Slot = slot, Alive = true, BindTick = Server.TickCount };
        // Attach this persona's recorded structural aim bias (#44), if any.
        // Cached once here so AimController.Tick does no per-tick name lookup.
        fc.Aim.StructuralBias = StructuralProfileStore.TryGet(persona.Name);
        // No inline SwitchTeam here: we're inside the connect callback and
        // the engine is still mid-handshake for this controller. The fleet
        // enforcer converges actual→intended within one pass (≤0.5s) from
        // the safety of OnTick, with grace holding the requested team.
        _byId[id] = fc;
        _usedSteamIds.Add(persona.SteamId64);
        // Publish persona name into pool so C++ Hider's CPiS safety-net
        // can also re-overwrite engine-side m_Name on mapchange-rebuilt
        // CServerSideClient instances (defensive — primary path is CFC PRE).
        _pool.WriteName(slot, persona.Name);
        // Assert the managed mark from C# too (#196): the Hider stamps it
        // on its CFC path, but adopts that arrive outside that path
        // (re-adopt after reload, engine_quota races) must not depend on
        // it — Despawn/identity logic keys off this byte.
        if (_pool.Read(slot) == 0) _pool.Write(slot, 1);
        fc.OverwriteIdentityOnController(ctrl);

        // Engine re-stamps name from bot_names.txt during post-spawn —
        // re-write at +4/+16 ticks (name-only) to outlast that.
        var capFc = fc; var capSlot = fc.Slot;
        Server.RunOnTick(Server.TickCount + 4,  () => ReassertIdentity(capFc, capSlot));
        Server.RunOnTick(Server.TickCount + 16, () => ReassertIdentity(capFc, capSlot));

        Telemetry.Write("fake_spawn", new Dictionary<string, object?> {
            { "botId", id }, { "personaId", persona.Id },
            { "name", persona.Name }, { "steamId", persona.SteamId64.ToString() },
            { "team", team.ToString() }, { "slot", fc.Slot },
            { "profileId", profile.Seed.ToString("x16") },
            { "mode", SteamIds.Mode }, { "source", source },
            { "registryReuse", persona.LastSeenAt != persona.CreatedAt } });
    }

    public void Despawn(int id, string reason)
    {
        if (!_byId.TryGetValue(id, out var fc)) return;
        _byId.Remove(id);
        _registry.ReleaseSlot(fc.PersonaId);
        _pool.Write(fc.Slot, 0);  // un-mark so future engine clients aren't accidentally hidden
        _pool.WriteName(fc.Slot, "");
        fc.Aim.Disarm(_pool);     // release per-slot aim override if armed
        Telemetry.Write("fake_despawn", new Dictionary<string, object?> {
            { "botId", id }, { "personaId", fc.PersonaId }, { "reason", reason },
            { "name", fc.Name }, { "slot", fc.Slot } });
        try {
            // b696a39 lineage (#196), corrected: kick by USERID, no IsBot
            // gate. The Hider flips m_bFakePlayer so `ctrl.IsBot` reads
            // false (old gate skipped the kick); name-based `bot_kick`
            // missed overwritten names. And `kickid` takes a USERID, not
            // a slot — they coincide on a fresh boot (which made slot
            // kicks look correct) and drift apart once slots recycle:
            // slot kicks then silently miss and the un-kicked bots pile
            // up as unmanaged team-roster ghosts (6/25-style alive
            // counters, reproduced 2026-06-13).
            var ctrl = Utilities.GetPlayerFromSlot(fc.Slot);
            if (ctrl != null && ctrl.IsValid)
            {
                // UserId getter can throw on Hider-flipped fakes — an
                // unguarded read here silently killed the WHOLE kick
                // (caught by the outer catch, zero kickids issued).
                int kid = fc.Slot;
                try { kid = ctrl.UserId ?? fc.Slot; } catch { }
                // +2 ticks: the pool unmark above is sync, the Hider's
                // unflip sweep runs on the next GameFrame — by the time
                // this kick is processed the engine sees a REAL bot and
                // runs its native cleanup (no roster husk left behind).
                Server.RunOnTick(Server.TickCount + 2,
                    () => Server.ExecuteCommand($"kickid {kid}"));
            }
        } catch { }
        // Quota tracks (active+pending) — Despawn drops one, re-assert.
        EnforceBotQuota();
    }

    public int DespawnAll(string reason)
    {
        var n = _byId.Count;
        var managedSlots = new HashSet<int>(_byId.Values.Select(fc => fc.Slot));
        foreach (var id in _byId.Keys.ToArray()) Despawn(id, reason);

        // Defensive sweep (#196): engine-side fakes that never entered
        // _byId (bot_quota race before the FleetSize pin, or a prior
        // instance's leftovers) survive the managed loop — exactly the
        // orphans that ride into the next fleet spawn. Kick anything
        // that is neither a tracked human nor a just-despawned slot.
        // Human gate is double: the connect-time human registry plus
        // AuthorizedSteamID (same guard the team enforcer uses, #18/#98).
        int swept = 0;
        for (int slot = 0; slot < 64; slot++)
        {
            if (managedSlots.Contains(slot)) continue;
            CCSPlayerController? c = null;
            try { c = Utilities.GetPlayerFromSlot(slot); } catch { }
            if (c == null || !c.IsValid || IsHuman(c)) continue;
            // Post-kick controllers linger as "valid" for a few frames —
            // kicking them again just spams `userid not found`. Only
            // sweep slots the engine still considers connected.
            if (c.Connected != PlayerConnectedState.Connected) continue;
            int sweepKid = slot;
            try { sweepKid = c.UserId ?? slot; } catch { }
            Server.ExecuteCommand($"kickid {sweepKid}");
            swept++;
        }
        if (swept > 0)
        {
            Log.Info($"DespawnAll: swept {swept} unmanaged engine fake(s)");
            Telemetry.Write("orphan_sweep", new Dictionary<string, object?> {
                { "reason", reason }, { "count", swept } });
            n += swept;
        }
        // Drop in-flight pending personas too — without this, names that
        // were pushed to the C++ FIFO but not yet adopted will land as
        // fresh _byId entries on the next CFC PRE / OCC dance and the
        // fleet appears to "respawn" right after a kick. The pool FIFO
        // itself is C++-consumed (SPSC contract — CSSharp must not write
        // its tail), so we leave that pipeline alone; those orphan FIFO
        // names will be popped + adopted, but with _pendingPersonaIds
        // empty they take the engine_quota path and become regular
        // pickups. Combined with FleetSize=0 (set by replica_kick_bots)
        // they're shrunk on the next Reconcile.
        if (_pendingPersonaIds.Count > 0)
        {
            Telemetry.Write("pending_clear", new Dictionary<string, object?> {
                { "reason", reason }, { "count", _pendingPersonaIds.Count } });
            _pendingPersonaIds.Clear();
        }
        EnforceBotQuota();
        return n;
    }

    public void OnMapStart()
    {
        try {
            // (0) Notify reveal controller BEFORE any fleet churn. Its
            //     slot-keyed dictionaries (_botPrevTeams, _botTargetTeams,
            //     _combatState, _lastRespawnTick, _apocalypseCarriers) would
            //     otherwise survive the snapshot/wipe below and apply
            //     stage-specific overrides to whatever the engine rebinds
            //     those slots to on the new map (potentially real humans).
            //     See RevealController.OnMapStart docs. Issue #5.
            Reveal.OnMapStart();

            // (1) Snapshot active bots BEFORE clearing. _byId entries were
            //     preserved through the synthetic disconnect cascade because
            //     OnClientDisconnect skipped Despawn while pool.IsMapchangeInProgress.
            var snapshot = _byId.Values
                .Select(b => new RespawnEntry(b.PersonaId, b.Team))
                .ToList();

            // (2) Snapshot zombie slots from pool managed[] BEFORE wipe.
            //     These are the slots whose CServerSideClient instances are
            //     stuck in CHANGELEVEL → CONNECTED state on the new map.
            //     Utilities.GetPlayers() doesn't surface them (no CCSPlayer
            //     Controller for non-active clients), so we MUST use the pool.
            var zombieSlots = new List<int>();
            for (int slot = 0; slot < PoolMmap.Slots; slot++) {
                if (_pool.Read(slot) != 0) zombieSlots.Add(slot);
            }

            // (3) Wipe in-memory state — slots will be re-bound on adopt.
            _byId.Clear();
            _registry.ClearAllActiveSlots();
            _pendingPersonaIds.Clear();
            _humanNamesBySlot.Clear();  // humans re-fire OnClientPutInServer on new map

            // Mid-mapchange engine state has bot_quota at default (10) or
            // whatever the new map's gamemode_*.cfg sets. Reset to 0 BEFORE
            // we re-issue Spawn() — the supercede in CFC PRE will block any
            // race attempt the engine makes between here and the respawn
            // batch firing 8 ticks later.
            Server.ExecuteCommand("bot_quota 0");

            // (4) Wipe pool managed[] + names — old slot indices are about
            //     to be invalid. CFC PRE / OCC will re-mark fresh slots when
            //     respawned bots arrive.
            foreach (var slot in zombieSlots) {
                _pool.Write(slot, 0);
                _pool.WriteName(slot, "");
            }

            // (5) Clear mapchange flag — synthetic disconnect cascade is done.
            //     Any further OnClientDisconnect goes through real-path.
            _pool.WriteMapchangeFlag(false);

            // (6) Kick zombie engine clients. kickid takes a USERID — it
            //     only equals the slot on a fresh boot, so resolve the
            //     live controller's UserId and fall back to the slot for
            //     husks with no resolvable controller (engine ignores
            //     invalid ids without error).
            int kicks = 0;
            foreach (var slot in zombieSlots) {
                try {
                    int kid = slot;
                    try {
                        var zc = Utilities.GetPlayerFromSlot(slot);
                        if (zc != null && zc.IsValid && zc.UserId.HasValue) kid = zc.UserId.Value;
                    } catch { }
                    Server.ExecuteCommand($"kickid {kid}");
                    kicks++;
                } catch (Exception ex) {
                    Log.Debug($"OnMapStart kickid slot={slot}: {ex.Message}");
                }
            }

            Telemetry.Write("mapchange_respawn", new Dictionary<string, object?> {
                { "snapshotCount", snapshot.Count }, { "kicks", kicks },
                { "zombieSlots", string.Join(",", zombieSlots) } });

            // (7) Schedule respawn AFTER kicks settle. Engine processes
            //     kickid in current tick window; spawn a few ticks later
            //     so engine slots are free.
            var tickFire = Server.TickCount + 8;
            foreach (var s in snapshot) {
                var capPid = s.PersonaId; var capTeam = s.Team;
                Server.RunOnTick(tickFire, () => {
                    try { Spawn(capTeam, capPid); }
                    catch (Exception ex) {
                        Log.Error($"OnMapStart respawn pid={capPid}: {ex.Message}");
                    }
                });
            }

            // (8) Mark fleet ready — Reconcile() will now run from Tick at 1Hz.
            //     If snapshot.Count < FleetSize, reconcile will top up; if >,
            //     it will trim. On boot (snapshot empty), reconcile spawns
            //     up to FleetSize from a clean slate.
            Fleet.OnMapStartComplete();
        } catch (Exception ex) { Log.Error($"OnMapStart: {ex.Message}"); }
    }

    private readonly record struct RespawnEntry(int PersonaId, FakeTeam Team);

    public void OnTick()
    {
        _tick++; _ticksSinceSummary++;
        Fleet.OnTick();
        Reveal.OnTick();
        if (++_ticksSinceTeamEnforce >= TeamEnforceIntervalTicks)
        {
            _ticksSinceTeamEnforce = 0;
            try { EnforceFleetTeams(); }
            catch (Exception ex) { Log.Error($"EnforceFleetTeams: {ex.Message}"); }
        }
        foreach (var fc in _byId.Values)
        {
            CCSPlayerController? c = null;
            try { c = Utilities.GetPlayerFromSlot(fc.Slot); } catch { }
            if (c == null || !c.IsValid) { fc.Simulator.Tick(); fc.Aim.Disarm(_pool); continue; }
            fc.Tick(_tick, c, _pool);
            EmitPerTickTelemetry(fc);
        }
        if (_ticksSinceSummary < 64) return;
        _ticksSinceSummary = 0;
        foreach (var fc in _byId.Values)
        {
            var (avg, loss) = fc.DrainSummary();
            Telemetry.Write("net_summary", new Dictionary<string, object?> {
                { "botId", fc.Id }, { "avgPingMs", avg },
                { "jitterMs", fc.Network.JitterRangeMs }, { "lossRate60s", loss } });
        }

        DrainStalePending();
        SweepRosterHusks();

        // 1-second persistent bot_quota re-assert. Engine resets quota at
        // warmup_end and round-restart; without periodic enforcement it
        // creeps up to default (10) and triggers the supercede CPU-loop
        // (320+ /sec). Cheap (one ExecuteCommand per second).
        EnforceBotQuota();
    }

    /// <summary>
    /// 1Hz sweep of <see cref="_pendingPersonaIds"/> — drops entries older
    /// than <see cref="PendingTimeoutTicks"/> (5 s). Without this, a silent
    /// bot_add refusal (engine race, gamemode lock, warmup transition) leaves
    /// persona IDs in the queue forever — Reconcile sums them into `total`
    /// and stops growing the fleet because it thinks the spawns are still
    /// in flight. Issue #9.
    /// </summary>

    // ─── Fleet-team enforcement ──────────────────────────────────────────
    // The engine re-assigns bot teams behind our back: `bot_add ct|t` is
    // advisory (ChangeTeam logs `willSwitch 0` denials), and on EVERY match
    // end the game dumps all players to <Unassigned> and brings back only a
    // few (issue #187 — reproduced 3/3). RevealController enforces teams
    // per tick but only while a reveal is active; this is the steady-state
    // counterpart for the plain fleet.
    //
    // Policy per pass (every TeamEnforceIntervalTicks, outside mapchange
    // and reveal):
    //   - mass dump (≥2 managed bots in Unassigned): repair EVERYONE to
    //     their intended fc.Team — the match-end signature, deterministic
    //     restore beats whatever partial re-assignment the engine did;
    //   - lone Unassigned bot: repair to fc.Team;
    //   - valid CT↔T mismatch inside the spawn-grace window: hold the
    //     requested team (this is what makes `replica_spawn_bots 2 ct`
    //     actually stick);
    //   - valid CT↔T mismatch after grace: ADOPT (fc.SetTeam) — halftime
    //     swaps and scrambles are legitimate and must not be fought.
    // Guards mirror RevealController.EnforceTeamMembership (#18/#98):
    // skip invalid controllers and any slot holding an authorized human.
    //
    // Hang-safety (2026-06-13 post-mortem): SwitchTeam during the round
    // teardown/spawn dance is the same race class as the v0.6.0.6
    // SwitchTeam-vs-mp_restartgame bug, and mid-transition TeamNum reads
    // 0 for every pawn-less controller — the old code would have seen a
    // phantom "mass dump" at every round end and issued a full-fleet
    // SwitchTeam burst inside the teardown frame. Now: a quiet window
    // after every round/match lifecycle event (NoteRoundTransition, wired
    // in ReplicaPlugin), a per-pass switch budget, a per-bot cooldown,
    // and a give-up that adopts the engine's team after repeated refusals.
    private int _ticksSinceTeamEnforce;
    private int _quietUntilTick;
    private bool _enforcerAnnounced;
    private const int TeamEnforceIntervalTicks  = 32;   // 0.5s at 64 tick
    private const int SpawnTeamGraceTicks       = 640;  // 10s
    private const int TransitionQuietTicks      = 192;  // 3s
    private const int MaxSwitchesPerPass        = 2;    // full 9-bot repair ≈ 2.5s
    private const int PerBotSwitchCooldownTicks = 128;  // 2s
    private const int GiveupWindowTicks         = 640;  // 10s
    private const int GiveupSwitchLimit         = 4;

    /// <summary>Round/match lifecycle event seen — hold all SwitchTeam
    /// activity until the engine's teardown/spawn dance settles. The
    /// match-end Unassigned dump (#187) is still repaired: enforcement
    /// resumes 3s after the LAST transition event and restores whatever
    /// the engine didn't bring back.</summary>
    public void NoteRoundTransition(string reason)
    {
        _quietUntilTick = Server.TickCount + TransitionQuietTicks;
    }

    private void EnforceFleetTeams()
    {
        if (_byId.Count == 0) return;
        if (IsMapchangeInProgress) return;
        if (Server.TickCount < _quietUntilTick) return;
        if (Reveal != null && Reveal.Stage != RevealController.RevealStage.Idle) return;

        if (!_enforcerAnnounced)
        {
            _enforcerAnnounced = true;
            Log.Info($"EnforceFleetTeams armed: interval={TeamEnforceIntervalTicks}t " +
                     $"grace={SpawnTeamGraceTicks}t quiet={TransitionQuietTicks}t " +
                     $"budget={MaxSwitchesPerPass}/pass cooldown={PerBotSwitchCooldownTicks}t");
        }

        int unassigned = 0;
        foreach (var fc in _byId.Values)
        {
            CCSPlayerController? c = null;
            try { c = Utilities.GetPlayerFromSlot(fc.Slot); } catch { }
            if (c == null || !c.IsValid || c.AuthorizedSteamID != null) continue;
            if (c.TeamNum < 2) unassigned++;
        }
        bool massRepair = unassigned >= 2;
        int now = Server.TickCount;
        int adopted = 0, deferred = 0, gaveUp = 0;
        var planned = new List<(FakeClient Fc, CCSPlayerController Ctrl, int Intended)>();

        foreach (var fc in _byId.Values)
        {
            CCSPlayerController? c = null;
            try { c = Utilities.GetPlayerFromSlot(fc.Slot); } catch { }
            if (c == null || !c.IsValid || c.AuthorizedSteamID != null) continue;

            int actual   = c.TeamNum;
            int intended = (int)fc.Team;
            if (actual == intended) continue;

            bool inGrace = now - fc.BindTick <= SpawnTeamGraceTicks;
            if (!(actual < 2 || massRepair || inGrace))
            {
                // Valid CT↔T move after grace: halftime swap / scramble —
                // legitimate, adopt instead of fighting.
                fc.SetTeam(actual == 3 ? FakeTeam.CT : FakeTeam.T);
                adopted++;
                continue;
            }

            // Anti ping-pong: roll the 10s window, count issued switches.
            if (now - fc.TeamSwitchWindowStart > GiveupWindowTicks)
            {
                fc.TeamSwitchWindowStart = now;
                fc.TeamSwitchesInWindow  = 0;
            }
            if (fc.TeamSwitchesInWindow >= GiveupSwitchLimit)
            {
                // Engine reverted us GiveupSwitchLimit times in 10s —
                // stop fighting (willSwitch-0 style refusal), follow it.
                if (actual >= 2)
                {
                    fc.SetTeam(actual == 3 ? FakeTeam.CT : FakeTeam.T);
                    gaveUp++;
                    Telemetry.Write("team_enforce_giveup", new Dictionary<string, object?> {
                        { "botId", fc.Id }, { "slot", fc.Slot },
                        { "adoptedTeam", fc.Team.ToString() } });
                }
                continue;  // Unassigned + give-up: leave it; next window retries.
            }

            if (now - fc.LastTeamSwitchTick < PerBotSwitchCooldownTicks) { deferred++; continue; }
            planned.Add((fc, c, intended));
        }

        // Budget: spread mass repairs over passes — the interval is 0.5s,
        // a full 9-bot restore completes in ~2.5s without ever putting
        // more than 2 engine team-change cascades into one frame.
        if (planned.Count > MaxSwitchesPerPass)
        {
            deferred += planned.Count - MaxSwitchesPerPass;
            planned.RemoveRange(MaxSwitchesPerPass, planned.Count - MaxSwitchesPerPass);
        }

        if (planned.Count > 0 || adopted > 0 || gaveUp > 0)
        {
            // Intent BEFORE action: if a SwitchTeam ever wedges the frame,
            // the telemetry shows what was attempted (AutoFlush is on).
            Log.Info($"EnforceFleetTeams: switching={planned.Count} adopted={adopted} " +
                     $"gaveUp={gaveUp} deferred={deferred} unassigned={unassigned}" +
                     (massRepair ? " (mass-repair)" : ""));
            Telemetry.Write("team_enforce", new Dictionary<string, object?> {
                { "switching", planned.Count }, { "adopted", adopted },
                { "gaveUp", gaveUp }, { "deferred", deferred },
                { "unassigned", unassigned }, { "mass", massRepair },
                { "slots", string.Join(",", planned.Select(p => p.Fc.Slot)) } });
        }

        foreach (var (fc, c, intended) in planned)
        {
            fc.LastTeamSwitchTick = now;
            fc.TeamSwitchesInWindow++;
            try { c.SwitchTeam((CsTeam)intended); }
            catch (Exception ex) { Log.Debug($"EnforceFleetTeams: switch slot={fc.Slot} → {fc.Team}: {ex.Message}"); }
        }
    }

    // ─── Roster-husk janitor ─────────────────────────────────────────────
    // Disconnected fake controllers sometimes survive as entities and keep
    // their team-roster membership — the scoreboard then shows phantom
    // members ("Alive 4/8" with 4 real CTs). Two known producers: kick
    // paths that miss (fixed: kickid-by-userid) and the engine's own bot
    // cleanup not recognizing Hider-flipped clients (m_bFakePlayer=0, so
    // CCSBotManager never frees them — root fixed C++-side by un-flipping
    // on unmark; this sweep is the safety net for any remaining source).
    // 1 Hz, ≤4 removals per pass, humans double-gated out.
    private const int MaxHuskRemovalsPerPass = 4;

    private void SweepRosterHusks()
    {
        int removed = 0;
        for (int slot = 0; slot < 64 && removed < MaxHuskRemovalsPerPass; slot++)
        {
            CCSPlayerController? c = null;
            try { c = Utilities.GetPlayerFromSlot(slot); } catch { }
            if (c == null || !c.IsValid || IsHuman(c)) continue;
            if (_byId.Values.Any(b => b.Slot == slot)) continue;
            bool husk;
            try { husk = c.Connected == PlayerConnectedState.Disconnected; }
            catch { continue; }
            if (!husk) continue;
            try
            {
                c.Remove();
                removed++;
                Log.Info($"SweepRosterHusks: removed disconnected husk controller slot={slot}");
            }
            catch (Exception ex) { Log.Debug($"SweepRosterHusks slot={slot}: {ex.Message}"); }
        }
        if (removed > 0)
            Telemetry.Write("husk_sweep", new Dictionary<string, object?> { { "removed", removed } });
    }

    private void DrainStalePending()
    {
        if (_pendingPersonaIds.Count == 0) return;
        int now = Server.TickCount;
        int dropped = 0;
        while (_pendingPersonaIds.Count > 0
            && now - _pendingPersonaIds.Peek().EnqueueTick > PendingTimeoutTicks)
        {
            var (pid, enqueueTick, _) = _pendingPersonaIds.Dequeue();
            // ReleaseSlot is belt-and-braces — a never-adopted persona's
            // ActiveOnSlot was never bound, so it's already null. Safe
            // no-op in the normal case.
            _registry.ReleaseSlot(pid);
            // Mirror the bot_add we issued but never saw resolve — the
            // counter sits unconsumed otherwise.
            if (_commandSpawnsPending > 0) _commandSpawnsPending--;
            Telemetry.Write("pending_timeout", new Dictionary<string, object?> {
                { "personaId", pid }, { "ageTicks", now - enqueueTick } });
            dropped++;
        }
        if (dropped > 0)
        {
            Log.Warn($"DrainStalePending: dropped {dropped} stale entries " +
                     $"(>{PendingTimeoutTicks / 64}s old). Engine may be refusing bot_add silently.");
        }
    }

    private void EmitPerTickTelemetry(FakeClient fc)
    {
        if (fc.Simulator.SpikeStartedThisTick)
            Telemetry.Write("net_spike", new Dictionary<string, object?> {
                { "botId", fc.Id }, { "peakMs", fc.Simulator.LastSpikePeakMs },
                { "durationMs", fc.Simulator.LastSpikeDurationMs }, { "tick", _tick } });
        if (fc.Simulator.LossThisTick)
            Telemetry.Write("net_loss", new Dictionary<string, object?>
                { { "botId", fc.Id }, { "tick", _tick } });
        var bufLoss = fc.Buffer.LastDropReasonLoss;
        var bufOver = fc.Buffer.LastDropReasonOverflow;
        if (bufLoss > 0) Telemetry.Write("buffer_drop", new Dictionary<string, object?>
            { { "botId", fc.Id }, { "reason", "loss" }, { "tick", _tick } });
        if (bufOver > 0) Telemetry.Write("buffer_drop", new Dictionary<string, object?>
            { { "botId", fc.Id }, { "reason", "overflow" }, { "tick", _tick } });
        if (bufLoss > 0 || bufOver > 0) fc.Buffer.Clear();
    }

    private void ReassertIdentity(FakeClient fc, int slot)
    {
        if (!_byId.ContainsKey(fc.Id)) return;
        if (fc.Slot != slot) return;
        try
        {
            var c = Utilities.GetPlayerFromSlot(slot);
            if (c == null || !c.IsValid) return;
            // c.IsBot is now False for managed bots (C++ Hider already wrote
            // m_bFakePlayer=0); pool flag is the trustworthy "managed bot"
            // signal. Same gate fix as OnClientPutInServer.
            if (_pool.Read(slot) == 0 && !c.IsBot) return;
            if (string.Equals(c.PlayerName, fc.Name, StringComparison.Ordinal)) return;
            fc.OverwriteNameOnController(c); // name-only — see FakeClient.cs
        }
        catch (Exception ex) { Log.Debug($"reassert slot={slot}: {ex.Message}"); }
    }

    public void Dispose() { OnUnload(); }
}
