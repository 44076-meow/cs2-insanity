using System;
using System.Collections.Generic;

namespace InsanityRevive;

/// <summary>
/// High-level "what kind of player is this bot pretending to be" — used
/// to bias all the other fields so they correlate (a school-rusher has
/// low-tier hardware AND high aggression AND a chatty toxic personality
/// — not a random combo of those).
///
/// Weights from spec 2026-05-08. Total = 100.
/// </summary>
public enum BotArchetype
{
    SchoolRusher,    // 12% — школьник-раш (low skill, high aggression, wifi, chatty)
    SilverKamikaze,  // 15% — серебряный камикадзе (very low skill, high aggression)
    EgoCarry,        // 10% — тащер-эгоист (high skill, ego, ethernet)
    AwpCamper,       //  8% — AWP-кемпер (high aim, low aggression, patient)
    TeamPlayer,      // 12% — тимплеер (mid skill, balanced, low toxicity)
    Tilter,          //  8% — тилтер (mid skill, very high tilt-prone)
    Silent,          // 15% — молчун (varies, low chattiness, low toxicity)
    BoomerOnM4,      //  6% — бумер на M4 (mid skill, low aggression, ethernet)
    Smurf,           //  4% — смурф (very high skill, low account age, high-end gear)
    OldPC,           //  5% — дед на старом ПК (mid skill, potato hardware)
    Random,          //  5% — случайный (no constraints — pure noise sample)
}

public enum HardwareTier { Potato, Low, Mid, High, Enthusiast }

public enum Region { EuWest, EuEast, RuCis, NorthAm, SouthAm, Asia, Other }

public enum BotLanguage { Russian, English, German, French, Spanish, Other }

public enum Mood { Fresh, Warmed, Tired, Frustrated }

/// <summary>
/// How the bot manifests complacency — the visual signature of "не играет".
/// Set at generation time from archetype; doesn't change at runtime.
/// Future behaviour modules (movement, buy, utility) read this to pick
/// flavor: showoff bots try no-scopes, lazy bots sit further back, passive
/// bots disengage to weird angles. (Used as enum hint for now; behaviour
/// modules don't exist yet.)
/// </summary>
public enum ComplacencyStyle { Lazy, Showoff, Passive }

/// <summary>
/// Round-end payload for <see cref="BotProfile.NotifyEvent(string,RoundEventArgs?)"/>.
/// Caller computes team averages from observed stats so bots don't "know"
/// each other's hidden SkillRating — see InsanityRevivePlugin's
/// <c>EventRoundEnd</c> handler.
/// </summary>
public sealed class RoundEventArgs
{
    public bool   Win                 { get; init; }
    public double OwnTeamAvgSkill     { get; init; }   // 0..100
    public double EnemyTeamAvgSkill   { get; init; }   // 0..100
    /// <summary>Optional bot's own contribution metric (kills/deaths
    /// or normalized 0..1). Reserved for future use; not consumed yet.</summary>
    public double OwnPerformance      { get; init; }
}

/// <summary>
/// Per-archetype generation constraints. All fields are ranges or
/// deterministic biases; the actual values get jittered inside the
/// archetype's range so two SchoolRushers don't end up identical.
/// </summary>
internal readonly struct ArchetypeParams
{
    public readonly int   Weight;
    public readonly int   SkillMin, SkillMax;
    public readonly int   AimMin, AimMax;            // can deviate from skill
    public readonly int   AggressionMin, AggressionMax;
    public readonly int   ToxicityMin, ToxicityMax;
    public readonly int   ChattinessMin, ChattinessMax;
    public readonly int   TiltProneMin, TiltProneMax;
    public readonly int   PatienceMin, PatienceMax;
    public readonly int   TeamPlayerMin, TeamPlayerMax;
    /// <summary>weight per HardwareTier (Potato..Enthusiast, 5 entries).</summary>
    public readonly int[] HardwareWeights;
    /// <summary>weight per ConnectionType bucket (CableLike, WifiLike, MobileLike, FarLike, ChaosLike, VpnLike).</summary>
    public readonly int[] NetworkBucketWeights;
    public readonly int   AccountAgeMinDays, AccountAgeMaxDays;

    public ArchetypeParams(int weight,
        int skMin, int skMax, int amMin, int amMax,
        int agMin, int agMax, int toMin, int toMax, int chMin, int chMax,
        int tlMin, int tlMax, int paMin, int paMax, int tpMin, int tpMax,
        int[] hw, int[] net, int aaMin, int aaMax)
    {
        Weight = weight;
        SkillMin = skMin; SkillMax = skMax;
        AimMin = amMin;   AimMax = amMax;
        AggressionMin = agMin; AggressionMax = agMax;
        ToxicityMin = toMin;   ToxicityMax = toMax;
        ChattinessMin = chMin; ChattinessMax = chMax;
        TiltProneMin = tlMin;  TiltProneMax = tlMax;
        PatienceMin = paMin;   PatienceMax = paMax;
        TeamPlayerMin = tpMin; TeamPlayerMax = tpMax;
        HardwareWeights = hw;
        NetworkBucketWeights = net;
        AccountAgeMinDays = aaMin; AccountAgeMaxDays = aaMax;
    }
}

