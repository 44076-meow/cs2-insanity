// Shared layout for /tmp/replica_fake_slots.bin.
//
// Layout v6 (was v5; appended AimSlot[64] block for per-slot aim override):
//   [   0..   3]  uint32 magic          = 'INSF' = 0x46534E49
//   [   4..   7]  uint32 version        = 6
//   [   8..  11]  uint32 activeFlag     = kill-switch (0/1) — hider on/off
//   [  12..  15]  uint32 mapchangeFlag  = mapchange in progress (0/1)
//   [  16.. 135]  uint8 managed[120]    = per-slot management bit
//   [ 136..3975]  char names[120][32]   = per-slot persona name (null-terminated)
//   [3976..4487]  char fifoBuf[16][32]  = SPSC ring buffer of pending personas
//   [4488..4491]  uint32 fifoHead       = CSSharp writes
//   [4492..4495]  uint32 fifoTail       = C++ writes
//   [4496..4499]  uint32 aimOverrideEn  = global aim-override enabled (0/1)
//   [4500..4503]  float  aimOverridePitch
//   [4504..4507]  float  aimOverrideYaw
//   [4508..4511]  uint32 aimSlotCount   = 64 (compile-time constant — fewer
//                                          rounds-up to mmap page anyway)
//   [4512..4515]  uint32 reserved       = 0 (alignment to 8B for the array)
//   [4516..6051]  AimSlot[64]           = 24 bytes per entry, see below
// Total = 6052 bytes.
//
// AimSlot layout (32 bytes, 8-byte aligned). v7 widened from v6 to add
// the bt_target_* feedback channel: C++ writes BT's freshly-set
// m_lookPitch/Yaw each tick (before our override stomp), C# reads it
// to know BT's intended target without stale-read pollution. Solves
// the sample/write phase lag — C# can react every tick to fresh BT
// data instead of caching a target across N ticks.
//   [+0..+7]   uint64 bot_key          CCSBot* pointer (the AI struct,
//                                       == `this` inside CCSBot::Update-
//                                       LookAngles). NOT the CCSPlayer-
//                                       Pawn pointer — empirically the
//                                       value at CCSBot+0x8 doesn't match
//                                       what CSSharp returns from pawn.
//                                       Handle, so we key on ccsbot which
//                                       both sides agree on. C# obtains
//                                       it via pawn.Bot.Handle. 0 =
//                                       empty entry.
//   [+8..+11]  uint32 enabled          0: no override, 1: use override
//   [+12..+15] float  pitch            override pitch (C# writes)
//   [+16..+19] float  yaw              override yaw (C# writes)
//   [+20..+23] float  bt_target_pitch  fresh BT m_lookPitch (C++ writes)
//   [+24..+27] float  bt_target_yaw    fresh BT m_lookYaw   (C++ writes)
//   [+28..+31] uint32 reserved         0 / alignment
//
// AIM OVERRIDE block (v5, 2026-05-08; per-slot extension v6, 2026-05-09):
//   GLOBAL toggle that the C++ AimHook reads inside CCSBot::UpdateLookAngles
//   PRE-detour. When enabled, every CCSBot's m_lookPitch/m_lookYaw is forced
//   to (aimOverridePitch, aimOverrideYaw) before the engine smoother runs.
//
//   v6 adds per-slot AimSlot[64]. Handler resolution order:
//     (1) Read pawn ptr from CCSBot+0x8.
//     (2) Linear scan AimSlot[0..63]. If pawn_key matches AND enabled=1,
//         use that slot's (pitch, yaw).
//     (3) Else if global aimOverrideEn=1, use global (pitch, yaw).
//     (4) Else no override, fall through to engine smoother.
//
//   Linear scan over 64 entries is one cache-line of pawn_keys plus a few
//   more — hundreds of cycles per fire, run on the game tick thread which
//   is already inside CCSBot::UpdateLookAngles. Negligible.
//
//   CSSharp WRITES; C++ READS. No race: CSSharp updates these fields from a
//   single rcon command handler on the game tick thread, the C++ side reads
//   them on the same thread (UpdateLookAngles runs in CCSBot::Upkeep which
//   runs from the per-tick AI subsystem on the same main thread).
//
// FIFO is single-producer / single-consumer. Counters increment monotonically;
// slot index = counter % capacity. Empty when head == tail. Overflow when
// head - tail >= capacity (don't push more than capacity-1 outstanding).
//
// v4 → v5 is a NON-BREAKING extension: existing fields unchanged, new fields
// appended after FIFO tail. v4 readers see truncated file = old layout. v5
// pool files starting from a fresh /tmp file (auto-recreated by both sides).

#pragma once

#include <stdint.h>
#include <stddef.h>

