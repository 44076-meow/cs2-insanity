// ReplicaHider — Metamod:Source plugin that hides the BOT icon for
// fake-clients managed by the Replica (CSSharp) plugin.
//
// Mechanism: when a managed bot completes its OnClientConnected
// post-hook, we write 0 into m_bFakePlayer (offset 160) on its
// CServerSideClient instance. The userinfo string-table broadcast that
// follows reports fakeplayer=0, and the client's panorama scoreboard
// renders ping instead of the BOT icon.
//
// Single-source-of-truth design: server.cfg sets `bot_quota 0`, so the
// engine never auto-fills bots — every CServerSideClient that exists
// was issued by FakeClientManager (CSSharp) via `bot_add`, and every
// such bot fires OnClientConnected. No race, no sweep needed.
//
// Pool layout shared with CSSharp: see src/pool_format.h.

#include "plugin.h"
#include "aim_hook.h"

#include <stdio.h>
#include <stdint.h>
#include <unistd.h>
#include <string.h>
#include <dlfcn.h>

#include <atomic>
#include <chrono>
#include <set>
#include <string>

#include <iserver.h>
#include <eiface.h>
#include <tier1/utlvector.h>
#include <tier1/convar.h>

#if !defined(__linux__)
#error "Linux x64 only — CS2 dedicated has no Windows server build."
#endif

// CNetworkGameServerBase->m_Clients lives at byte offset 592 on this
// CS2 build. CS2Fixes' utils.cpp uses
// (CUtlVector<CServerSideClient*>*)(&GetIGameServer()[74]) — pointer-
// array indexing on INetworkGameServer*, stride sizeof(class)=8, so
// 74 × 8 = 592. Validated empirically.
constexpr int kClientListOffset = 74 * 8;

// CServerSideClient::m_bFakePlayer offset on this CS2 build. Reference
// header (CS2Fixes serversideclient.h) put it at 176; live binary has
// it at 160 — layout shifted -16 bytes. Diffed engine bots (0x01) vs
// human (0x00) to confirm.
constexpr int kFakePlayerOffset = 160;

// CServerSideClient::m_Name offset (CUtlString = 8-byte char* m_pString).
// Reference header had it at 80; -16 shift yields 64. Validated probe
// (offset=64 → 'Mangos' for live bot 'Mangos'). Treat as immutable for
// this CS2 build; if engine layout changes, re-probe.
constexpr int kNameOffset = 64;

SH_DECL_HOOK6_void(IServerGameClients, OnClientConnected, SH_NOATTRIB, 0,
                   CPlayerSlot, const char*, uint64, const char*, const char*, bool);
SH_DECL_HOOK4_void(IServerGameClients, ClientPutInServer, SH_NOATTRIB, 0,
                   CPlayerSlot, char const*, int, uint64);
SH_DECL_HOOK1(IVEngineServer, CreateFakeClient, SH_NOATTRIB, 0, CPlayerSlot, const char*);
// Parameterized variant on INetworkGameServer (parent of CNetworkGameServerBase).
// Engine logs "CNetworkServerService::StartChangeLevel( (no landmark) )" at this
// call-site, then disconnects clients in the same code path. PRE-hooking here
// catches BEFORE the disconnect cascade.
class INetworkGameClient;  // forward decl matching iserver.h
SH_DECL_HOOK3(INetworkGameServer, StartChangeLevel, SH_NOATTRIB, 0,
              CUtlVector<INetworkGameClient*>*, const char*, const char*, void*);

// In-flight set of personas we issued via CFC PRE override but haven't yet
// seen at OCC. OCC handler matches incoming pszName here to decide whether
// the slot is one of ours and should be marked + byte-flipped. Same engine
// thread for both hooks — no synchronization needed.
static std::set<std::string> g_ExpectedNames;

