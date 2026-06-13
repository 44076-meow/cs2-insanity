#ifndef REPLICA_HIDER_PLUGIN_H
#define REPLICA_HIDER_PLUGIN_H

#include <ISmmPlugin.h>
#include <playerslot.h>

#include "pool.h"

// Pulled in for the StartChangeLevel hook return type. SourceHook requires
// the exact member-fn-ptr type (compiler rejects cross-cast to void*).
#include <tier1/utlvector.h>

class CServerSideClient;
class INetworkGameClient;

class ReplicaHiderPlugin : public ISmmPlugin, public IMetamodListener {
public:
    bool Load(PluginId id, ISmmAPI* ismm, char* error, size_t maxlen, bool late) override;
    bool Unload(char* error, size_t maxlen) override;

    // Post-hook: writes m_bFakePlayer=0 into the live CServerSideClient
    // for managed slots, so userinfo broadcast tells clients fakeplayer=0
    // and the panorama scoreboard renders ping instead of the BOT icon.
    void Hook_OnClientConnected_Post(CPlayerSlot slot, const char* pszName, uint64 xuid,
                                     const char* pszNetworkID, const char* pszAddress,
                                     bool bFakePlayer);

    // Post-hook for ClientPutInServer. Fires after OCC. Used to catch
    // late-adopted bots: those whose CSSharp Spawn() did NOT pre-mark
    // the pool (engine-spawned). CSSharp pool.Write happens between OCC
    // and CPiS via OnClientConnected listener; this hook then sees pool
    // [slot]==1 and flips byte 160 just like the OCC path.
    void Hook_ClientPutInServer_Post(CPlayerSlot slot, char const* pszName, int type, uint64 xuid);

    // PRE-hook on IVEngineServer2::CreateFakeClient. Fires before engine
    // allocates the CServerSideClient and starts the connect chain. Pops a
    // persona from the pool FIFO (filled by CSSharp Spawn() before bot_add)
    // or falls back to a built-in roster, then SUPERSEDEs the call with
    // override netname so userinfo broadcast carries our name natively.
    CPlayerSlot Hook_CreateFakeClient_Pre(const char* netname);
    void Hook_GameFrame(bool simulating, bool bFirstTick, bool bLastTick);
    void OnLevelInit(char const* pMapName, char const*, char const*, char const*, bool, bool) override;

    // Mapchange survival hook chain:
    //  - INetworkGameServer::StartChangeLevel PRE (this hook, parameterized
    //    variant): EARLIEST point in the changelevel sequence — engine logs
    //    "CNetworkServerService::StartChangeLevel( (no landmark) )" and
    //    starts client tear-down here. PRE-hook fires before disconnect
    //    cascade. Sets shm flag so CSSharp's OnClientDisconnect skips
    //    Despawn for synthetic disconnects.
    //  - INetworkServerService::StartChangeLevel(void) — the void variant
    //    fires LATER (after disconnects, before LevelShutdown). Empirically
    //    too late on its own. Not hooked.
    //  - IMetamodListener::OnLevelShutdown (kept, redundant): fires too late
    //    (AFTER LoopDeactivate cascade — verified empirically 2026-05-02).
    //    Kept as belt-and-braces in case another mapchange path bypasses
    //    StartChangeLevel.
    //  - CSSharp OnMapStart: clears the flag after snapshot (see
    //    FakeClientManager.OnMapStart).
    CUtlVector<INetworkGameClient*>* Hook_StartChangeLevel_Pre(
        const char* mapName, const char* landmark, void* changelevelState);
    void OnLevelShutdown() override;

    const char* GetAuthor()      override { return "frad70"; }
    const char* GetName()        override { return "ReplicaHider"; }
    const char* GetDescription() override { return "Selective fake-client hider via direct m_bFakePlayer field write"; }
    const char* GetURL()         override { return ""; }
    const char* GetLicense()     override { return "MIT"; }
    const char* GetVersion()     override { return "0.5.0"; }
    const char* GetDate()        override { return __DATE__; }
    const char* GetLogTag()      override { return "REPLICAHIDER"; }

    ReplicaHider::Pool* GetPool() { return &m_Pool; }

    // CUtlString::Set(const char*) signature, resolved from libtier0.so
    // via dlsym at Load(). Used to overwrite engine-side m_szClientName /
    // m_Name on CServerSideClient. nullptr if dlsym failed — name overwrite
    // disabled, byte-160 path still works.
    using CUtlStringSetFn = void (*)(void* /*CUtlString this*/, const char*);
    CUtlStringSetFn m_pUtlStringSet = nullptr;

    // Latched OFF state, set when a layout-drift defense trips (e.g.,
    // the candidate CUtlString field at kNameOffset doesn't look like a
    // userspace pointer). Public so the OverwriteEngineName free
    // function in plugin.cpp can latch it without becoming a member.
    bool m_bSelfDisabled = false;  // latched on pool header corruption or layout drift

private:
    ReplicaHider::Pool m_Pool;
    void* m_pHookedGameServer = nullptr;  // diagnostic-only — last instance ptr; never dereferenced (UAF, issue #10)
    int   m_StartChangeLevelHookId = 0;   // SH_ADD_HOOK_MEMFUNC return; SH_REMOVE_HOOK_ID without touching stale instance
    void* m_pTier0 = nullptr;       // dlopen handle for libtier0.so
};

extern ReplicaHiderPlugin g_Plugin;

PLUGIN_GLOBALVARS();

#endif
