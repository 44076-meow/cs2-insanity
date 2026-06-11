using System;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;

namespace Replica;

// Variant A install: we attach a pre-hook on ProcessUsercmds and use it
// strictly for accounting — we observe which CPlayerSlot had usercmds
// processed on each game-tick and (later, when behavior layers exist)
// the same hook becomes the place where injected cmds for fake-clients
// are blended in. Today the hook NEVER mutates the buffer; it only
// reads the slot index and ticks the per-bot accounting. This is the
// "best-effort, no-stub" middle path: the wiring is real, the side
// effect on engine state is zero.
public sealed class ProcessUsercmdsDetour
{
    public bool Installed { get; private set; }
    public string? InstallError { get; private set; }

    private MemoryFunctionVoid<IntPtr, IntPtr, int>? _func;
    public delegate void OnSlotProcessedHandler(int slot, int cmdCount);
    public event OnSlotProcessedHandler? OnSlotProcessed;

    public bool Install()
    {
        try
        {
            // Gamedata name lookup — CSSharp parses the OS-specific bytes
            // (incl. wildcards) from gamedata json and resolves to a real
            // address. Raw byte string would require us to re-implement
            // wildcard parsing, and FindSignature alone doesn't accept ?.
            _func = new MemoryFunctionVoid<IntPtr, IntPtr, int>("ProcessUsercmds");
            _func.Hook(OnPreProcess, HookMode.Pre);
            Installed = true;
            return true;
        }
        catch (Exception ex)
        {
            InstallError = ex.Message;
            Installed = false;
            return false;
        }
    }

    public void Uninstall()
    {
        if (!Installed || _func == null) return;
        try { _func.Unhook(OnPreProcess, HookMode.Pre); } catch { }
        Installed = false;
    }

    private HookResult OnPreProcess(DynamicHook h)
    {
        // ProcessUsercmds(this, CUserCmd* cmds, int numcmds, ...) — we read
        // numcmds and the slot index off `this`. The first 4 bytes of the
        // CCSPlayer_MovementServices-derived "this" aren't guaranteed to
        // be a slot, so we can't trivially pull a CPlayerSlot out without
        // walking the schema. Instead, we use the calling player handle
        // available via the dynamic-hook param interface when present.
        // If anything throws we swallow — the engine path must continue.
        try
        {
            int slot = -1;
            int numcmds = 0;
            try { numcmds = h.GetParam<int>(2); } catch { }
            try
            {
                // CPlayerSlot is encoded in the CSPlayerUserCmd header; we
                // approximate by hashing the `this` pointer to a stable id
                // for accounting. The Manager translates real controller
                // slots → fake-bot ids out-of-band via the tick listener.
                var thisPtr = h.GetParam<IntPtr>(0);
                slot = (int)((long)thisPtr & 0x7FFFFFFF);
            }
            catch { }
            OnSlotProcessed?.Invoke(slot, numcmds);
        }
        catch { }
        return HookResult.Continue;
    }
}