/// <summary>
/// Single source of truth for everything about a bot's "character" —
/// identity, hardware, network, skill, psychology. Any future module
/// (aim, chat, buy, movement) reads from here instead of inventing its
/// own per-bot rng.
///
/// Static fields (init-only): generated once from SteamID seed →
/// reproducible across sessions. Includes the existing NetworkProfile
/// (ping/jitter/spike/loss params) as a sub-object.
///
/// Dynamic fields: Mood, Tilt, Streaks, RoundsPlayed. Reset between
/// adoptions. Updated by NotifyEvent so behaviour modules can observe
/// "bot got tilted" and adjust their reads of CurrentAimSkill /
/// CurrentReactionMs / CurrentChattiness / CurrentToxicity.
///
/// DEFERRED (per spec "не пихать всё сразу"):
///   - Habits: PreferredWeapons, EconomyStyle, PreferredPositions,
///     UtilityUsage, PreferredSides — wait until buy/movement modules.
///   - AvatarSeed — no avatar logic yet.
///   - JSON persistence to survive bot disconnect/reconnect — current
///     adoption flow regenerates from stable seed instead.
///   - "Saved personalities" pool in DB — optional per spec.
/// </summary>
public sealed class BotProfile
{
    // === Identity =====================================================
    public ulong  Seed             { get; init; }
    public ulong  SteamID          { get; init; }
    public Region Region           { get; init; }
    public BotLanguage Language    { get; init; }
    public int    AccountAgeDays   { get; init; }
    public int    LocalTimeZoneOffsetHours { get; init; }

    // === Hardware + network (correlated) ==============================
    public HardwareTier   Hardware    { get; init; }
    public int            BaselineFPS { get; init; }
    public NetworkProfile Network     { get; init; } = null!;

    // === Archetype + skill + psychology (correlated) ==================
    public BotArchetype Archetype       { get; init; }
    public int          SkillRating     { get; init; }
    public int          AimSkillBase    { get; init; }
    public int          MovementSkill   { get; init; }
    public int          GameSense       { get; init; }
    public int          ReactionBaseMs  { get; init; }
    public int          AggressionBase  { get; init; }
    public int          ToxicityBase    { get; init; }
    public int          ChattinessBase  { get; init; }
    public int          TiltProneness   { get; init; }   // 0..100, sensitivity
    public int          Patience        { get; init; }
    public int          TeamPlayerBias  { get; init; }

    public ComplacencyStyle ComplacencyStyle { get; init; }
    public DateTime SessionStartedAt { get; init; } = DateTime.UtcNow;

    // === Dynamic state ================================================
    public Mood  Mood             { get; private set; } = Mood.Fresh;
    public int   Tilt             { get; private set; }   // 0..100
    public float Complacency      { get; private set; }   // 0..100 — расслабон от лёгкого противника
    public int   RoundsAtHighComp { get; private set; }   // consecutive rounds with comp ≥ 50
    public int   WinStreak        { get; private set; }
    public int   LossStreak       { get; private set; }
    public int   RoundsPlayed     { get; private set; }
    public int   Kills            { get; private set; }
    public int   Deaths           { get; private set; }

    // === Computed (read-only — clamp Mood/Tilt/Complacency into base values) ===
    //
    // Aim:    base × moodMult × (1 − tilt/200) × (1 − complacency/250)
    //         At complacency=100 → ≈40% degradation; at tilt=100 → ≈50%.
    // React:  base × (1 + tilt/100) × (1 + complacency/300) / moodMult
    //         At complacency=100 → ~33% slower; at tilt=100 → 2× slower.
    // Sense:  base × (1 − complacency/200)
    //         At complacency=100 → 50% — "не смотрит на радар, не слышит шаги".
    public int CurrentAimSkill =>
        ClampSkill((int)Math.Round(
            AimSkillBase * MoodMultiplier(Mood)
            * (1.0 - Tilt / 200.0)
            * (1.0 - Complacency / 250.0)));
    public int CurrentReactionMs =>
        Math.Clamp((int)Math.Round(
            ReactionBaseMs * (1.0 + Tilt / 100.0)
            * (1.0 + Complacency / 300.0)
            / MoodMultiplier(Mood)), 100, 800);
    public int CurrentGameSense =>
        ClampSkill((int)Math.Round(GameSense * (1.0 - Complacency / 200.0)));
    public int CurrentChattiness =>
        Math.Clamp(ChattinessBase + (ToxicityBase > 50 ? Tilt / 4 : -Tilt / 8), 0, 100);
    public int CurrentToxicity =>
        Math.Clamp(ToxicityBase + Tilt / 3, 0, 100);

