using System;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;

namespace Replica;

/// <summary>
/// Holds a stable resident fleet of fake-clients on the server.
///
/// Vision (v0.6.0+, post pivot from 24h public-server simulation):
/// friend connects, sees a plausible lobby of <see cref="Config.FleetSize"/>
/// bots with persistent identities. Plays in the illusion they're real
/// players. Admin triggers !reveal — see <c>RevealController</c> (P/12).
///
/// Reconciliation runs once per second from FakeClientManager.OnTick.
/// Skips during mapchange (v0.5.1 survival mechanism owns that window).
/// Spawns to grow, Despawns LRU (oldest LastSeenAt first) to shrink.
///
/// Boot sequence:
///   1. Plugin OnLoad → Config read → FakeClientManager.OnLoad
///   2. FleetManager constructor — capture FakeClientManager + Telemetry
///   3. First Tick after ~1s → Reconcile() → Spawn(target) bots
///   4. Engine `bot_join_after_player=true` is BYPASSED by `bot_add` so
///      bots come up even with zero humans — verified in v0.5.2 smoke
///      test E (5 bots spawned without me connected).
/// </summary>
public sealed class FleetManager
{
    private readonly FakeClientManager _mgr;

    /// <summary>Tick counter for 1Hz reconciliation cadence.</summary>
    private int _ticksSinceReconcile;

    /// <summary>
    /// Suppresses Reconcile until the first OnMapStart completes — pool
    /// state during the boot/changelevel transition is not safe to drive
    /// fleet decisions from. Cleared in <see cref="OnMapStartComplete"/>.
    /// </summary>
    private bool _ready;

    public FleetManager(FakeClientManager mgr)
    {
        _mgr = mgr;
    }

    /// <summary>
    /// Called from FakeClientManager.OnMapStart's tail (after zombie kicks
    /// + respawn batch is scheduled). Marks fleet as ready to reconcile
    /// on the new map.
    /// </summary>
    public void OnMapStartComplete()
    {
        _ready = true;
    }

    /// <summary>
    /// Driver entry. Called every server tick from FakeClientManager.OnTick.
    /// Reconciles at 1Hz (every 64 ticks). Skips during mapchange.
    /// </summary>
    public void OnTick()
    {
        if (!_ready) return;
        if (++_ticksSinceReconcile < 64) return;
        _ticksSinceReconcile = 0;
        try { Reconcile(); }
        catch (Exception ex) { Log.Error($"FleetManager Reconcile: {ex.Message}"); }
    }

    /// <summary>
    /// Spawn or despawn to match <see cref="Config.FleetSize"/>. Idempotent
    /// when at target. Mapchange-aware (skips during synthetic disconnect
    /// cascade window).
    /// </summary>
    public void Reconcile()
    {
        if (_mgr.IsMapchangeInProgress) return;

        int target = _mgr.Config.FleetSize;
        int active = _mgr.All.Count;
        int pending = _mgr.PendingPersonaCount;
        int total = active + pending;

        if (total == target) return;

        if (total < target)
        {
            int n = target - total;
            // Alternate teams to balance the fleet. Counts include pending
            // bots so we don't all stack one side.
            int ctCount = _mgr.All.Count(b => b.Team == FakeTeam.CT);
            for (int i = 0; i < n; i++)
            {
                var team = (ctCount + i) % 2 == 0 ? FakeTeam.CT : FakeTeam.T;
                _mgr.Spawn(team);
            }
            _mgr.Telemetry.Write("fleet_grow", new Dictionary<string, object?> {
                { "target", target }, { "wasActive", active },
                { "wasPending", pending }, { "spawnedNow", n } });
        }
        else
        {
            // Shrink — kick oldest-bound first (LRU by Persona.LastSeenAt
            // proxy: lower botId arrived earlier in this session).
            int n = total - target;
            var toKick = _mgr.All.OrderBy(b => b.Id).Take(n).ToList();
            foreach (var fc in toKick) _mgr.Despawn(fc.Id, "fleet_shrink");
            _mgr.Telemetry.Write("fleet_shrink", new Dictionary<string, object?> {
                { "target", target }, { "wasActive", active },
                { "kickedNow", n } });
        }
    }
}
