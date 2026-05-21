// mmap'd shared pool, writable on the C++ side as of v3. CSSharp owns the
// schema (sets active flag on boot, clears slots on Despawn, pushes pending
// personas to the FIFO). C++ also writes managed/name when CFC PRE override
// fires — at OCC time the slot is known and we mark it ourselves so CSSharp
// doesn't have to predict it.
//
// v4 (mapchange survival): C++ writes mapchangeFlag at OnLevelShutdown;
// CSSharp reads it at OnClientDisconnect to skip Despawn during the engine's
// synthetic disconnect cascade, then clears at OnMapStart after snapshotting.

#pragma once

#include "pool_format.h"

namespace InsanityHider {

class Pool {
public:
    bool Open(const char* path);
    void Close();
    bool IsOpen() const { return m_pBase != nullptr; }
    bool IsManaged(int slot) const;
    bool IsActive() const;
    bool IsMapchangeInProgress() const;
    bool RevalidateHeader() const;
    const char* GetName(int slot) const;

    // v5 aim-override block. CSSharp WRITES; C++ READS in the AimHook
    // PRE-detour. v5 is a single global pair (pitch/yaw applies to every
    // bot). v6 added per-slot — see LookupPerSlotAim below.
    bool  IsAimOverrideEnabled() const;
    float GetAimPitch() const;
    float GetAimYaw() const;

    // v6 per-slot aim override. Linear scan over POOL_AIM_SLOT_COUNT
    // entries looking for AimSlot.bot_key == botPtr AND enabled=1.
    // On match, writes pitch/yaw and returns true. Otherwise returns
    // false and leaves outPitch/outYaw untouched. Key is the CCSBot
    // pointer (== `this` in UpdateLookAngles); see pool_format.h note.
    bool LookupPerSlotAim(uint64_t botPtr, float& outPitch, float& outYaw) const;

    // v7 — write BT's freshly-set m_lookPitch/Yaw into the AimSlot's
    // bt_target fields. Originally added (Etap C+) so the C# AimController
    // could read BT's intended target next tick without stale-read
    // pollution from our override. DIAGNOSTIC-ONLY since Etap D
    // (commit 172f86e — AimController gained its own target picker and
    // stopped consuming this channel). Writer is retained so the channel
    // can be revived without bumping the pool to v8 if a hybrid mode is
    // ever needed; see pool_format.h + issue #46. Linear scan to find
    // matching entry; no-op if no entry is registered for this CCSBot.
    void WriteBotTargetForBot(uint64_t botPtr, float btPitch, float btYaw);

    // Writes (writable mmap, v3+).
    void WriteManaged(int slot, uint8_t val);
    void WriteName(int slot, const char* name);
    void WriteMapchangeFlag(bool inProgress);
    // Pop one pending persona from the FIFO. Copies into outBuf (always
    // null-terminates). Returns true if a name was popped, false if FIFO empty.
    bool PopFifo(char* outBuf, size_t outBufBytes);

    const char* LastError() const { return m_szError; }

private:
    void*    m_pBase    = nullptr;
    size_t   m_nMapped  = 0;
    int      m_iFd      = -1;
    char     m_szError[128] = {0};
};

} // namespace InsanityHider