    // === Lifecycle ====================================================
    /// <summary>
    /// Update dynamic state in response to a runtime event. Modules fire
    /// this from EventPlayerDeath / EventRoundEnd / etc. For events not
    /// listed, this is a no-op (forward-compat — modules can fire
    /// arbitrary kinds without errors).
    ///
    /// "RoundEnd" requires non-null <paramref name="args"/> — it updates
    /// streaks + mood + complacency (skill-gap based). The string-only
    /// <see cref="NotifyEvent(string)"/> overload is no-op for "RoundEnd"
    /// (all in-tree producers pass args; the no-args call survives only
    /// as a forward-compat hook for "Death"/"Kill"/"RoundWin"/"RoundLoss"
    /// and unknown kinds).
    /// </summary>
    public void NotifyEvent(string kind) => NotifyEvent(kind, null);

    public void NotifyEvent(string kind, RoundEventArgs? args)
    {
        switch (kind)
        {
            case "Death":
                Deaths++;
                Tilt = Math.Min(100, Tilt + 5 + (TiltProneness / 10));
                break;

            case "Kill":
                Kills++;
                Tilt = Math.Max(0, Tilt - 5);
                break;

            case "RoundEnd":
                if (args == null) break;
                RoundsPlayed++;
                if (args.Win)
                {
                    WinStreak++;
                    LossStreak = 0;
                    Tilt = Math.Max(0, Tilt - 3);
                }
                else
                {
                    LossStreak++;
                    WinStreak = 0;
                    if (LossStreak >= 2)
                        Tilt = Math.Min(100, Tilt + 3 + (TiltProneness / 12));
                }
                RecomputeMood();
                if (RoundsPlayed > 1)
                    UpdateComplacency(args);
                // Frustrated mood breaks complacency by definition.
                if (Mood == Mood.Frustrated && Complacency > 15f)
                    Complacency = 15f;
                if (Complacency >= 50f) RoundsAtHighComp++;
                else                    RoundsAtHighComp = 0;
                break;

            // Draws / forfeits / aborts (winnerTeam ∉ {T,CT}). The round
            // still happened — bump RoundsPlayed and recompute mood —
            // but neither team "won", so streaks, Tilt, and the
            // skill-gap complacency drift stay put. Without this kind,
            // EventRoundEnd had to early-return on draws (per #37) and
            // RoundsPlayed silently undercounted; that left the
            // complacency machine in its "first-round excluded"
            // baseline longer than the bots had actually been playing.
            case "Draw":
                RoundsPlayed++;
                RecomputeMood();
                if (Mood == Mood.Frustrated && Complacency > 15f)
                    Complacency = 15f;
                if (Complacency >= 50f) RoundsAtHighComp++;
                else                    RoundsAtHighComp = 0;
                break;

            // Legacy single-string events — kept for forward compat with
            // any caller that only knows the string-only NotifyEvent. The
            // streak/tilt update they do is also done inside RoundEnd
            // when args are provided, so don't mix-and-match (caller
            // picks one path or the other).
            case "RoundWin":
                WinStreak++;
                LossStreak = 0;
                Tilt = Math.Max(0, Tilt - 3);
                break;

            case "RoundLoss":
                LossStreak++;
                WinStreak = 0;
                if (LossStreak >= 2)
                    Tilt = Math.Min(100, Tilt + 3 + (TiltProneness / 12));
                break;
        }
    }