// DEPRECATED — kept for reference / future PersonaScheduler use.
//
// In v0.5.1-beta this roster was the fallback for engine_quota /
// autoteambalance / manual `bot_add` cases where the CSSharp FIFO was
// empty. Empirical effect: ghost bots with curated names appeared on
// slots beyond explicit `replica_spawn_bots N` (e.g. warmup→match
// transition spawned 5-10 extras with these names).
//
// In v0.5.2-beta this fallback is REMOVED from the active code path:
// `Hook_CreateFakeClient_Pre` returns MRES_SUPERCEDE with invalid slot
// when the FIFO is empty after a double-check fence — engine sees
// "CreateFakeClient() returned null", logs "Unable to create bot",
// gives up. Net effect: only Spawn()-issued bots reach OCC, no ghosts.
//
// Roster left here in case PersonaScheduler (P/03 step 6) wants to
// inject names through a different path (e.g. allow engine auto-fill
// to come up only when scheduler "wants" a session). For now: unused.
static const char* k_FallbackRoster[] [[maybe_unused]] = {
    "kennyS",  "Magisk",  "ZywOo",   "s1mple",  "NiKo",     "device",
    "electronic","blameF","frozen",  "huNter",  "rain",     "Twistzz",
    "ropz",    "Brollan", "Aleksib", "broky",   "FaNg",     "donk",
    "sh1ro",   "jks",     "Boombl4", "Ax1Le",   "Hobbit",   "n0rb3r7",
    "Spinx",   "torzsi",  "stavn",   "TeSeS",   "kyxsan",   "snappi",
    "k1to",    "sjuush",
};

ReplicaHiderPlugin g_Plugin;
PLUGIN_EXPOSE(ReplicaHiderPlugin, g_Plugin);

IVEngineServer*     engine      = nullptr;
ICvar*              icvar       = nullptr;
IServerGameClients* gameclients = nullptr;
extern INetworkServerService* g_pNetworkServerService;

static CServerSideClient* ResolveClientBySlot(int slot) {
    if (!g_pNetworkServerService) return nullptr;
    auto* server = g_pNetworkServerService->GetIGameServer();
    if (!server) return nullptr;
    auto* clientList = reinterpret_cast<CUtlVector<CServerSideClient*>*>(
        reinterpret_cast<unsigned char*>(server) + kClientListOffset);
    int count = clientList->Count();
    if (count < 0 || count > 256 || slot < 0 || slot >= count) return nullptr;
    return clientList->Element(slot);
}

// Overwrite engine-side m_Name (CUtlString) at offset 64. Calls the
// engine's CUtlString::Set so the heap allocation is managed correctly.
// No-op if dlsym failed at Load() (m_pUtlStringSet == nullptr). Returns
// the m_pString readback after the call (or nullptr if disabled / no-op),
// so the caller can verify the write took.
static const char* OverwriteEngineName(ReplicaHiderPlugin* plugin, void* pClient, const char* newName) {
    if (!plugin->m_pUtlStringSet || !pClient || !newName || !newName[0]) return nullptr;
    void* pUtlString = reinterpret_cast<unsigned char*>(pClient) + kNameOffset;
    plugin->m_pUtlStringSet(pUtlString, newName);
    return *reinterpret_cast<const char**>(pUtlString);  // CUtlString::m_pString at offset 0
}

void ReplicaHiderPlugin::Hook_OnClientConnected_Post(CPlayerSlot slot, const char* pszName, uint64,
                                                     const char*, const char*, bool bFakePlayer) {
    int idx = slot.Get();

    // Self-disable guard (c): if the pool's magic/version has shifted under
    // us (CSSharp recreated it with a different layout), latch off forever.
    // m_bSelfDisabled is a runtime latch, separate from the kill-switch.
    if (m_bSelfDisabled) RETURN_META(MRES_IGNORED);
    if (m_Pool.IsOpen() && !m_Pool.RevalidateHeader()) {
        META_CONPRINTF("[ReplicaHider] error: pool header revalidation failed — self-disabling\n");
        m_bSelfDisabled = true;
        RETURN_META(MRES_IGNORED);
    }

    if (!m_Pool.IsActive())                                   RETURN_META(MRES_IGNORED);
    if (!bFakePlayer)                                         RETURN_META(MRES_IGNORED);
    if (idx < 0 || idx >= (int)ReplicaHider::POOL_SLOTS)     RETURN_META(MRES_IGNORED);

    // Match incoming pszName against our expected_set populated by CFC PRE.
    // If hit, this slot is ours: mark managed + write pool name + flip byte.
    bool managed = false;
    if (pszName && pszName[0]) {
        auto it = g_ExpectedNames.find(pszName);
        if (it != g_ExpectedNames.end()) {
            managed = true;
            m_Pool.WriteManaged(idx, 1);
            m_Pool.WriteName(idx, pszName);
            g_ExpectedNames.erase(it);
        } else if (m_Pool.IsManaged(idx)) {
            // Fallback: pool was already marked (e.g. mapchange survival).
            managed = true;
        }
    }
    if (!managed) RETURN_META(MRES_IGNORED);

    auto* pClient = ResolveClientBySlot(idx);
    if (!pClient) {
        META_CONPRINTF("[ReplicaHider] error: ResolveClientBySlot null slot=%d\n", idx);
        RETURN_META(MRES_IGNORED);
    }

    auto* raw = reinterpret_cast<unsigned char*>(pClient);
    if (raw[kFakePlayerOffset] != 0x01) RETURN_META(MRES_IGNORED);  // idempotent
    raw[kFakePlayerOffset] = 0;

    // m_Name already carries persona (engine got it via CFC override), but
    // call Set anyway to keep CUtlString::m_pString consistent with what we
    // expect (CSSharp side reads it back).
    OverwriteEngineName(this, pClient, pszName);

    META_CONPRINTF("[ReplicaHider] mark+flip slot=%d persona='%s'\n", idx, pszName);
    RETURN_META(MRES_IGNORED);
}

