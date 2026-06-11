using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Replica;

// Stable identity for a fake-client persona, designed to survive across
// mapchange / server restart / hot-reload. Persisted as JSON via
// PersonaRegistry. Most fields nullable — they get populated as later
// phases (P/05–P/09 + PersonaScheduler) come online; for v0.5.1-beta
// only Id, Name, CreatedAt, LastSeenAt, ActiveOnSlot are written.
//
// JSON serialization uses System.Text.Json — public mutable properties
// + parameterless ctor for deserialize. All timestamps are ISO-8601 UTC
// strings ("o" format) so the file is timezone-agnostic across server
// migrations.
public sealed class Persona
{
    // ── MVP fields (P/03 — written in v0.5.1-beta) ─────────────────

    /// <summary>Stable monotonic id. Max+1 on insert, never reused.</summary>
    public int Id { get; set; }

    /// <summary>Current display name (mutable across rounds for theatre).</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Stable SteamID64 for this persona. Cross-session identity from a
    /// network perspective — ProcessUserCmds detour stamps it, schema
    /// overwrite uses it, NetworkProfile.Generate seeds from it.
    /// 0 = uninitialized / pre-P-01 persona; AdoptController synthesizes
    /// one via SteamIds.Generate(slot) and persists into the persona.
    /// </summary>
    public ulong SteamId64 { get; set; }

    /// <summary>UTC ISO-8601 timestamp first inserted into registry.</summary>
    public string CreatedAt { get; set; } = "";

    /// <summary>UTC ISO-8601 timestamp last bound to a player slot.</summary>
    public string LastSeenAt { get; set; } = "";

    /// <summary>
    /// Player slot this persona is currently bound to, or null if dormant.
    /// Volatile — cleared at OnMapStart, re-bound on adopt. NEVER trust
    /// for cross-mapchange identity; use Id for that. Reset to null on
    /// Load — disk value of "currently bound" is meaningless after a
    /// non-clean shutdown.
    /// </summary>
    public int? ActiveOnSlot { get; set; }

    // ── PersonaScheduler future fields (P/03 follow-up) ────────────
    // Added to schema now so JSON file doesn't migrate when scheduler
    // ships. PersonaScheduler vision: simulate public-server churn —
    // personas with active hours, session length distributions,
    // preferred maps. dormant until the scheduler reads them.

    /// <summary>Opaque schedule profile id (TBD when scheduler ships).</summary>
    public string? ScheduleProfile { get; set; }

    /// <summary>UTC ISO-8601 — when the last continuous session started.</summary>
    public string? LastSessionStart { get; set; }

    /// <summary>UTC ISO-8601 — when the last continuous session ended.</summary>
    public string? LastSessionEnd { get; set; }

    /// <summary>Lifetime sessions count for "regular vs occasional" model.</summary>
    public long? TotalSessionsCount { get; set; }

    /// <summary>Lifetime cumulative playtime in minutes.</summary>
    public long? TotalPlaytimeMinutes { get; set; }

    // ── Future-phase fields — nullable, dormant until phase activates

    /// <summary>P/08 — voice & chat archetype.</summary>
    /// <remarks>Values: "silent", "strategic", "casual", "toxic", "memer".</remarks>
    public string? Archetype { get; set; }

    /// <summary>P/06 — aim skill tier 0..10. Higher = better.</summary>
    public int? SkillTier { get; set; }

    /// <summary>P/08 — primary language for chat / voice cadence.</summary>
    /// <remarks>ISO-639-1: "en", "ru", "pt", "tr", etc.</remarks>
    public string? NativeLang { get; set; }

    /// <summary>P/07 — per-map mastery, 0.0..1.0. Maps not present = unknown.</summary>
    public Dictionary<string, double>? MapMastery { get; set; }

    /// <summary>P/09 — buy preference: "full", "eco", "force", "anti_eco_save".</summary>
    public string? BuyPreference { get; set; }

    /// <summary>P/05 — opaque seed/id into MovementProfile registry.</summary>
    public string? MovementProfileId { get; set; }

    /// <summary>P/06 — opaque seed/id into AimProfile registry.</summary>
    public string? AimProfileId { get; set; }

    /// <summary>P/04 — synthetic Steam profile snapshot (avatar, level, hours).</summary>
    /// <remarks>Schema TBD when P/04 is unblocked w/ legal review.</remarks>
    public Dictionary<string, object?>? SteamProfile { get; set; }

    // ── Helpers ────────────────────────────────────────────────────

    [JsonIgnore]
    public bool IsActive => ActiveOnSlot != null;

    public Persona() { }   // for deserialize

    public Persona(int id, string name, string nowIso)
    {
        Id = id;
        Name = name;
        CreatedAt = nowIso;
        LastSeenAt = nowIso;
        ActiveOnSlot = null;
    }
}