    /// <summary>
    /// Skill-gap complacency drift, called once per RoundEnd when args
    /// are present. Implements the spec from 2026-05-08:
    ///   gap > +40   → +10..20
    ///   gap > +25   → +5..10
    ///   gap > +15   → small drift toward 0
    ///   ±15        → equal match, drift to 0
    ///   gap < -15   → -5..10 (собирается)
    ///   gap < -30   → -10..20
    /// Plus: Win × highGap → +3..5 (confirms "easy"). Loss × comp≥50 →
    /// wakeup chance roll (60% − comp/4, with psychology mods).
    /// Smurf bonus ×1.3 on positive deltas (играет вполсилы по дизайну).
    /// Per-round movement capped to avoid >25 jumps in either direction
    /// (besides successful wakeups, which can dump 30..50).
    /// </summary>
    private void UpdateComplacency(RoundEventArgs args)
    {
        var rng = new MiniRng(Seed ^ ((ulong)RoundsPlayed * 0x9E3779B97F4A7C15UL));
        double gap = args.OwnTeamAvgSkill - args.EnemyTeamAvgSkill;
        float delta;

        if (gap > 40)        delta = 10f + rng.NextRange(0, 11);    // +10..20
        else if (gap > 25)   delta =  5f + rng.NextRange(0,  6);    // +5..10
        else if (gap > 15)   delta = -Math.Min(Complacency, 1f + rng.NextRange(0, 3));
        else if (gap > -15)  delta = -Math.Min(Complacency, 2f + rng.NextRange(0, 4));
        else if (gap > -30)  delta = -(5f + rng.NextRange(0, 6));   // -5..-10
        else                 delta = -(10f + rng.NextRange(0, 11)); // -10..-20

        // Smurf — recharges complacency 1.3× (плеер вполсилы по дизайну).
        if (Archetype == BotArchetype.Smurf && delta > 0)
            delta *= 1.3f;

        // Issue #40 — ComplacencyStyle as active modifier on positive drift.
        // Showoff gets cocky faster (ego loves the lead); Lazy accumulates
        // slowly-but-persistently; Passive temperament resists zone-out.
        // Negative drift is style-agnostic — losing pulls everyone back
        // toward focus at the same rate.
        if (delta > 0)
        {
            delta *= ComplacencyStyle switch
            {
                ComplacencyStyle.Showoff => 1.15f,
                ComplacencyStyle.Lazy    => 1.05f,
                ComplacencyStyle.Passive => 0.80f,
                _                         => 1.0f,
            };
        }

        // Round outcome modifiers.
        if (args.Win && gap > 25)
            delta += 3f + rng.NextRange(0, 3);  // +3..5 — "и так норм"
        else if (!args.Win && Complacency >= 50f)
            delta = ApplyWakeupChance(delta, rng);

        // Cap per-round movement (anti-detect: real complacency drifts smoothly).
        if (delta >  25f) delta =  25f;
        if (delta < -50f) delta = -50f;  // wakeups can be larger

        Complacency = Math.Clamp(Complacency + delta, 0f, 100f);

        // Issue #40 — Passive soft-ceiling. Defensive temperament won't
        // allow full zone-out; floor positive complacency at 70. Wakeups
        // can still pull below freely.
        if (ComplacencyStyle == ComplacencyStyle.Passive && Complacency > 70f)
            Complacency = 70f;
    }

    /// <summary>
    /// Loss-while-complacent dice roll. Returns the delta to apply
    /// instead of the raw spec one — successful wakeup is a big drop
    /// (30..50, modulated by aggression), failed wakeup is symbolic
    /// (-2..-5) so the bot keeps playing relaxed for another round.
    /// </summary>
    private float ApplyWakeupChance(float fallthroughDelta, MiniRng rng)
    {
        // Base 60% − complacency/4. At comp=50 → 47.5%, comp=90 → 37.5%.
        double wakeupChance = 0.60 - Complacency / 400.0;
        if (TiltProneness > 70)   wakeupChance *= 0.7;  // тилтеры вместо собранности уходят в тильт
        if (GameSense > 60)       wakeupChance *= 1.3;  // опытные раньше замечают
        if (AggressionBase > 70)  wakeupChance *= 1.2;  // агрессивные собираются быстрее
        // Issue #40 — ComplacencyStyle wakeup bias. Showoff ego-shocks back
        // into focus when losing while ahead; Lazy stays apathetic and
        // rarely wakes; Passive sits at baseline.
        if (ComplacencyStyle == ComplacencyStyle.Showoff) wakeupChance *= 1.3;
        else if (ComplacencyStyle == ComplacencyStyle.Lazy)    wakeupChance *= 0.5;

        if (rng.NextDouble() < wakeupChance)
        {
            // Wakeup successful — big drop.
            float drop = 30f + rng.NextRange(0, 21);
            // Aggressive bots wake but still slightly underestimate;
            // clamp final complacency to land in 20..30 range.
            if (AggressionBase > 70)
            {
                float target = 20f + rng.NextRange(0, 11);
                drop = Math.Max(drop, Complacency - target);
            }
            return -drop;
        }
        // Wakeup failed — symbolic drop, bot keeps playing relaxed.
        return -2f - rng.NextRange(0, 4);
    }

    private void RecomputeMood()
    {
        var elapsed = DateTime.UtcNow - SessionStartedAt;
        // Frustrated overrides everything if losing streak is bad.
        if (LossStreak >= 3) { Mood = Mood.Frustrated; return; }
        if (elapsed > TimeSpan.FromMinutes(90) || RoundsPlayed >= 30) { Mood = Mood.Tired; return; }
        if (RoundsPlayed >= 5) { Mood = Mood.Warmed; return; }
        Mood = Mood.Fresh;
    }