// CPiS post-hook is now a redundant safety net. CFC PRE override + OCC mark
// already handle byte-flip and name overwrite for the normal path. CPiS is
// useful only for slots that survived a mapchange (pool already managed,
// engine recreated CServerSideClient, byte 160 reset to 0x01).
void ReplicaHiderPlugin::Hook_ClientPutInServer_Post(CPlayerSlot slot, char const* pszName,
                                                      int type, uint64) {
    if (m_bSelfDisabled) RETURN_META(MRES_IGNORED);
    if (!m_Pool.IsActive()) RETURN_META(MRES_IGNORED);
    if (type != 1) RETURN_META(MRES_IGNORED);
    int idx = slot.Get();
    if (idx < 0 || idx >= (int)ReplicaHider::POOL_SLOTS) RETURN_META(MRES_IGNORED);
    if (!m_Pool.IsManaged(idx)) RETURN_META(MRES_IGNORED);

    auto* pClient = ResolveClientBySlot(idx);
    if (!pClient) RETURN_META(MRES_IGNORED);
    auto* raw = reinterpret_cast<unsigned char*>(pClient);

    if (raw[kFakePlayerOffset] != 0x01) RETURN_META(MRES_IGNORED);  // already flipped
    raw[kFakePlayerOffset] = 0;
    const char* persona = m_Pool.GetName(idx);
    if (persona) OverwriteEngineName(this, pClient, persona);
    META_CONPRINTF("[ReplicaHider] CPiS safety-net slot=%d persona='%s'\n",
                   idx, persona ? persona : pszName);
    RETURN_META(MRES_IGNORED);
}

