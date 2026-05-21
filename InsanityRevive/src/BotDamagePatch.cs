using System;
using CounterStrikeSharp.API.Core;

namespace InsanityRevive;

/// <summary>
/// Filters damage on managed bots during reveal stages.
///
/// Two flavors of damage are blocked when active:
///   1) Inflictor class is inferno / molotov_projectile / hegrenade_projectile
///      / env_explosion. Used by Stage 4 APOCALYPSE so the grenade-rain
///      effect and C4 carrier detonations fry humans but not the swarm
///      itself. Without this the molotovs from probe 2 of
///      `notes/stage_3_4_probes.md` would mulch our own bots first.
///
///      env_explosion side-effect: this also blocks damage from MAP-PLACED
///      env_explosion entities (workshop maps, demolition scripts, scripted
///      barrel bursts) — managed bots survive what a human in the same spot
///      doesn't. Accepted trade because Stage 4 dominates the use case and
///      the alternative (entity-tracking dict mapping handle → "is one of
///      ours") would need lifecycle hooks on SpawnExplosionAt + cleanup on
///      env_explosion's death event. Revisit if community-map play surfaces
///      a concrete asymmetry complaint.
///   2) Attacker is another managed bot. Belt-and-suspenders for Stage 1+
///      after `mp_teammates_are_enemies` experiments — even if that path
///      is reintroduced later, bots will not damage each other directly.
///      Self-damage (slot==slot) is allowed (legit env / fall damage).
///
/// Stage 4 carrier damage reduction (issue #26): if <see cref="CarrierPredicate"/>
/// returns true for the victim slot, surviving damage (i.e. damage that
/// passed the two blocklists above) is scaled by
/// <see cref="CarrierIncomingDamageMultiplier"/>. Keeps a C4 carrier alive
/// long enough to detonate under AWP/HE counter-pressure.
///
/// Implementation notes:
///   - Was: `VirtualFunctions.CBaseEntity_TakeDamageOldFunc.Hook(...)` which
///     CSSharp 1.0.367 marked obsolete.
///   - Now: `Listeners.OnEntityTakeDamagePre` — modern, documented, public.
///     Returning `HookResult.Handled` prevents the entire damage pipeline;
///     mutating <c>info.Damage</c> and returning <c>HookResult.Continue</c>
///     lets the pipeline run with the modified value.
///
/// Caller (RevealController) toggles via Install/Uninstall. NOT installed
/// at plugin Load — Stage 4 entry installs, EndReveal uninstalls.
/// </summary>
public sealed class BotDamagePatch
{
    /// <summary>
    /// Per-carrier incoming damage multiplier applied to damage that
    /// survives the inflictor- and attacker-class blocklists. 0.5 chosen
    /// from issue #26 acceptance: an unscoped AWP body shot (75) at 0.5x
    /// = 37.5 leaves a 100 HP carrier alive for at least one more hit.
    /// </summary>
    public const float CarrierIncomingDamageMultiplier = 0.5f;

    /// <summary>
    /// Predicate identifying Stage 4 C4 carriers by slot. Set by
    /// <c>RevealController.EnterStage4</c>, cleared in <c>CleanupReveal</c>.
    /// Null at all other times — no scaling applied.
    /// </summary>
    public Func<int, bool>? CarrierPredicate { get; set; }

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
            // entity once it ignites, HE before/at detonation, and the
            // env_explosion entity spawned by Stage 4 carrier detonation
            // (RevealController.SpawnExplosionAt — without this, every bot
            // inside Stage4ExplosionRadius=400 HU dies on every carrier blow).
            var inflictorEnt = info.Inflictor.Value;
            if (inflictorEnt != null) {
                var inflictorClass = inflictorEnt.DesignerName;
                if (inflictorClass == "inferno"
                    || inflictorClass == "molotov_projectile"
                    || inflictorClass == "hegrenade_projectile"
                    || inflictorClass == "env_explosion") {
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

            // Class 3 (issue #26): Stage 4 carrier damage reduction. Damage
            // that survived the two blocklists above is from a human or an
            // environmental hazard not on the inflictor blocklist. Scale
            // it for carriers so AWP/HE counter-pressure can't OHKO before
            // the detonation timer fires.
            if (CarrierPredicate?.Invoke(victimSlot) == true)
            {
                info.Damage *= CarrierIncomingDamageMultiplier;
            }
        } catch (Exception ex) {
            Log.Debug($"OnEntityTakeDamage filter: {ex.Message}");
        }
        return HookResult.Continue;
    }
}
