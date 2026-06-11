// =============================================================================
// AimHook.cs — controller for the C++ inline detour on CCSBot::UpdateLookAngles.
//
// REWORK 2026-05-08: prior version tried to install a CSSharp signature-based
// Funchook directly. Verified empirically (via /proc/<pid>/mem dump) that the
// patch never landed — CSSharp's Hook() reported success but the function's
// first bytes stayed unmodified. CCSharp Funchook silently fails on this
// function's prologue, possibly because it can't safely relocate the
// RIP-relative LEAs.
//
// New approach: the actual detour lives in ReplicaHider (C++ side) — see
// ReplicaHider/src/aim_hook.cpp. CSSharp's job is now to WRITE the override
// values into the shared mmap pool; the C++ PRE-hook reads them on each
// CCSBot::UpdateLookAngles fire and writes to m_lookPitch (CCSBot+0x594C)
// and m_lookYaw (CCSBot+0x5954).
//
// API (preserved for backward compat):
//   SetGlobalOverride(pitch?, yaw?)  — null = clear, otherwise (pitch,yaw)
//                                       written to pool, all bots affected.
// =============================================================================

using System;

namespace Replica;

/// <summary>
/// Controller-side state for the C++ AimHook. Real detour is in
/// ReplicaHider/src/aim_hook.cpp; this class only writes override values
/// into the shared mmap pool that the C++ side reads.
/// </summary>
public static class AimHook
{
    public static float OverridePitch { get; private set; } = float.NaN;
    public static float OverrideYaw   { get; private set; } = float.NaN;
    public static bool  OverrideEnabled => !float.IsNaN(OverridePitch) && !float.IsNaN(OverrideYaw);

    /// <summary>
    /// Set or clear the global aim override. Both null = clear; otherwise
    /// the pair is written to pool offsets the C++ AimHook PRE-detour reads
    /// on each CCSBot::UpdateLookAngles invocation. Affects every managed
    /// CCSBot uniformly — per-slot is a future extension.
    /// </summary>
    public static void SetGlobalOverride(PoolMmap pool, float? pitch, float? yaw)
    {
        if (pool == null || !pool.IsOpen) return;

        if (pitch.HasValue && yaw.HasValue)
        {
            OverridePitch = pitch.Value;
            OverrideYaw   = yaw.Value;
            pool.WriteAimOverride(true, pitch.Value, yaw.Value);
            Log.Info($"AimHook override SET pool: pitch={pitch.Value:F1} yaw={yaw.Value:F1} (C++ side reads on each UpdateLookAngles fire)");
        }
        else
        {
            OverridePitch = float.NaN;
            OverrideYaw   = float.NaN;
            pool.ClearAimOverride();
            Log.Info("AimHook override CLEARED");
        }
    }

    public static string DebugStatus(PoolMmap pool)
    {
        if (pool == null || !pool.IsOpen) return "pool not open";
        var (en, p, y) = pool.ReadAimOverride();
        return $"enabled={en} pitch={p:F1} yaw={y:F1} (real detour lives in ReplicaHider C++ — see meta logs for fire counts)";
    }
}