// PRE-hook on engine->CreateFakeClient. Engine has just decided to allocate
// a fake-client and proposes a netname (from bot_names.txt). We override it
// with our persona BEFORE engine continues, so userinfo broadcast carries
// our name natively. SET_META_RESULT_NEWPARAMS recalls the function chain
// with the new arg, then we return MRES_SUPERCEDE inside the macro.
CPlayerSlot ReplicaHiderPlugin::Hook_CreateFakeClient_Pre(const char* netname) {
    if (m_bSelfDisabled || !m_Pool.IsActive()) {
        RETURN_META_VALUE(MRES_IGNORED, CPlayerSlot(-1));
    }

    // FIFO pop attempt #1. PopFifo internally does an acquire-load on the
    // head index, so it sees any persona pushed before its load completes.
    char fifoBuf[ReplicaHider::POOL_NAME_BYTES];
    const char* persona = nullptr;
    if (m_Pool.PopFifo(fifoBuf, sizeof(fifoBuf)) && fifoBuf[0]) {
        persona = fifoBuf;
    } else {
        // FIFO empty on first read = no Spawn() preceded this CreateFakeClient.
        // Possible causes:
        //   (a) engine match-start auto-fill (warmup→match transition) — the
        //       primary issue P/03 step 4 is closing
        //   (b) engine_quota / autoteambalance / mp_bots_per_team churn
        //   (c) manual rcon `bot_add` (admin testing — no CSSharp Spawn())
        //   (d) RACE: CSSharp Spawn() pushed FIFO between our PopFifo failure
        //       and this branch — paranoia retry below catches it.
        //
        // For (a)–(c) we want SUPERCEDE: engine sees CreateFakeClient return
        // CPlayerSlot(-1), logs "Unable to create bot" and gives up. Net
        // effect: 0 ghost bots, only Spawn()-issued bots reach OCC.
        //
        // For (d): explicit acquire fence + retry. If the second pop succeeds,
        // a legitimate Spawn() arrived in the race window — use that persona,
        // do not silently drop. The fence is technically redundant (PopFifo's
        // own acquire-load already orders against any release-store on head),
        // but it documents the paranoia and costs nothing.
        std::atomic_thread_fence(std::memory_order_acquire);
        if (m_Pool.PopFifo(fifoBuf, sizeof(fifoBuf)) && fifoBuf[0]) {
            persona = fifoBuf;
        } else {
            // Confirmed empty after fence — engine auto-fill. Block creation.
            //
            // Rate-limited aggregate log (1 Hz). Warmup→match transition can
            // attempt 10+ creates in a single tick burst; un-aggregated logs
            // would spam the console and obscure other diagnostics.
            using namespace std::chrono;
            static int64_t s_lastFlushMs = 0;
            static uint32_t s_supercedeCount = 0;
            static std::string s_lastNetname;
            int64_t nowMs = duration_cast<milliseconds>(
                steady_clock::now().time_since_epoch()).count();
            ++s_supercedeCount;
            s_lastNetname = netname ? netname : "<null>";
            if (nowMs - s_lastFlushMs >= 1000) {
                META_CONPRINTF("[ReplicaHider] CFC PRE supercede x%u in last "
                               "%lldms (last netname='%s', FIFO empty — engine "
                               "auto-fill blocked)\n",
                               s_supercedeCount,
                               (long long)(nowMs - s_lastFlushMs),
                               s_lastNetname.c_str());
                s_supercedeCount = 0;
                s_lastFlushMs = nowMs;
            }
            RETURN_META_VALUE(MRES_SUPERCEDE, CPlayerSlot(-1));
        }
    }

    // FIFO had a persona (or race-recovered second attempt found one).
    g_ExpectedNames.insert(persona);
    META_CONPRINTF("[ReplicaHider] CFC PRE override '%s' -> '%s'\n",
                   netname ? netname : "<null>", persona);
    RETURN_META_VALUE_NEWPARAMS(MRES_HANDLED, CPlayerSlot(-1),
                                &IVEngineServer::CreateFakeClient, (persona));
}

void ReplicaHiderPlugin::OnLevelInit(char const* pMapName, char const*, char const*,
                                      char const*, bool, bool) {
    // After mapchange, CServerSideClient instances are recreated with
    // m_bFakePlayer=0x01. With bot_quota=0 (server.cfg), nothing
    // auto-spawns — FakeClientManager re-issues bot_add for each pool
    // slot, and OnClientConnected fires per-bot as expected. Nothing
    // to do here; just log for cross-reference with telemetry.
    //
    // We DON'T clear mapchange flag here — CSSharp OnMapStart owns the
    // clear, after snapshotting active personas. OnLevelInit fires
    // before CSSharp's StartupServer hook in the engine-Metamod-CSSharp
    // chain, so clearing here would let synthetic OnClientDisconnects
    // through.

    // StartChangeLevel hook re-registration. GetIGameServer() returns null
    // at plugin Load(). Engine may also re-create the game server instance
    // across mapchanges (verified empirically — hook registered at boot
    // didn't fire on the next user-issued changelevel). On every
    // OnLevelInit, check the current pointer; if it changed, remove from
    // the old instance and add to the new one.
    {
        auto* gameServer = g_pNetworkServerService ? g_pNetworkServerService->GetIGameServer() : nullptr;
        if (gameServer && gameServer != m_pHookedGameServer) {
            if (m_pHookedGameServer) {
                SH_REMOVE_HOOK_MEMFUNC(INetworkGameServer, StartChangeLevel,
                                       (INetworkGameServer*)m_pHookedGameServer, this,
                                       &ReplicaHiderPlugin::Hook_StartChangeLevel_Pre, false);
                META_CONPRINTF("[ReplicaHider] StartChangeLevel hook removed from stale instance %p\n",
                               m_pHookedGameServer);
            }
            SH_ADD_HOOK_MEMFUNC(INetworkGameServer, StartChangeLevel, gameServer,
                                this, &ReplicaHiderPlugin::Hook_StartChangeLevel_Pre, false /* PRE */);
            m_pHookedGameServer = (void*)gameServer;
            META_CONPRINTF("[ReplicaHider] StartChangeLevel hook registered on game server %p\n",
                           (void*)gameServer);
        } else if (!gameServer) {
            META_CONPRINTF("[ReplicaHider] warning: GetIGameServer() null at OnLevelInit — retry next map\n");
        }
    }

    META_CONPRINTF("[ReplicaHider] OnLevelInit map=%s\n", pMapName ? pMapName : "?");
}

