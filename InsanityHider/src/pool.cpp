#include "pool.h"

#include <fcntl.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>
#include <errno.h>
#include <string.h>
#include <stdio.h>
#include <atomic>

namespace InsanityHider {

bool Pool::Open(const char* path) {
    Close();

    // Writable since v3 — C++ side marks managed slots from CFC PRE / OCC.
    m_iFd = ::open(path, O_RDWR);
    if (m_iFd < 0) {
        snprintf(m_szError, sizeof(m_szError),
                 "open(%s) failed: errno=%d %s", path, errno, strerror(errno));
        return false;
    }

    struct stat st{};
    if (fstat(m_iFd, &st) < 0) {
        snprintf(m_szError, sizeof(m_szError),
                 "fstat failed: errno=%d %s", errno, strerror(errno));
        Close();
        return false;
    }
    if (st.st_size < (off_t)POOL_TOTAL) {
        snprintf(m_szError, sizeof(m_szError),
                 "pool too small: %lld < %zu", (long long)st.st_size, POOL_TOTAL);
        Close();
        return false;
    }

    void* base = mmap(nullptr, POOL_TOTAL, PROT_READ | PROT_WRITE, MAP_SHARED, m_iFd, 0);
    if (base == MAP_FAILED) {
        snprintf(m_szError, sizeof(m_szError),
                 "mmap failed: errno=%d %s", errno, strerror(errno));
        Close();
        return false;
    }

    auto* u32 = reinterpret_cast<const uint32_t*>(base);
    uint32_t magic = u32[0], version = u32[1];
    if (magic != POOL_MAGIC) {
        snprintf(m_szError, sizeof(m_szError),
                 "magic mismatch: got 0x%08x want 0x%08x", magic, POOL_MAGIC);
        munmap(base, POOL_TOTAL);
        Close();
        return false;
    }
    if (version != POOL_VERSION) {
        snprintf(m_szError, sizeof(m_szError),
                 "version mismatch: got %u want %u (delete pool file before v4 deploy)",
                 version, POOL_VERSION);
        munmap(base, POOL_TOTAL);
        Close();
        return false;
    }

    m_pBase = base;
    m_nMapped = POOL_TOTAL;
    m_szError[0] = '\0';
    return true;
}

void Pool::Close() {
    if (m_pBase) {
        munmap(m_pBase, m_nMapped);
        m_pBase = nullptr;
        m_nMapped = 0;
    }
    if (m_iFd >= 0) {
        ::close(m_iFd);
        m_iFd = -1;
    }
}

bool Pool::IsManaged(int slot) const {
    if (!m_pBase) return false;
    if (slot < 0 || slot >= (int)POOL_SLOTS) return false;
    auto* slots = reinterpret_cast<const uint8_t*>(m_pBase) + POOL_MANAGED_OFFSET;
    return slots[slot] != 0;
}

bool Pool::IsActive() const {
    if (!m_pBase) return false;
    auto* p = reinterpret_cast<const uint32_t*>(
        reinterpret_cast<const uint8_t*>(m_pBase) + POOL_ACTIVE_OFFSET);
    return *p != 0;
}

bool Pool::IsMapchangeInProgress() const {
    if (!m_pBase) return false;
    auto* p = reinterpret_cast<const std::atomic<uint32_t>*>(
        reinterpret_cast<const uint8_t*>(m_pBase) + POOL_MAPCHANGE_OFFSET);
    return p->load(std::memory_order_acquire) != 0;
}

bool Pool::RevalidateHeader() const {
    if (!m_pBase) return false;
    auto* u32 = reinterpret_cast<const uint32_t*>(m_pBase);
    return u32[0] == POOL_MAGIC && u32[1] == POOL_VERSION;
}

const char* Pool::GetName(int slot) const {
    if (!m_pBase) return nullptr;
    if (slot < 0 || slot >= (int)POOL_SLOTS) return nullptr;
    auto* p = reinterpret_cast<const char*>(m_pBase) + POOL_NAMES_OFFSET + slot * POOL_NAME_BYTES;
    return p[0] ? p : nullptr;
}

void Pool::WriteManaged(int slot, uint8_t val) {
    if (!m_pBase) return;
    if (slot < 0 || slot >= (int)POOL_SLOTS) return;
    auto* slots = reinterpret_cast<uint8_t*>(m_pBase) + POOL_MANAGED_OFFSET;
    slots[slot] = val;
}

void Pool::WriteName(int slot, const char* name) {
    if (!m_pBase) return;
    if (slot < 0 || slot >= (int)POOL_SLOTS) return;
    auto* dst = reinterpret_cast<char*>(m_pBase) + POOL_NAMES_OFFSET + slot * POOL_NAME_BYTES;
    if (!name || !name[0]) {
        memset(dst, 0, POOL_NAME_BYTES);
        return;
    }
    size_t n = strnlen(name, POOL_NAME_BYTES - 1);
    memcpy(dst, name, n);
    dst[n] = '\0';
    if (n + 1 < POOL_NAME_BYTES) memset(dst + n + 1, 0, POOL_NAME_BYTES - n - 1);
}

void Pool::WriteMapchangeFlag(bool inProgress) {
    if (!m_pBase) return;
    auto* p = reinterpret_cast<std::atomic<uint32_t>*>(
        reinterpret_cast<uint8_t*>(m_pBase) + POOL_MAPCHANGE_OFFSET);
    p->store(inProgress ? 1u : 0u, std::memory_order_release);
}

bool Pool::IsAimOverrideEnabled() const {
    if (!m_pBase) return false;
    auto* p = reinterpret_cast<const uint32_t*>(
        reinterpret_cast<const uint8_t*>(m_pBase) + POOL_AIM_OVERRIDE_EN_OFFSET);
    return *p != 0;
}

float Pool::GetAimPitch() const {
    if (!m_pBase) return 0.0f;
    float v = 0.0f;
    memcpy(&v, reinterpret_cast<const uint8_t*>(m_pBase) + POOL_AIM_OVERRIDE_PITCH_OFFSET, sizeof(v));
    return v;
}

float Pool::GetAimYaw() const {
    if (!m_pBase) return 0.0f;
    float v = 0.0f;
    memcpy(&v, reinterpret_cast<const uint8_t*>(m_pBase) + POOL_AIM_OVERRIDE_YAW_OFFSET, sizeof(v));
    return v;
}

bool Pool::LookupPerSlotAim(uint64_t botPtr, float& outPitch, float& outYaw) const {
    if (!m_pBase || botPtr == 0) return false;
    auto* base = reinterpret_cast<const uint8_t*>(m_pBase) + POOL_AIM_SLOTS_OFFSET;
    for (size_t i = 0; i < POOL_AIM_SLOT_COUNT; i++) {
        auto* entry = base + i * POOL_AIM_SLOT_BYTES;
        uint64_t k;
        memcpy(&k, entry + POOL_AIM_SLOT_BOT_OFFSET, sizeof(k));
        if (k != botPtr) continue;
        uint32_t en;
        memcpy(&en, entry + POOL_AIM_SLOT_ENABLED_OFFSET, sizeof(en));
        if (en == 0) return false;
        memcpy(&outPitch, entry + POOL_AIM_SLOT_PITCH_OFFSET, sizeof(outPitch));
        memcpy(&outYaw,   entry + POOL_AIM_SLOT_YAW_OFFSET,   sizeof(outYaw));
        return true;
    }
    return false;
}

// DIAGNOSTIC-ONLY post Etap D (commit 172f86e) — see issue #46. C# no
// longer reads bt_target_* (AimController owns target selection). Kept
// to preserve pool v7 layout for a possible future hybrid-mode revival.
void Pool::WriteBotTargetForBot(uint64_t botPtr, float btPitch, float btYaw) {
    if (!m_pBase || botPtr == 0) return;
    auto* base = reinterpret_cast<uint8_t*>(m_pBase) + POOL_AIM_SLOTS_OFFSET;
    for (size_t i = 0; i < POOL_AIM_SLOT_COUNT; i++) {
        auto* entry = base + i * POOL_AIM_SLOT_BYTES;
        uint64_t k;
        memcpy(&k, entry + POOL_AIM_SLOT_BOT_OFFSET, sizeof(k));
        if (k != botPtr) continue;
        memcpy(entry + POOL_AIM_SLOT_BT_PITCH_OFFSET, &btPitch, sizeof(btPitch));
        memcpy(entry + POOL_AIM_SLOT_BT_YAW_OFFSET,   &btYaw,   sizeof(btYaw));
        return;
    }
}

bool Pool::PopFifo(char* outBuf, size_t outBufBytes) {
    if (!m_pBase || !outBuf || outBufBytes == 0) return false;
    auto* base = reinterpret_cast<uint8_t*>(m_pBase);
    auto* phead = reinterpret_cast<std::atomic<uint32_t>*>(base + POOL_FIFO_HEAD_OFFSET);
    auto* ptail = reinterpret_cast<std::atomic<uint32_t>*>(base + POOL_FIFO_TAIL_OFFSET);
    uint32_t head = phead->load(std::memory_order_acquire);
    uint32_t tail = ptail->load(std::memory_order_relaxed);
    if (head == tail) return false;  // empty
    const char* slot = reinterpret_cast<const char*>(
        base + POOL_FIFO_OFFSET + (tail % POOL_FIFO_CAPACITY) * POOL_NAME_BYTES);
    size_t n = strnlen(slot, POOL_NAME_BYTES);
    if (n == 0) {
        // Empty slot — treat as no entry, advance tail to skip.
        ptail->store(tail + 1, std::memory_order_release);
        return false;
    }
    if (n >= outBufBytes) n = outBufBytes - 1;
    memcpy(outBuf, slot, n);
    outBuf[n] = '\0';
    ptail->store(tail + 1, std::memory_order_release);
    return true;
}

} // namespace InsanityHider