    private static double MoodMultiplier(Mood m) => m switch
    {
        Mood.Fresh       => 1.00,
        Mood.Warmed      => 1.05,
        Mood.Tired       => 0.85,
        Mood.Frustrated  => 0.80,
        _                => 1.00,
    };

    private static int ClampSkill(int v) => v < 0 ? 0 : v > 100 ? 100 : v;

    // === Generation ===================================================
    private static readonly Dictionary<BotArchetype, ArchetypeParams> _archetypes = new()
    {
        // hw weights index:    Potato Low Mid High Enthusiast
        // net bucket index:    Cable Wifi Mobile Far Chaos Vpn

        [BotArchetype.SchoolRusher] = new(12,
            skMin: 35, skMax: 55,    amMin: 25, amMax: 50,
            agMin: 75, agMax: 100,   toMin: 50, toMax: 90, chMin: 60, chMax: 95,
            tlMin: 60, tlMax: 90,    paMin: 5,  paMax: 25, tpMin: 10, tpMax: 35,
            hw:  new[] { 30, 40, 25,  5,  0 },
            net: new[] {  5, 60, 15,  5, 10,  5 },
            aaMin: 30, aaMax: 730),

        [BotArchetype.SilverKamikaze] = new(15,
            skMin: 20, skMax: 40,    amMin: 15, amMax: 35,
            agMin: 70, agMax: 95,    toMin: 40, toMax: 80, chMin: 30, chMax: 70,
            tlMin: 40, tlMax: 70,    paMin: 5,  paMax: 25, tpMin: 20, tpMax: 50,
            hw:  new[] { 20, 40, 30, 10,  0 },
            net: new[] { 25, 40, 15,  5, 10,  5 },
            aaMin: 60, aaMax: 1500),

        [BotArchetype.EgoCarry] = new(10,
            skMin: 70, skMax: 85,    amMin: 70, amMax: 90,
            agMin: 55, agMax: 80,    toMin: 30, toMax: 70, chMin: 30, chMax: 60,
            tlMin: 35, tlMax: 60,    paMin: 30, paMax: 60, tpMin: 20, tpMax: 50,
            hw:  new[] {  0, 10, 35, 40, 15 },
            net: new[] { 60, 25,  5,  5,  0,  5 },
            aaMin: 1000, aaMax: 4500),

        [BotArchetype.AwpCamper] = new(8,
            skMin: 60, skMax: 80,    amMin: 80, amMax: 95,
            agMin: 10, agMax: 30,    toMin: 10, toMax: 40, chMin: 15, chMax: 45,
            tlMin: 15, tlMax: 35,    paMin: 70, paMax: 95, tpMin: 30, tpMax: 60,
            hw:  new[] {  0, 10, 35, 40, 15 },
            net: new[] { 65, 25,  5,  0,  0,  5 },
            aaMin: 800, aaMax: 4000),

        [BotArchetype.TeamPlayer] = new(12,
            skMin: 50, skMax: 70,    amMin: 45, amMax: 70,
            agMin: 30, agMax: 60,    toMin: 5,  toMax: 25, chMin: 40, chMax: 75,
            tlMin: 10, tlMax: 30,    paMin: 50, paMax: 80, tpMin: 65, tpMax: 95,
            hw:  new[] {  5, 25, 45, 20,  5 },
            net: new[] { 35, 40, 10,  5,  5,  5 },
            aaMin: 500, aaMax: 3500),

        [BotArchetype.Tilter] = new(8,
            skMin: 55, skMax: 75,    amMin: 50, amMax: 75,
            agMin: 40, agMax: 75,    toMin: 50, toMax: 90, chMin: 50, chMax: 90,
            tlMin: 80, tlMax: 100,   paMin: 20, paMax: 50, tpMin: 25, tpMax: 55,
            hw:  new[] {  5, 25, 45, 20,  5 },
            net: new[] { 25, 40, 10,  5, 10, 10 },
            aaMin: 400, aaMax: 2500),

        [BotArchetype.Silent] = new(15,
            skMin: 40, skMax: 80,    amMin: 35, amMax: 80,
            agMin: 25, agMax: 65,    toMin: 0,  toMax: 25, chMin: 0,  chMax: 25,
            tlMin: 5,  tlMax: 30,    paMin: 40, paMax: 80, tpMin: 30, tpMax: 65,
            hw:  new[] { 10, 25, 35, 25,  5 },
            net: new[] { 30, 35,  5, 10, 10, 10 },
            aaMin: 300, aaMax: 4000),

        [BotArchetype.BoomerOnM4] = new(6,
            skMin: 45, skMax: 65,    amMin: 40, amMax: 65,
            agMin: 20, agMax: 45,    toMin: 5,  toMax: 30, chMin: 20, chMax: 50,
            tlMin: 10, tlMax: 30,    paMin: 60, paMax: 90, tpMin: 40, tpMax: 75,
            hw:  new[] {  5, 25, 45, 20,  5 },
            net: new[] { 70, 20,  0,  5,  0,  5 },
            aaMin: 2500, aaMax: 6000),

        [BotArchetype.Smurf] = new(4,
            skMin: 85, skMax: 95,    amMin: 85, amMax: 98,
            agMin: 30, agMax: 80,    toMin: 20, toMax: 70, chMin: 20, chMax: 60,
            tlMin: 5,  tlMax: 25,    paMin: 30, paMax: 70, tpMin: 20, tpMax: 60,
            hw:  new[] {  0,  5, 25, 40, 30 },
            net: new[] { 75, 15,  0,  5,  0,  5 },
            aaMin: 5,  aaMax: 200),

        [BotArchetype.OldPC] = new(5,
            skMin: 40, skMax: 60,    amMin: 35, amMax: 60,
            agMin: 30, agMax: 55,    toMin: 5,  toMax: 30, chMin: 10, chMax: 40,
            tlMin: 10, tlMax: 35,    paMin: 50, paMax: 80, tpMin: 40, tpMax: 70,
            hw:  new[] { 60, 30, 10,  0,  0 },
            net: new[] { 25, 45, 10, 10,  5,  5 },
            aaMin: 1500, aaMax: 5500),

        [BotArchetype.Random] = new(5,
            skMin: 20, skMax: 95,    amMin: 15, amMax: 95,
            agMin: 5,  agMax: 95,    toMin: 0,  toMax: 95, chMin: 0,  chMax: 95,
            tlMin: 5,  tlMax: 95,    paMin: 5,  paMax: 95, tpMin: 5,  tpMax: 95,
            hw:  new[] { 20, 20, 20, 20, 20 },
            net: new[] { 20, 20, 15, 15, 15, 15 },
            aaMin: 30, aaMax: 5000),
    };

