// See aim_hook.h for design overview.
#include "aim_hook.h"
#include "plugin.h"
#include "pool.h"
#include "pool_format.h"

#include <stdio.h>
#include <stdint.h>
#include <string.h>
#include <errno.h>
#include <unistd.h>
#include <sys/mman.h>

#include <atomic>

namespace InsanityHider {

AimHook g_AimHook;

// Sigscan pattern — same as gamedata/insanity.json:CCSBot_UpdateLookAngles.
// Function prologue from libserver.so 2026-05-08 (BuildID 60c3c87436...).
//
//   55                       push rbp
//   48 8d 05 ?? ?? ?? ??     lea  rax, [rip + VPROF_GROUP_NAME]    (RIP-rel)
//   48 8d 35 ?? ?? ?? ??     lea  rsi, [rip + VPROF_COUNTER]       (RIP-rel)
//   48 89 e5                 mov  rbp, rsp
//   41 55                    push r13
//   4c 8d 6d c0              lea  r13, [rbp - 0x40]
//   41 54                    push r12
//   53                       push rbx
//   4c 89 ea                 mov  rdx, r13
//   48 89 fb                 mov  rbx, rdi      ; rdi = this = CCSBot*
//
// Wildcards mark the two RIP-relative literal offsets that change every build.
struct PatternByte {
    uint16_t v;  // 0xFFFF = wildcard, else the literal byte
};
static const PatternByte kPattern[] = {
    {0x55}, {0x48}, {0x8D}, {0x05}, {0xFFFF}, {0xFFFF}, {0xFFFF}, {0xFFFF},
    {0x48}, {0x8D}, {0x35}, {0xFFFF}, {0xFFFF}, {0xFFFF}, {0xFFFF},
    {0x48}, {0x89}, {0xE5}, {0x41}, {0x55}, {0x4C}, {0x8D}, {0x6D}, {0xC0},
    {0x41}, {0x54}, {0x53}, {0x4C}, {0x89}, {0xEA}, {0x48}, {0x89}, {0xFB},
};
static constexpr size_t kPatternLen = sizeof(kPattern) / sizeof(kPattern[0]);

// Hook fire counter for diagnostics. Incremented from C handler (single-thread
// in practice — game tick on main thread — so atomic is paranoia).
static std::atomic<uint64_t> g_HookFires{0};

// Trampoline pointer set by Install(), read by aim_hook_naked_entry's
// `jmp *(g_aim_trampoline_ptr)` at hook return. Linkage = global so the
// .S file can reference it via RIP-relative load.
extern "C" {
    void* g_aim_trampoline_ptr = nullptr;
    void  aim_hook_naked_entry();   // implemented in aim_hook_asm.S
    void  aim_hook_handler(void* ccsbot);  // C-side, called from naked entry
}

// ─────────────────────────────────────────────────────────────────────────────
// libserver.so location lookup (parses /proc/self/maps).
// Returns the FULL executable address range — first r-xp start to last r-xp end.
// libserver.so has multiple r-xp segments separated by tiny rwxp stubs (PLT
// thunks etc.); we scan the union. The pattern (33 bytes, 25 strict) is
// restrictive enough that data sections won't false-match.
// ─────────────────────────────────────────────────────────────────────────────
struct LibInfo {
    uintptr_t exec_start = 0;
    uintptr_t exec_end   = 0;
};
static LibInfo FindLibServer() {
    LibInfo info;
    FILE* f = fopen("/proc/self/maps", "r");
    if (!f) return info;
    char line[2048];
    while (fgets(line, sizeof(line), f)) {
        if (!strstr(line, "libserver.so")) continue;
        if (!strstr(line, "/csgo/bin/")) continue;  // skip metamod stub
        uintptr_t start, end;
        char perms[16];
        if (sscanf(line, "%lx-%lx %15s", &start, &end, perms) != 3) continue;
        if (perms[0] == 'r' && perms[2] == 'x') {
            if (info.exec_start == 0 || start < info.exec_start) info.exec_start = start;
            if (end > info.exec_end) info.exec_end = end;
        }
    }
    fclose(f);
    return info;
}

// Linear pattern scan over [start, end). Returns first match, nullptr if none.
static unsigned char* PatternScan(uintptr_t start, uintptr_t end) {
    if (end <= start) return nullptr;
    auto* p = reinterpret_cast<const unsigned char*>(start);
    size_t n = end - start;
    if (n < kPatternLen) return nullptr;
    for (size_t i = 0; i + kPatternLen <= n; i++) {
        bool ok = true;
        for (size_t j = 0; j < kPatternLen; j++) {
            uint16_t pv = kPattern[j].v;
            if (pv == 0xFFFF) continue;
            if (p[i + j] != pv) { ok = false; break; }
        }
        if (ok) return const_cast<unsigned char*>(p + i);
    }
    return nullptr;
}

// Allocate a 4KB executable page within signed 32-bit reach of `near`.
// Strategy: scan upward and downward in 64KB steps, calling mmap with hint.
// Linux usually honors hints when the address is free.
static void* AllocateNearby(void* near, size_t size) {
    auto base = (uintptr_t)near & ~(uintptr_t)0xFFF;
    // Try downward first (kernel often gives us nearby pages this way).
    for (intptr_t off = -0x100000; off > -(intptr_t)0x70000000; off -= 0x100000) {
        void* hint = (void*)(base + off);
        void* p = mmap(hint, size,
                       PROT_READ | PROT_WRITE | PROT_EXEC,
                       MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
        if (p == MAP_FAILED) continue;
        intptr_t delta = (intptr_t)((uintptr_t)p - base);
        bool in_range = (delta >= -(intptr_t)0x7FFFF000)
                     && (delta <=  (intptr_t)0x7FFFF000);
        if (in_range) return p;
        munmap(p, size);
    }
    for (intptr_t off = 0x100000; off < (intptr_t)0x70000000; off += 0x100000) {
        void* hint = (void*)(base + off);
        void* p = mmap(hint, size,
                       PROT_READ | PROT_WRITE | PROT_EXEC,
                       MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
        if (p == MAP_FAILED) continue;
        intptr_t delta = (intptr_t)((uintptr_t)p - base);
        bool in_range = (delta >= -(intptr_t)0x7FFFF000)
                     && (delta <=  (intptr_t)0x7FFFF000);
        if (in_range) return p;
        munmap(p, size);
    }
    return nullptr;
}

// ─────────────────────────────────────────────────────────────────────────────
// Install / Uninstall
// ─────────────────────────────────────────────────────────────────────────────

bool AimHook::Install() {
    if (m_target) return true;

    auto info = FindLibServer();
    if (!info.exec_start) {
        META_CONPRINTF("[AimHook] error: libserver.so executable region not found in /proc/self/maps\n");
        return false;
    }
    META_CONPRINTF("[AimHook] libserver.so .text [0x%lx .. 0x%lx] (%.2f MB)\n",
                   info.exec_start, info.exec_end, (info.exec_end - info.exec_start) / 1048576.0);

    auto* target = PatternScan(info.exec_start, info.exec_end);
    if (!target) {
        META_CONPRINTF("[AimHook] error: pattern scan miss — sig stale (CS2 update?)\n");
        return false;
    }
    m_target = target;
    META_CONPRINTF("[AimHook] target found at %p\n", target);

    auto* tramp = (unsigned char*)AllocateNearby(target, 4096);
    if (!tramp) {
        META_CONPRINTF("[AimHook] error: failed to allocate trampoline within ±2GB of target\n");
        m_target = nullptr;
        return false;
    }
    m_trampoline = tramp;
    META_CONPRINTF("[AimHook] trampoline at %p (delta %+ld bytes from target)\n",
                   tramp, (long)((intptr_t)tramp - (intptr_t)target));

    // ── Build trampoline ────────────────────────────────────────────────────
    // [0..0]:  0x55 (push rbp) — position-independent
    // [1..7]:  48 8D 05 <new_disp32>  (lea rax, [rip+adjust])
    // [8..12]: E9 <rel32 to target+8>
    tramp[0] = 0x55;
    tramp[1] = 0x48;
    tramp[2] = 0x8D;
    tramp[3] = 0x05;

    int32_t orig_disp;
    memcpy(&orig_disp, target + 4, 4);
    intptr_t orig_target = (intptr_t)(target + 8) + orig_disp;
    intptr_t new_disp    = orig_target - (intptr_t)(tramp + 8);
    int32_t  new_disp32  = (int32_t)new_disp;
    if ((intptr_t)new_disp32 != new_disp) {
        META_CONPRINTF("[AimHook] error: trampoline LEA disp out of i32 range (%ld)\n", (long)new_disp);
        munmap(tramp, 4096);
        m_target = m_trampoline = nullptr;
        return false;
    }
    memcpy(tramp + 4, &new_disp32, 4);

    tramp[8] = 0xE9;
    intptr_t back_rel  = (intptr_t)(target + 8) - (intptr_t)(tramp + 13);
    int32_t  back_rel32 = (int32_t)back_rel;
    if ((intptr_t)back_rel32 != back_rel) {
        META_CONPRINTF("[AimHook] error: trampoline back-jmp out of i32 range (%ld)\n", (long)back_rel);
        munmap(tramp, 4096);
        m_target = m_trampoline = nullptr;
        return false;
    }
    memcpy(tramp + 9, &back_rel32, 4);

    // ── Patch target ────────────────────────────────────────────────────────
    auto page_size = (size_t)sysconf(_SC_PAGESIZE);
    auto page_addr = (uintptr_t)target & ~(page_size - 1);
    // Two pages in case the 8-byte patch spans a page boundary.
    if (mprotect((void*)page_addr, page_size * 2, PROT_READ | PROT_WRITE | PROT_EXEC) != 0) {
        META_CONPRINTF("[AimHook] error: mprotect target page R/W/X failed errno=%d\n", errno);
        munmap(tramp, 4096);
        m_target = m_trampoline = nullptr;
        return false;
    }

    memcpy(m_origBytes, target, 8);

    intptr_t entry_rel  = (intptr_t)((uintptr_t)&aim_hook_naked_entry) - (intptr_t)(target + 5);
    int32_t  entry_rel32 = (int32_t)entry_rel;
    if ((intptr_t)entry_rel32 != entry_rel) {
        META_CONPRINTF("[AimHook] error: hook entry too far from target (%ld)\n", (long)entry_rel);
        mprotect((void*)page_addr, page_size * 2, PROT_READ | PROT_EXEC);
        munmap(tramp, 4096);
        m_target = m_trampoline = nullptr;
        return false;
    }

    unsigned char patch[8] = { 0xE9, 0, 0, 0, 0, 0x90, 0x90, 0x90 };
    memcpy(patch + 1, &entry_rel32, 4);
    memcpy(target, patch, 8);

    // Restore page back to R/X. Trampoline stays R/W/X — short-lived,
    // tiny, and reverting it won't survive Uninstall anyway.
    mprotect((void*)page_addr, page_size * 2, PROT_READ | PROT_EXEC);

    g_aim_trampoline_ptr = tramp;
    META_CONPRINTF("[AimHook] installed: target=%p tramp=%p handler=%p\n",
                   target, tramp, (void*)&aim_hook_naked_entry);
    return true;
}

void AimHook::Uninstall() {
    if (!m_target) return;
    auto page_size = (size_t)sysconf(_SC_PAGESIZE);
    auto page_addr = (uintptr_t)m_target & ~(page_size - 1);
    if (mprotect((void*)page_addr, page_size * 2, PROT_READ | PROT_WRITE | PROT_EXEC) == 0) {
        memcpy(m_target, m_origBytes, 8);
        mprotect((void*)page_addr, page_size * 2, PROT_READ | PROT_EXEC);
    }
    if (m_trampoline) munmap(m_trampoline, 4096);
    g_aim_trampoline_ptr = nullptr;
    m_target = nullptr;
    m_trampoline = nullptr;
    META_CONPRINTF("[AimHook] uninstalled\n");
}

uint64_t AimHook::HookFireCount() const {
    return g_HookFires.load(std::memory_order_relaxed);
}

// ─────────────────────────────────────────────────────────────────────────────
// C handler — called by aim_hook_naked_entry (asm) once per UpdateLookAngles
// invocation, before the smoother runs. rdi is preserved across the call.
// ─────────────────────────────────────────────────────────────────────────────
extern "C" void aim_hook_handler(void* ccsbot) {
    g_HookFires.fetch_add(1, std::memory_order_relaxed);

    // Log first few fires + every 200th to keep diagnostics breadcrumbs
    // visible without flooding the console.
    uint64_t n = g_HookFires.load(std::memory_order_relaxed);
    if (n <= 4 || (n % 200) == 0) {
        META_CONPRINTF("[AimHook] fire#%lu this=%p\n", (unsigned long)n, ccsbot);
    }
    if (!ccsbot) return;

    auto* pool = g_Plugin.GetPool();
    if (!pool) return;

    auto* base = reinterpret_cast<unsigned char*>(ccsbot);
    void* pawn = nullptr;
    memcpy(&pawn, base + InsanityHider::CCSBOT_PLAYER_PTR_OFFSET, sizeof(pawn));

    // v7 feedback channel: capture BT's freshly-set m_lookPitch/Yaw BEFORE
    // we clobber them with our override.
    //
    // **DIAGNOSTIC-ONLY post Etap D (commit 172f86e) — see issue #46.**
    // C# AimController previously read these to know BT's intended target,
    // but since Etap D it picks its own target via front-cone scan and
    // never reads bt_target_* again. The write below is retained so the
    // pool v7 layout stays intact and a future hybrid mode can re-light
    // the channel without bumping to v8.
    //
    // Historic motivation (kept for context): solving the sample-write
    // phase lag — verified 2026-05-09 that the 8-tick cache pattern
    // stalled reaction enough that kennyS skill=95 ended up bottom of
    // the fragboard. v7 fixed lag; Etap D fixed the skill-ceiling.
    float btPitch = *reinterpret_cast<float*>(base + InsanityHider::CCSBOT_LOOK_PITCH_OFFSET);
    float btYaw   = *reinterpret_cast<float*>(base + InsanityHider::CCSBOT_LOOK_YAW_OFFSET);
    pool->WriteBotTargetForBot(reinterpret_cast<uint64_t>(ccsbot), btPitch, btYaw);

    // Per-slot first: if CSSharp registered an AimSlot keyed on this CCSBot,
    // use its (pitch, yaw). Else fall back to the global override.
    // Key is the CCSBot pointer (== `this` here, == pawn.Bot.Handle on C#),
    // NOT the CCSPlayerPawn pointer — empirically the value at CCSBot+0x8
    // doesn't match what CSSharp returns from pawn.Handle (different memory
    // region, different object identity), even though writing to it at
    // +m_angEyeAngles offset has visual effect.
    float pitch, yaw;
    bool haveOverride = false;
    bool fromPerSlot = false;
    haveOverride = pool->LookupPerSlotAim(reinterpret_cast<uint64_t>(ccsbot), pitch, yaw);
    fromPerSlot = haveOverride;
    if (!haveOverride) {
        if (!pool->IsAimOverrideEnabled()) return;
        pitch = pool->GetAimPitch();
        yaw   = pool->GetAimYaw();
    }
    // First-fire diagnostic: print when a per-slot override matches a pawn.
    // Goes silent after first hit per-process to avoid log flood.
    static std::atomic<bool> g_PerSlotLogged{false};
    if (fromPerSlot) {
        bool expected = false;
        if (g_PerSlotLogged.compare_exchange_strong(expected, true)) {
            META_CONPRINTF("[AimHook] perslot MATCH pawn=%p pitch=%.2f yaw=%.2f (further matches not logged)\n",
                           pawn, pitch, yaw);
        }
    }

    // (1) Write CCSBot.m_lookPitch / m_lookYaw. Required to lock — verified
    // 2026-05-09: writing only m_angEyeAngles let the smoother body of
    // UpdateLookAngles immediately undo our write (eye lerp'd back toward
    // BT's m_lookPitch within smoother alpha each tick → user reported
    // "ZywOo не смотрит в пол" when we manually locked at pitch=70).
    //
    // The feedback-loop concern (C# AimController reads m_lookPitch and
    // sees our own write) is solved on the C# side by a SAMPLE / WRITE
    // phase pattern — AimController disarms its AimSlot every N ticks
    // for one tick, letting BT write m_lookPitch cleanly, samples it on
    // the next tick, then re-arms with target + noise for the next N-1
    // ticks. So m_lookPitch is BT-owned during sample phase and ours
    // during write phase. Drift is bounded; visible aim error is full.
    *reinterpret_cast<float*>(base + InsanityHider::CCSBOT_LOOK_PITCH_OFFSET) = pitch;
    *reinterpret_cast<float*>(base + InsanityHider::CCSBOT_LOOK_YAW_OFFSET)   = yaw;

    // (2) Write CCSPlayerPawn.m_angEyeAngles. Per AimDiag (2026-05-08),
    // bullet trajectory matched this field within ~1° in 30/30 fires.
    // PRE-UpdateLookAngles runs BEFORE the per-tick shoot trace reads it,
    // so our write propagates to bullet direction.
    if (pawn) {
        auto* p = reinterpret_cast<unsigned char*>(pawn);
        // QAngle = 3 floats: pitch (X), yaw (Y), roll (Z=0).
        *reinterpret_cast<float*>(p + InsanityHider::PAWN_EYE_ANGLES_OFFSET + 0) = pitch;
        *reinterpret_cast<float*>(p + InsanityHider::PAWN_EYE_ANGLES_OFFSET + 4) = yaw;
        // Roll left untouched — original value preserved (humans don't roll).
    }
}

}  // namespace InsanityHider