CUtlVector<INetworkGameClient*>* ReplicaHiderPlugin::Hook_StartChangeLevel_Pre(
    const char* mapName, const char* landmark, void* /*changelevelState*/)
{
    // PRIMARY mapchange hook. INetworkGameServer::StartChangeLevel(name,
    // landmark, *) is called by the engine at the start of the changelevel
    // sequence — BEFORE clients are deactivated. PRE-hook fires before the
    // body, so the shm flag is latched before any synthetic OnClientDisconnect
    // cascade reaches CSSharp.
    //
    // First-run regression (v0.5.1, 2026-05-02) confirmed earlier hook on
    // the void variant of INetworkServerService::StartChangeLevel fired
    // AFTER disconnects. The parameterized variant on INetworkGameServer
    // is the actual entry point.
    if (m_bSelfDisabled || !m_Pool.IsOpen()) {
        META_CONPRINTF("[ReplicaHider] StartChangeLevel PRE — pool unavailable, flag NOT set\n");
        RETURN_META_VALUE(MRES_IGNORED, nullptr);
    }
    m_Pool.WriteMapchangeFlag(true);
    g_ExpectedNames.clear();
    META_CONPRINTF("[ReplicaHider] StartChangeLevel PRE — mapchange flag latched (map='%s' landmark='%s')\n",
                   mapName ? mapName : "?", landmark ? landmark : "");
    RETURN_META_VALUE(MRES_IGNORED, nullptr);
}

void ReplicaHiderPlugin::OnLevelShutdown() {
    // SECONDARY (belt-and-braces). Fires AFTER the LoopDeactivate cascade
    // — verified empirically: server.log line 874 vs 858 for disconnect.
    // By then CSSharp has already wiped its bot state, so this is too
    // late to gate the disconnect cascade. We still latch the flag in
    // case some future mapchange path bypasses StartChangeLevel and goes
    // directly through LevelShutdown — costs nothing.
    if (m_bSelfDisabled || !m_Pool.IsOpen()) {
        META_CONPRINTF("[ReplicaHider] OnLevelShutdown — pool unavailable\n");
        return;
    }
    m_Pool.WriteMapchangeFlag(true);
    g_ExpectedNames.clear();
    META_CONPRINTF("[ReplicaHider] OnLevelShutdown — mapchange flag latched (secondary)\n");
}

