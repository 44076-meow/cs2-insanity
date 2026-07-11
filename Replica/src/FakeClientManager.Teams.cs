using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace Replica;

// Steady-state fleet-team enforcement. Manager core: see FakeClientManager.cs.
public sealed partial class FakeClientManager
{
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
}