    /// <summary>
    /// Build a fully-correlated profile from a stable seed. Same seed
    /// always returns same static-field result (Mood/Tilt/etc start
    /// fresh per call; static fields don't drift).
    /// </summary>
    public static BotProfile Generate(ulong seed)
    {
        var rng = new MiniRng(seed);

        // 1. Archetype by weighted random.
        BotArchetype arch = PickArchetype(rng.NextUlong());
        var ap = _archetypes[arch];

        // 2. Region — independent of archetype mostly.
        Region region = (Region)((rng.NextUlong() & 0xFFFF) % 7);
        BotLanguage lang = PickLanguageForRegion(region, rng.NextUlong());

        // 3. Hardware tier — biased by archetype.
        HardwareTier hw = PickWeighted(ap.HardwareWeights, rng.NextUlong()) switch
        {
            0 => HardwareTier.Potato,
            1 => HardwareTier.Low,
            2 => HardwareTier.Mid,
            3 => HardwareTier.High,
            _ => HardwareTier.Enthusiast,
        };

        // 4. Baseline FPS — from hardware.
        int baselineFps = hw switch
        {
            HardwareTier.Potato     => 30 + (int)(rng.NextUlong() % 31),    // 30–60
            HardwareTier.Low        => 60 + (int)(rng.NextUlong() % 41),    // 60–100
            HardwareTier.Mid        => 100 + (int)(rng.NextUlong() % 61),   // 100–160
            HardwareTier.High       => 160 + (int)(rng.NextUlong() % 81),   // 160–240
            HardwareTier.Enthusiast => 240 + (int)(rng.NextUlong() % 161),  // 240–400
            _ => 100,
        };

        // 5. Network — pick connection-type bucket biased by archetype,
        //    then resolve to a specific ConnectionType, then construct.
        int bucket = PickWeighted(ap.NetworkBucketWeights, rng.NextUlong());
        ConnectionType ct = PickConnectionTypeFromBucket(bucket, hw, rng.NextUlong());
        var network = NetworkProfile.GenerateForType(seed, ct);

        // 6. Skill — within archetype range.
        int skill    = RangeRoll(ap.SkillMin, ap.SkillMax, rng.NextUlong());
        int aim      = RangeRoll(ap.AimMin, ap.AimMax, rng.NextUlong());
        int movement = ClampSkill(skill + (int)(rng.NextUlong() % 15) - 7);
        int gs       = ClampSkill(skill + (int)(rng.NextUlong() % 11) - 5);

        // 7. Reaction — correlated with aim (high aim → fast reaction).
        //    Top aim ≈ 180ms, bottom aim ≈ 380ms, with ±20ms noise.
        int reaction = 380 - (int)(aim * 2.0) + (int)((rng.NextUlong() % 41) - 20);
        reaction = Math.Clamp(reaction, 160, 420);

        // 8. Psychology — within archetype range.
        int aggression  = RangeRoll(ap.AggressionMin,  ap.AggressionMax,  rng.NextUlong());
        int toxicity    = RangeRoll(ap.ToxicityMin,    ap.ToxicityMax,    rng.NextUlong());
        int chattiness  = RangeRoll(ap.ChattinessMin,  ap.ChattinessMax,  rng.NextUlong());
        int tiltProne   = RangeRoll(ap.TiltProneMin,   ap.TiltProneMax,   rng.NextUlong());
        int patience    = RangeRoll(ap.PatienceMin,    ap.PatienceMax,    rng.NextUlong());
        int teamPlayer  = RangeRoll(ap.TeamPlayerMin,  ap.TeamPlayerMax,  rng.NextUlong());

        // 9. Account age.
        int accAge = RangeRoll(ap.AccountAgeMinDays, ap.AccountAgeMaxDays, rng.NextUlong());

        // 10. Time zone — biased by region.
        int tz = TimeZoneForRegion(region, rng.NextUlong());

        // 11. Complacency style — flavor of "не играет", set from archetype.
        //     Showoff = no-scopes / ножевые выходы / гранаты-ради-красоты.
        //     Lazy    = сидит дальше / отказывается от инициативы.
        //     Passive = странные углы, дисэнгейдж от боя.
        ComplacencyStyle compStyle = arch switch
        {
            BotArchetype.SchoolRusher
                or BotArchetype.SilverKamikaze
                or BotArchetype.EgoCarry
                or BotArchetype.Smurf       => ComplacencyStyle.Showoff,
            BotArchetype.AwpCamper
                or BotArchetype.BoomerOnM4  => ComplacencyStyle.Passive,
            BotArchetype.Silent
                or BotArchetype.OldPC
                or BotArchetype.TeamPlayer
                or BotArchetype.Tilter      => ComplacencyStyle.Lazy,
            _                                => ComplacencyStyle.Lazy,
        };

        return new BotProfile
        {
            Seed                     = seed,
            SteamID                  = seed,
            Archetype                = arch,
            Region                   = region,
            Language                 = lang,
            AccountAgeDays           = accAge,
            LocalTimeZoneOffsetHours = tz,
            Hardware                 = hw,
            BaselineFPS              = baselineFps,
            Network                  = network,
            SkillRating              = skill,
            AimSkillBase             = aim,
            MovementSkill            = movement,
            GameSense                = gs,
            ReactionBaseMs           = reaction,
            AggressionBase           = aggression,
            ToxicityBase             = toxicity,
            ChattinessBase           = chattiness,
            TiltProneness            = tiltProne,
            Patience                 = patience,
            TeamPlayerBias           = teamPlayer,
            ComplacencyStyle         = compStyle,
        };
    }