bool ReplicaHiderPlugin::Load(PluginId id, ISmmAPI* ismm, char* error, size_t maxlen, bool late) {
    PLUGIN_SAVEVARS();

    GET_V_IFACE_CURRENT(GetEngineFactory, engine, IVEngineServer, INTERFACEVERSION_VENGINESERVER);
    GET_V_IFACE_CURRENT(GetEngineFactory, icvar,  ICvar,          CVAR_INTERFACE_VERSION);
    GET_V_IFACE_ANY    (GetServerFactory, gameclients, IServerGameClients, INTERFACEVERSION_SERVERGAMECLIENTS);
    GET_V_IFACE_ANY    (GetEngineFactory, g_pNetworkServerService, INetworkServerService,
                        NETWORKSERVERSERVICE_INTERFACE_VERSION);

    g_pCVar = icvar;
    g_SMAPI->AddListener(this, this);

    if (!m_Pool.Open("/tmp/replica_fake_slots.bin")) {
        META_CONPRINTF("[ReplicaHider] error: pool open (%s) — all clients treated as engine\n",
                       m_Pool.LastError());
    } else {
        META_CONPRINTF("[ReplicaHider] pool mapped (%zu slots)\n", ReplicaHider::POOL_SLOTS);
    }

    // Resolve CUtlString::Set from libtier0 for engine-side name overwrite.
    // RTLD_NOLOAD first — engine has already loaded libtier0; we just want
    // a handle for dlsym. Fallback: regular dlopen if NOLOAD returns null.
    m_pTier0 = dlopen("libtier0.so", RTLD_NOW | RTLD_NOLOAD);
    if (!m_pTier0) m_pTier0 = dlopen("libtier0.so", RTLD_NOW);
    if (m_pTier0) {
        m_pUtlStringSet = reinterpret_cast<CUtlStringSetFn>(
            dlsym(m_pTier0, "_ZN10CUtlString3SetEPKc"));
    }
    if (!m_pUtlStringSet) {
        META_CONPRINTF("[ReplicaHider] warning: CUtlString::Set unresolved (%s) — name overwrite disabled, byte 160 only\n",
                       dlerror() ? dlerror() : "no tier0 handle");
    } else {
        META_CONPRINTF("[ReplicaHider] CUtlString::Set resolved at %p — name overwrite enabled\n",
                       (void*)m_pUtlStringSet);
    }

    SH_ADD_HOOK(IServerGameClients, OnClientConnected, gameclients,
                SH_MEMBER(this, &ReplicaHiderPlugin::Hook_OnClientConnected_Post), true);
    SH_ADD_HOOK(IServerGameClients, ClientPutInServer, gameclients,
                SH_MEMBER(this, &ReplicaHiderPlugin::Hook_ClientPutInServer_Post), true);
    SH_ADD_HOOK(IVEngineServer, CreateFakeClient, engine,
                SH_MEMBER(this, &ReplicaHiderPlugin::Hook_CreateFakeClient_Pre), false /* PRE */);
    // StartChangeLevel hook registration is DEFERRED to first OnLevelInit —
    // GetIGameServer() returns null at plugin Load() time (game server not
    // yet created). After the engine creates it via StartupServer, OnLevelInit
    // fires and we register there (gated by m_bChangeLevelHookRegistered).

    META_CONPRINTF("[ReplicaHider] loaded — m_bFakePlayer offset=%d, kill-switch via pool[%zu]\n",
                   kFakePlayerOffset, ReplicaHider::POOL_ACTIVE_OFFSET);

    // AimHook — minimal inline detour on libserver.so:CCSBot::UpdateLookAngles.
    // Install() pattern-scans the libserver .text segment, mprotects the
    // function page R/W/X, writes a 5-byte JMP to our naked entry stub, and
    // builds a nearby trampoline. Failures are logged but non-fatal — the
    // hider half of the plugin runs fine without aim hooking. See aim_hook.h
    // for the full design rationale.
    if (!ReplicaHider::g_AimHook.Install()) {
        META_CONPRINTF("[ReplicaHider] AimHook install failed — aim override disabled "
                       "(hider unaffected)\n");
    }

    return true;
}

bool ReplicaHiderPlugin::Unload(char* error, size_t maxlen) {
    ReplicaHider::g_AimHook.Uninstall();
    SH_REMOVE_HOOK(IServerGameClients, OnClientConnected, gameclients,
                   SH_MEMBER(this, &ReplicaHiderPlugin::Hook_OnClientConnected_Post), true);
    SH_REMOVE_HOOK(IServerGameClients, ClientPutInServer, gameclients,
                   SH_MEMBER(this, &ReplicaHiderPlugin::Hook_ClientPutInServer_Post), true);
    SH_REMOVE_HOOK(IVEngineServer, CreateFakeClient, engine,
                   SH_MEMBER(this, &ReplicaHiderPlugin::Hook_CreateFakeClient_Pre), false);
    if (m_pHookedGameServer) {
        SH_REMOVE_HOOK_MEMFUNC(INetworkGameServer, StartChangeLevel,
                               (INetworkGameServer*)m_pHookedGameServer, this,
                               &ReplicaHiderPlugin::Hook_StartChangeLevel_Pre, false);
        m_pHookedGameServer = nullptr;
    }
    m_Pool.Close();
    if (m_pTier0) { dlclose(m_pTier0); m_pTier0 = nullptr; m_pUtlStringSet = nullptr; }
    return true;
}
