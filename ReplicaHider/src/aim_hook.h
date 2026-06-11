// =============================================================================
// aim_hook.h — minimal inline detour for libserver.so:CCSBot::UpdateLookAngles.
//
// What it does:
//   * Pattern-scans libserver.so executable region for the 33-byte signature
//     of CCSBot::UpdateLookAngles (extracted 2026-05-08, see aim_hook.cpp).
//   * mprotect's the function's page R/W/X.
//   * Replaces the first 8 bytes (1-byte `push rbp` + 7-byte `lea rax`) with:
//         E9 <rel32 to aim_hook_naked_entry>
//         90 90 90  (NOPs)
//   * Allocates a trampoline page within ±2GB of the target containing:
//         55                              (push rbp — same as original)
//         48 8D 05 <adjusted_disp32>      (lea rax, [rip+...] with new disp
//                                          so the LEA still resolves to the
//                                          same VPROF group string in mem)
//         E9 <rel32 to target+8>          (jmp back to original body)
//
// Why we need it:
//   The plugin-side CSSharp hook (MemoryFunctionVoid signature-based) silently
//   failed for this function — its byte-patch never landed (verified via
//   /proc/<pid>/mem dump). UpdateLookAngles is non-virtual, so SourceHook
//   vtable hooks don't apply either. CSSharp's hookmangen for Linux x64 is
//   incomplete in upstream metamod-source (static_assert failure at compile),
//   leaving us with no off-the-shelf hooking option.
//
//   The function is the per-tick aim driver: it reads m_lookPitch (CCSBot+0x594C)
//   and m_lookYaw (CCSBot+0x5954), smoothes them into internal state used by
//   the bot AI's shoot trace. PRE-overriding those two floats is the only way
//   to redirect bot aim from outside the engine BT.
//
// API:
//   AimHook::Install()  — pattern scan, mprotect, write JMP, build trampoline.
//                          Returns true on success; false logs via META_CONPRINTF.
//                          Idempotent.
//   AimHook::Uninstall() — restore original bytes, unmap trampoline. Idempotent.
// =============================================================================

#pragma once

#include <stdint.h>

namespace ReplicaHider {

class AimHook {
public:
    AimHook() = default;

    bool Install();
    void Uninstall();
    bool IsInstalled() const { return m_target != nullptr; }

    // Last installed target / trampoline addresses for diagnostics.
    void* TargetAddr() const     { return m_target; }
    void* TrampolineAddr() const { return m_trampoline; }
    uint64_t HookFireCount() const;  // implemented in .cpp; pulls from atomic counter

private:
    unsigned char* m_target     = nullptr;
    unsigned char* m_trampoline = nullptr;
    unsigned char  m_origBytes[8] = {0};
};

extern AimHook g_AimHook;

}  // namespace ReplicaHider