    private static BotArchetype PickArchetype(ulong roll)
    {
        int total = 0;
        foreach (var p in _archetypes.Values) total += p.Weight;
        int r = (int)(roll % (ulong)total);
        int acc = 0;
        foreach (var (k, v) in _archetypes)
        {
            acc += v.Weight;
            if (r < acc) return k;
        }
        return BotArchetype.Silent; // unreachable
    }

    private static int PickWeighted(int[] weights, ulong roll)
    {
        int total = 0;
        foreach (var w in weights) total += w;
        if (total == 0) return 0;
        int r = (int)(roll % (ulong)total);
        int acc = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            acc += weights[i];
            if (r < acc) return i;
        }
        return weights.Length - 1;
    }

    private static int RangeRoll(int lo, int hi, ulong roll)
    {
        if (hi <= lo) return lo;
        return lo + (int)(roll % (ulong)(hi - lo + 1));
    }

    /// <summary>
    /// ConnectionType bucket → specific type. Buckets are coarse
    /// archetype-level groupings (Cable / Wifi / Mobile / Far / Chaos /
    /// Vpn); within each bucket the specific type is randomized with a
    /// secondary nudge from hardware tier (potato wifi → WifiBad bias).
    /// </summary>
    private static ConnectionType PickConnectionTypeFromBucket(int bucket, HardwareTier hw, ulong roll)
    {
        int r = (int)(roll % 100UL);
        return bucket switch
        {
            0 /* Cable  */ => hw >= HardwareTier.High
                              ? (r < 60 ? ConnectionType.CableStable : ConnectionType.CableNormal)
                              : (r < 35 ? ConnectionType.CableStable : ConnectionType.CableNormal),
            1 /* Wifi   */ => hw == HardwareTier.Potato
                              ? (r < 30 ? ConnectionType.WifiBad : r < 70 ? ConnectionType.WifiMid : ConnectionType.WifiGood)
                              : (r < 40 ? ConnectionType.WifiGood : r < 75 ? ConnectionType.WifiMid : ConnectionType.WifiBad),
            2 /* Mobile */ => r < 55 ? ConnectionType.Mobile5G : ConnectionType.Mobile4G,
            3 /* Far    */ => r < 70 ? ConnectionType.RegionFar : ConnectionType.RegionVeryFar,
            4 /* Chaos  */ => ConnectionType.SchoolNet,
            5 /* Vpn    */ => ConnectionType.Vpn,
            _              => ConnectionType.CableNormal,
        };
    }

    private static BotLanguage PickLanguageForRegion(Region r, ulong roll)
    {
        int p = (int)(roll % 100UL);
        return r switch
        {
            Region.RuCis  => p < 80 ? BotLanguage.Russian : BotLanguage.English,
            Region.EuWest => p < 35 ? BotLanguage.German  : p < 65 ? BotLanguage.French : p < 80 ? BotLanguage.English : BotLanguage.Other,
            Region.EuEast => p < 70 ? BotLanguage.Russian : BotLanguage.English,
            Region.NorthAm=> BotLanguage.English,
            Region.SouthAm=> p < 70 ? BotLanguage.Spanish : BotLanguage.English,
            Region.Asia   => p < 60 ? BotLanguage.English : BotLanguage.Other,
            _             => BotLanguage.English,
        };
    }

    private static int TimeZoneForRegion(Region r, ulong roll)
    {
        int p = (int)(roll % 100UL);
        return r switch
        {
            Region.EuWest => p < 50 ? 1 : 2,
            Region.EuEast => p < 50 ? 2 : 3,
            Region.RuCis  => 3 + (int)(roll % 8UL),  // MSK..VLAT
            Region.NorthAm=> p < 60 ? -5 : -8,
            Region.SouthAm=> -3,
            Region.Asia   => 8 + (int)(roll % 4UL),
            _             => 0,
        };
    }

    /// <summary>
    /// Multi-line dump for `insanity_profile <slot>` rcon. Includes
    /// dynamic state so admins can see why a bot is behaving the way
    /// it is (high tilt? frustrated mood? low aim from session length?).
    /// </summary>
    public string DebugDump()
    {
        var wakeupLabel = Complacency >= 50f
            ? $"wakeup={(0.60 - Complacency / 400.0) * 100:F0}%baseline"
            : "wakeup=n/a";
        return
            $"  archetype:    {Archetype} (complacencyStyle={ComplacencyStyle})\n" +
            $"  identity:     region={Region} lang={Language} accAge={AccountAgeDays}d tz=UTC{LocalTimeZoneOffsetHours:+#;-#;0}\n" +
            $"  hardware:     tier={Hardware} fps={BaselineFPS}\n" +
            $"  network:      {Network}\n" +
            $"  skill:        rating={SkillRating} aim={AimSkillBase} mov={MovementSkill} sense={GameSense} react={ReactionBaseMs}ms\n" +
            $"  psychology:   aggr={AggressionBase} tox={ToxicityBase} chat={ChattinessBase} tiltProne={TiltProneness} patience={Patience} teamPlay={TeamPlayerBias}\n" +
            $"  dynamic:      mood={Mood} tilt={Tilt} comp={Complacency:F1} (highStreak={RoundsAtHighComp}, {wakeupLabel}) W{WinStreak}/L{LossStreak} rounds={RoundsPlayed} K{Kills}/D{Deaths}\n" +
            $"  computed:     curAim={CurrentAimSkill} curReact={CurrentReactionMs}ms curSense={CurrentGameSense} curChat={CurrentChattiness} curTox={CurrentToxicity}";
    }

    /// <summary>Tiny RNG used during static generation + complacency
    /// updates (state-coupled, no GC).</summary>
    private struct MiniRng
    {
        private ulong _s;
        public MiniRng(ulong seed) { _s = seed == 0 ? 0xABADCAFEDEADBEEFUL : seed; }
        public ulong NextUlong()
        {
            unchecked
            {
                ulong x = _s;
                x ^= x >> 12; x ^= x << 25; x ^= x >> 27;
                _s = x;
                return x * 0x2545F4914F6CDD1DUL;
            }
        }
        public double NextDouble() => (NextUlong() >> 11) * (1.0 / (1UL << 53));
        /// <summary>Returns int in [lo, hi) — half-open, matching System.Random.</summary>
        public int NextRange(int lo, int hi)
        {
            if (hi <= lo) return lo;
            return lo + (int)(NextUlong() % (ulong)(hi - lo));
        }
    }
}
