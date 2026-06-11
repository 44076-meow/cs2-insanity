using System;
using CounterStrikeSharp.API.Core;

namespace Replica;

/// <summary>
/// Filters damage on managed bots during reveal stages.
///
/// Two flavors of damage are blocked when active:
///   1) Inflictor class is inferno / molotov_projectile / hegrenade_projectile.
///      Used by Stage 4 APOCALYPSE so the grenade-rain effect fries humans
///      but not the swarm itself. Without this the molotovs from probe 2 of
///      `notes/stage_3_4_probes.md` would mulch our own bots first.
///   2) Attacker is another managed bot. Belt-and-suspenders for Stage 1+
///      after `mp_teammates_are_enemies` experiments — even if that path
///      is reintroduced later, bots will not damage each other directly.
///      Self-damage (slot==slot) is allowed (legit env / fall damage).
///
/// Implementation notes:
///   - Was: `VirtualFunctions.CBaseEntity_TakeDamageOldFunc.Hook(...)` which
///     CSSharp 1.0.367 marked obsolete.
///   - Now: `Listeners.OnEntityTakeDamagePre` — modern, documented, public.
///     Returning `HookResult.Handled` prevents the entire damage pipeline.
///
/// Caller (RevealController) toggles via Install/Uninstall. NOT installed
/// at plugin Load — Stage 4 entry installs, EndReveal uninstalls.
/// </summary>
public sealed class BotDamagePatch
{
    private readonly BasePlugin _plugin;
    private readonly FakeClientManager _mgr;
    private bool _hooked;
    private Listeners.OnEntityTakeDamagePre? _handler;

    public BotDamagePatch(BasePlugin plugin, FakeClientManager mgr)
    {
        _plugin = plugin;
        _mgr = mgr;
    }

    public void Install()
    {
        if (_hooked) return;
        try {
            _handler = OnEntityTakeDamage;
            _plugin.RegisterListener<Listeners.OnEntityTakeDamagePre>(_handler);
            _hooked = true;
            Log.Info("BotDamagePatch installed (Listeners.OnEntityTakeDamagePre)");
        } catch (Exception ex) {
            Log.Error($"BotDamagePatch install: {ex.Message}");
            _handler = null;
        }
    }

    public void Uninstall()
    {
        if (!_hooked) return;
        try {
            if (_handler != null)
                _plugin.RemoveListener<Listeners.OnEntityTakeDamagePre>(_handler);
        } catch (Exception ex) {
            Log.Debug($"BotDamagePatch unhook: {ex.Message}");
        }
        _hooked = false;
        _handler = null;
    }

    public bool IsInstalled => _hooked;

    private HookResult OnEntityTakeDamage(CEntityInstance entity, CTakeDamageInfo info)
    {
        try {
            // We only filter damage TO managed bots. Humans take damage normally.
            if (entity is not CCSPlayerPawn victimPawn) return HookResult.Continue;
            var victimCtrl = victimPawn.Controller.Value as CCSPlayerController;
            if (victimCtrl == null || !victimCtrl.IsValid) return HookResult.Continue;

            int victimSlot = (int)victimCtrl.Slot;
            bool victimIsManagedBot = _mgr.FindBySlot(victimSlot) != null;
            if (!victimIsManagedBot) return HookResult.Continue;

            // Class 1: environmental projectile rain (Stage 4 APOCALYPSE).
            // Inflictor is the projectile/inferno entity itself, not the
            // thrower. This catches molotov_projectile mid-air, the inferno
            // entity once it ignites, and HE before/at detonation.
            var inflictorEnt = info.Inflictor.Value;
            if (inflictorEnt != null) {
                var inflictorClass = inflictorEnt.DesignerName;
                if (inflictorClass == "inferno"
                    || inflictorClass == "molotov_projectile"
                    || inflictorClass == "hegrenade_projectile") {
                    return HookResult.Handled;
                }
            }

            // Class 2: bot-vs-bot direct damage. Self-damage allowed.
            var attackerEnt = info.Attacker.Value;
            if (attackerEnt is CCSPlayerPawn attackerPawn) {
                var attackerCtrl = attackerPawn.Controller.Value as CCSPlayerController;
                if (attackerCtrl != null && attackerCtrl.IsValid) {
                    int attackerSlot = (int)attackerCtrl.Slot;
                    if (attackerSlot == victimSlot) return HookResult.Continue;
                    if (_mgr.FindBySlot(attackerSlot) != null) {
                        return HookResult.Handled;
                    }
                }
            }
        } catch (Exception ex) {
            Log.Debug($"OnEntityTakeDamage filter: {ex.Message}");
        }
        return HookResult.Continue;
    }
}