namespace ReplicaHider {

constexpr uint32_t POOL_MAGIC   = 0x46534E49u;
constexpr uint32_t POOL_VERSION = 7u;
constexpr size_t   POOL_SLOTS   = 120;
constexpr size_t   POOL_AIM_SLOT_COUNT = 64;
constexpr size_t   POOL_AIM_SLOT_BYTES = 32;

constexpr size_t POOL_HEADER_BYTES     = 16;
constexpr size_t POOL_ACTIVE_OFFSET    = 8;
constexpr size_t POOL_MAPCHANGE_OFFSET = 12;
constexpr size_t POOL_MANAGED_OFFSET   = POOL_HEADER_BYTES;            // 16
constexpr size_t POOL_NAMES_OFFSET     = POOL_MANAGED_OFFSET + POOL_SLOTS;  // 136
constexpr size_t POOL_NAME_BYTES       = 32;

constexpr size_t POOL_FIFO_CAPACITY    = 16;
constexpr size_t POOL_FIFO_OFFSET      = POOL_NAMES_OFFSET + (POOL_SLOTS * POOL_NAME_BYTES); // 3976
constexpr size_t POOL_FIFO_BYTES       = POOL_FIFO_CAPACITY * POOL_NAME_BYTES;               // 512
constexpr size_t POOL_FIFO_HEAD_OFFSET = POOL_FIFO_OFFSET + POOL_FIFO_BYTES;                 // 4488
constexpr size_t POOL_FIFO_TAIL_OFFSET = POOL_FIFO_HEAD_OFFSET + 4;                          // 4492

// v5 aim-override block (global single pair).
constexpr size_t POOL_AIM_OVERRIDE_EN_OFFSET    = POOL_FIFO_TAIL_OFFSET + 4;  // 4496
constexpr size_t POOL_AIM_OVERRIDE_PITCH_OFFSET = POOL_AIM_OVERRIDE_EN_OFFSET + 4;  // 4500
constexpr size_t POOL_AIM_OVERRIDE_YAW_OFFSET   = POOL_AIM_OVERRIDE_PITCH_OFFSET + 4;  // 4504

// v6 per-slot aim-override array. Handler scans for matching pawn_key.
constexpr size_t POOL_AIM_SLOT_COUNT_OFFSET = POOL_AIM_OVERRIDE_YAW_OFFSET + 4;     // 4508
constexpr size_t POOL_AIM_SLOTS_OFFSET      = POOL_AIM_SLOT_COUNT_OFFSET + 8;       // 4516 (skip 8B for count + reserved alignment pad)

// AimSlot field offsets relative to the start of an entry.
// (BOT_KEY rather than PAWN — see layout doc above for rationale.)
constexpr size_t POOL_AIM_SLOT_BOT_OFFSET        = 0;   // uint64
constexpr size_t POOL_AIM_SLOT_ENABLED_OFFSET    = 8;   // uint32
constexpr size_t POOL_AIM_SLOT_PITCH_OFFSET      = 12;  // float — C# writes
constexpr size_t POOL_AIM_SLOT_YAW_OFFSET        = 16;  // float — C# writes
constexpr size_t POOL_AIM_SLOT_BT_PITCH_OFFSET   = 20;  // float — C++ writes BT's m_lookPitch
constexpr size_t POOL_AIM_SLOT_BT_YAW_OFFSET     = 24;  // float — C++ writes BT's m_lookYaw
// 28..31 = reserved/alignment

constexpr size_t POOL_TOTAL = POOL_AIM_SLOTS_OFFSET + (POOL_AIM_SLOT_COUNT * POOL_AIM_SLOT_BYTES);  // 6052

// CCSBot field offsets (libserver.so 2026-05-08, BuildID 60c3c87436...).
// Used by aim_hook.cpp to write override values into the bot AI struct.
// Discovered via disassembly of CCSBot::UpdateLookAngles @ 0xb41c40 — see
// aim_hook.cpp's pattern + signature comment for full provenance.
constexpr size_t CCSBOT_LOOK_PITCH_OFFSET = 0x594C;  // float
constexpr size_t CCSBOT_LOOK_YAW_OFFSET   = 0x5954;  // float
// CCSBot.m_pPlayer — pointer to the bot's CCSPlayerPawn (from disassembly:
// `mov 0x8(%rbx), %rax` in UpdateLookAngles loads the player ptr).
constexpr size_t CCSBOT_PLAYER_PTR_OFFSET = 0x8;     // CCSPlayerPawn*
// CCSPlayerPawn.m_angEyeAngles — the schema field that AimDiag (2026-05-08)
// proved IS the actual shoot-direction source: bullet trajectory matched
// this field within ~1° across 30 logged fires. Plugin-side writes from
// Listeners.OnTick STUCK server-side but happened AFTER the per-tick shoot
// trace had already read the value, so they had no effect on bullets.
// PRE-UpdateLookAngles is earlier in the tick → write here propagates to
// the shoot trace.
constexpr size_t PAWN_EYE_ANGLES_OFFSET   = 0x1658;  // QAngle (3 floats)

} // namespace ReplicaHider
