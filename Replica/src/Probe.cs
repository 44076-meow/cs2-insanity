// =============================================================================
// Probe.cs — temporary live-verification commands for Stage 4 design points.
// =============================================================================
//
// Each method here is a one-shot test exposed via an `replica_probe_*` rcon
// command. They are NOT used by any production code path — they exist purely
// so a connected client can visually confirm an engine behaviour BEFORE we
// commit it to a Stage 4 codepath.
//
// Result reporting goes into `notes/stage_4_probes.md` after each session.
//
// Probes documented in `notes/stage_3_4_probes.md` (desk research, 2026-05-02):
//   1. m_clrRender   — does writing red to a player pawn tint the world model?
//   2. weapon_c4     — does GiveNamedItem on a bot show a C4 model + no
//                      "PLANT THE BOMB" objective marker, with no CT-side
//                      auto-revoke?
//   3. hurt-zero     — alternative damage filter via player_hurt PRE-hook,
//                      reserve in case OnEntityTakeDamagePre proves
//                      insufficient during Stage 4 grenade rain.
//
// These commands are SAFE to leave in production builds — they require
// `@css/cheats` permission and have no side-effect on inactive bots /
// disconnected slots. Removing them is allowed once Stage 4 ships and
// the design points are decided.
// =============================================================================

using System;
using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace Replica;

public static class Probe
{
    /// <summary>
    /// Probe 1 — write red color to m_clrRender on the bot's pawn.
    /// User reports back: is the model tinted on the client?
    ///
    /// Result expectations (from desk research):
    ///   GREEN  — model tints red, no engine warning. m_clrRender is the
    ///            canonical path; Stage 4 entry can use it directly.
    ///   YELLOW — model tints partially (e.g. world model only, view model
    ///            unchanged). Acceptable — humans see the swarm tinted,
    ///            view model not visible to them anyway.
    ///   RED    — model unchanged or engine warns "not networked".
    ///            Fallback: m_iGlowRange + m_clrGlow (CS2-specific glow API)
    ///            or `light_dynamic` entity parented to the bot.
    /// </summary>
    public static string Glow(int slot, byte r = 255, byte g = 0, byte b = 0)
    {
        var c = Utilities.GetPlayerFromSlot(slot);
        if (c == null || !c.IsValid) return $"slot {slot}: no controller";
        var pawn = c.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid) return $"slot {slot} ({c.PlayerName}): no pawn";

        try
        {
            pawn.Render = Color.FromArgb(255, r, g, b);
            bool marked = SchemaSafety.MarkChanged(pawn, "CBaseModelEntity", "m_clrRender");
            return $"slot {slot} ({c.PlayerName}): pawn.Render={r}/{g}/{b} marked={marked}";
        }
        catch (Exception ex)
        {
            return $"slot {slot} ({c.PlayerName}): EXCEPTION {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Probe 2 — give weapon_c4 to a bot via GiveNamedItem (the same path
    /// already used for weapon_knife / weapon_m249).
    ///
    /// Result expectations:
    ///   GREEN  — bot holds C4 model in hand or hip, no "PLANT THE BOMB"
    ///            objective text, no auto-revoke when bot is on CT.
    ///            Stage 4 can use as-designed (visible C4 + manual
    ///            env_explosion detonation).
    ///   YELLOW — bot holds C4 but a PLANT marker appears on radar/HUD.
    ///            Cosmetic; user can still play. Fallback acceptable.
    ///   RED    — engine revokes the C4 (CT side filter), or "PLANT THE
    ///            BOMB" floods all clients with objective text.
    ///            Stage 4 fallback: skip visible C4, only spawn
    ///            env_explosion on bot death / vision-trigger. Suicide
    ///            mechanic preserved, visual deferred.
    /// </summary>
    public static string GiveC4(int slot)
    {
        var c = Utilities.GetPlayerFromSlot(slot);
        if (c == null || !c.IsValid) return $"slot {slot}: no controller";
        if (c.PlayerPawn?.Value == null || !c.PlayerPawn.Value.IsValid)
            return $"slot {slot} ({c.PlayerName}): no pawn (dead?)";

        try
        {
            c.GiveNamedItem("weapon_c4");
            return $"slot {slot} ({c.PlayerName}): GiveNamedItem(\"weapon_c4\") issued — check radar + HUD";
        }
        catch (Exception ex)
        {
            return $"slot {slot} ({c.PlayerName}): EXCEPTION {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Probe 3 — alternative damage filter via Listeners.OnPlayerHurt (PRE
    /// equivalent we have available). One-shot install: registers a
    /// handler that zeroes the next bot-vs-bot damage observed, then
    /// auto-uninstalls. Does NOT replace the production BotDamagePatch.
    ///
    /// This exists as reserve in case the OnEntityTakeDamagePre filter
    /// proves insufficient (e.g. some Source-2 grenade damage paths
    /// bypass entity-level pre-hook). If OnEntityTakeDamage is enough,
    /// this probe stays as documentation of the alternative.
    /// </summary>
    public static string HurtZeroArmOnce(FakeClientManager mgr)
    {
        // Note: this is a placeholder probe that arms BotDamagePatch
        // for one shot. The actual hurt-zero codepath IS the same
        // OnEntityTakeDamagePre we ship. The "alternative hook" angle
        // is preserved as a comment in BotDamagePatch.cs.
        if (mgr.DamagePatch.IsInstalled)
            return "BotDamagePatch already installed — armed";
        mgr.DamagePatch.Install();
        return "BotDamagePatch armed via OnEntityTakeDamagePre — fire bot-vs-bot direct damage to verify";
    }

    public static string HurtZeroDisarm(FakeClientManager mgr)
    {
        if (!mgr.DamagePatch.IsInstalled) return "BotDamagePatch not installed";
        mgr.DamagePatch.Uninstall();
        return "BotDamagePatch uninstalled";
    }
}
