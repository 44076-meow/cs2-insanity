using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;

namespace Replica;

// Shared-memory pool mirror of C++ Pool (src/pool_format.h in ReplicaHider).
// Layout v4 (4496 bytes total, little-endian):
//   [   0..   3] magic = 0x46534E49 ('INSF')
//   [   4..   7] version = 4
//   [   8..  11] activeFlag (uint32: 0 = hider disabled, 1 = enabled)
//   [  12..  15] mapchangeFlag (uint32: 0 = idle, 1 = mapchange in progress)
//   [  16.. 135] managed[120] (uint8: 0 = unmanaged, 1 = our fake-client)
//   [ 136..3975] names[120][32] (null-terminated UTF-8, max 31 chars + NUL)
//   [3976..4487] fifoBuf[16][32]
//   [4488..4491] fifoHead
//   [4492..4495] fifoTail
//
// CSSharp side OWNS the pool — opens, validates/initializes, and writes
// per-slot management byte AND persona name. C++ side (ReplicaHider)
// reads only. Single-uint8 management writes are atomic; name writes are
// 32-byte memcpy — torn reads are possible across the boundary, so name
// readers should treat trailing junk past the NUL as benign.
//
// activeFlag and mapchangeFlag are SEPARATE uint32 words to avoid bit-stomp
// races (kill-switch toggle never clobbers mapchange flag and vice versa).
// SPSC discipline:
//   activeFlag    — CSSharp writes (kill-switch console cmd), C++ reads
//   mapchangeFlag — C++ writes at OnLevelShutdown (true), CSSharp clears
//                   at OnMapStart (false). CSSharp reads at OnClientDisconnect.
public sealed class PoolMmap : IDisposable
{
    public const uint Magic            = 0x46534E49u;
    public const uint Version          = 7u;
    public const int  Slots            = 120;
    public const int  AimSlotCount     = 64;
    public const int  AimSlotBytes     = 32;  // v7 widened from 24 for bt_target_* feedback channel
    public const int  HeaderBytes      = 16;
    public const int  ActiveOffset     = 8;
    public const int  MapchangeOffset  = 12;
    public const int  ManagedOffset    = HeaderBytes;                   // 16
    public const int  NamesOffset      = ManagedOffset + Slots;         // 136
    public const int  NameBytes        = 32;
    public const int  FifoCapacity     = 16;
    public const int  FifoOffset       = NamesOffset + (Slots * NameBytes);   // 3976
    public const int  FifoHeadOffset   = FifoOffset + (FifoCapacity * NameBytes);  // 4488
    public const int  FifoTailOffset   = FifoHeadOffset + 4;                       // 4492
    // v5 aim-override block. CSSharp writes (rcon command sets these);
    // C++ reads from inside the AimHook PRE-detour.
    public const int  AimOverrideEnOffset    = FifoTailOffset + 4;             // 4496
    public const int  AimOverridePitchOffset = AimOverrideEnOffset + 4;        // 4500
    public const int  AimOverrideYawOffset   = AimOverridePitchOffset + 4;     // 4504

    // v6 per-slot aim block. C++ scans AimSlot[64] looking for a matching
    // pawn pointer; if enabled bit set on that entry, uses its pitch/yaw,
    // else falls back to the global override above.
    public const int  AimSlotCountOffset = AimOverrideYawOffset + 4;           // 4508
    public const int  AimSlotsOffset     = AimSlotCountOffset + 8;             // 4516 (8B = count + reserved align pad)
    public const int  AimSlotBotOff      = 0;   // uint64 — CCSBot* (== `this` inside UpdateLookAngles, == pawn.Bot.Handle)
    public const int  AimSlotEnabledOff  = 8;
    public const int  AimSlotPitchOff    = 12;  // C# writes
    public const int  AimSlotYawOff      = 16;  // C# writes
    public const int  AimSlotBtPitchOff  = 20;  // C++ writes BT's m_lookPitch
    public const int  AimSlotBtYawOff    = 24;  // C++ writes BT's m_lookYaw

    public const int  Total              = AimSlotsOffset + (AimSlotCount * AimSlotBytes);  // 6052

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _va;
    private string _path = "";

    public bool IsOpen => _va != null;
    public string Path => _path;

    public bool Open(string path)
    {
        Close();
        _path = path;
        try
        {
            using (var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                if (fs.Length < Total) fs.SetLength(Total);
            }

            _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, Total, MemoryMappedFileAccess.ReadWrite);
            _va  = _mmf.CreateViewAccessor(0, Total, MemoryMappedFileAccess.ReadWrite);

            uint magic = _va.ReadUInt32(0);
            uint version = _va.ReadUInt32(4);
            if (magic != Magic || version != Version)
            {
                // Stale or older-version pool — full reinit. v3 → v4 layout
                // shift makes any preserved bytes meaningless; safest to wipe.
                Log.Warn($"PoolMmap reinit (magic=0x{magic:X8} ver={version} → v{Version})");
                _va.Write(0, Magic);
                _va.Write(4, Version);
                _va.Write(ActiveOffset, 1u);
                _va.Write(MapchangeOffset, 0u);
                ZeroManaged();
                ZeroNames();
                ZeroFifo();
                ZeroAimSlots();
            }
            else
            {
                // Valid v4 pool — fresh boot still wants clean slate.
                _va.Write(ActiveOffset, 1u);
                _va.Write(MapchangeOffset, 0u);
                ZeroManaged();
                ZeroNames();
                ZeroFifo();
                ZeroAimSlots();
            }
            Log.Info($"PoolMmap opened: {path} (v{Version}, {Slots} slots, active=1, mapchange=0)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"PoolMmap open failed: {ex.Message}");
            Close();
            return false;
        }
    }

    private void ZeroManaged()
    {
        if (_va == null) return;
        for (int i = 0; i < Slots; i++) _va.Write(ManagedOffset + i, (byte)0);
    }

    private void ZeroNames()
    {
        if (_va == null) return;
        for (int i = 0; i < Slots * NameBytes; i++) _va.Write(NamesOffset + i, (byte)0);
    }

    private void ZeroFifo()
    {
        if (_va == null) return;
        for (int i = 0; i < FifoCapacity * NameBytes; i++) _va.Write(FifoOffset + i, (byte)0);
        _va.Write(FifoHeadOffset, 0u);
        _va.Write(FifoTailOffset, 0u);
    }

    // v5 aim-override block accessors. Single global pair (one pitch/yaw for
    // all bots in the fleet); per-slot is a future extension. Reads/writes
    // are non-atomic uint32/float — fine because both sides run on the same
    // game tick thread.
    public void WriteAimOverride(bool enabled, float pitch, float yaw)
    {
        if (_va == null) return;
        _va.Write(AimOverridePitchOffset, pitch);
        _va.Write(AimOverrideYawOffset,   yaw);
        // Write enable LAST so the C++ reader sees consistent values.
        Thread.MemoryBarrier();
        _va.Write(AimOverrideEnOffset, enabled ? 1u : 0u);
    }

    public void ClearAimOverride()
    {
        if (_va == null) return;
        _va.Write(AimOverrideEnOffset, 0u);
    }

    public (bool enabled, float pitch, float yaw) ReadAimOverride()
    {
        if (_va == null) return (false, 0f, 0f);
        bool en = _va.ReadUInt32(AimOverrideEnOffset) != 0;
        float p = _va.ReadSingle(AimOverridePitchOffset);
        float y = _va.ReadSingle(AimOverrideYawOffset);
        return (en, p, y);
    }

    // v6 per-slot aim accessors. Slot index here is [0..AimSlotCount-1] —
    // we use the game's player slot (0..63) directly to keep the mapping
    // trivial in C# code. C++ side scans linearly looking for matching
    // bot_key, so the index is irrelevant to the hot path. Key is the
    // CCSBot* (== pawn.Bot.Handle, == `this` inside the hook handler) —
    // not pawn.Handle (see pool_format.h note for why).
    public void WriteAimSlot(int slot, ulong botKey, bool enabled, float pitch, float yaw)
    {
        if (_va == null) return;
        if (slot < 0 || slot >= AimSlotCount) return;
        int baseOff = AimSlotsOffset + slot * AimSlotBytes;
        // Order matters: write the enable LAST (after bot_key + body) so a
        // racing C++ reader can never see enabled=1 with stale body fields.
        // Both sides on the same game tick thread in normal flow, but the
        // discipline costs nothing.
        _va.Write(baseOff + AimSlotPitchOff, pitch);
        _va.Write(baseOff + AimSlotYawOff,   yaw);
        _va.Write(baseOff + AimSlotBotOff,   botKey);
        Thread.MemoryBarrier();
        _va.Write(baseOff + AimSlotEnabledOff, enabled ? 1u : 0u);
    }

    /// <summary>Clear the AimSlot at <paramref name="slot"/> — disables and
    /// zeroes bot_key so a future stale-pointer collision can't trigger an
    /// override on a recycled CCSBot.</summary>
    public void ClearAimSlot(int slot)
    {
        if (_va == null) return;
        if (slot < 0 || slot >= AimSlotCount) return;
        int baseOff = AimSlotsOffset + slot * AimSlotBytes;
        _va.Write(baseOff + AimSlotEnabledOff, 0u);
        Thread.MemoryBarrier();
        _va.Write(baseOff + AimSlotBotOff,   0UL);
    }

    public (ulong botKey, bool enabled, float pitch, float yaw) ReadAimSlot(int slot)
    {
        if (_va == null) return (0UL, false, 0f, 0f);
        if (slot < 0 || slot >= AimSlotCount) return (0UL, false, 0f, 0f);
        int baseOff = AimSlotsOffset + slot * AimSlotBytes;
        ulong key = _va.ReadUInt64(baseOff + AimSlotBotOff);
        bool  en  = _va.ReadUInt32(baseOff + AimSlotEnabledOff) != 0;
        float p   = _va.ReadSingle(baseOff + AimSlotPitchOff);
        float y   = _va.ReadSingle(baseOff + AimSlotYawOff);
        return (key, en, p, y);
    }

    /// <summary>v7 — read BT's m_lookPitch/Yaw that the C++ handler captured
    /// last tick (before our override stomped on it). This is the clean
    /// engine-target value AimController feeds noise on top of, no need to
    /// disarm and read m_lookPitch directly. Returns NaN if slot has no
    /// bot_key registered (no entry to read from).</summary>
    public (float btPitch, float btYaw) ReadBotTarget(int slot)
    {
        if (_va == null) return (float.NaN, float.NaN);
        if (slot < 0 || slot >= AimSlotCount) return (float.NaN, float.NaN);
        int baseOff = AimSlotsOffset + slot * AimSlotBytes;
        ulong key = _va.ReadUInt64(baseOff + AimSlotBotOff);
        if (key == 0) return (float.NaN, float.NaN);
        float p = _va.ReadSingle(baseOff + AimSlotBtPitchOff);
        float y = _va.ReadSingle(baseOff + AimSlotBtYawOff);
        return (p, y);
    }

    private void ZeroAimSlots()
    {
        if (_va == null) return;
        for (int i = 0; i < AimSlotCount * AimSlotBytes; i++) _va.Write(AimSlotsOffset + i, (byte)0);
        _va.Write(AimSlotCountOffset, (uint)AimSlotCount);
    }

    public void Write(int slot, byte val)
    {
        if (_va == null) return;
        if (slot < 0 || slot >= Slots) return;
        _va.Write(ManagedOffset + slot, val);
    }

    public byte Read(int slot)
    {
        if (_va == null) return 0;
        if (slot < 0 || slot >= Slots) return 0;
        return _va.ReadByte(ManagedOffset + slot);
    }

    public void WriteActive(bool active)
    {
        if (_va == null) return;
        _va.Write(ActiveOffset, active ? 1u : 0u);
    }

    public bool ReadActive()
    {
        if (_va == null) return false;
        return _va.ReadUInt32(ActiveOffset) != 0;
    }

    // Mapchange flag: C++ side sets at OnLevelShutdown (true), CSSharp side
    // reads at OnClientDisconnect to skip Despawn during the synthetic
    // disconnect cascade, and clears at OnMapStart after snapshotting the
    // active personas. SPSC at any moment — only one side writes at a time.
    public void WriteMapchangeFlag(bool inProgress)
    {
        if (_va == null) return;
        _va.Write(MapchangeOffset, inProgress ? 1u : 0u);
    }

    public bool IsMapchangeInProgress()
    {
        if (_va == null) return false;
        return _va.ReadUInt32(MapchangeOffset) != 0;
    }

    // Write persona name into per-slot 32-byte buffer. Truncates to 31 chars
    // and always null-terminates. Empty/null name clears the slot's name.
    public void WriteName(int slot, string? name)
    {
        if (_va == null) return;
        if (slot < 0 || slot >= Slots) return;
        var bytes = new byte[NameBytes];  // zero-init
        if (!string.IsNullOrEmpty(name))
        {
            var src = Encoding.UTF8.GetBytes(name);
            int n = Math.Min(src.Length, NameBytes - 1);
            Array.Copy(src, bytes, n);
            // bytes[n..] already 0 from new byte[].
        }
        int baseOff = NamesOffset + slot * NameBytes;
        for (int i = 0; i < NameBytes; i++) _va.Write(baseOff + i, bytes[i]);
    }

    public string ReadName(int slot)
    {
        if (_va == null) return "";
        if (slot < 0 || slot >= Slots) return "";
        int baseOff = NamesOffset + slot * NameBytes;
        var bytes = new byte[NameBytes];
        for (int i = 0; i < NameBytes; i++) bytes[i] = _va.ReadByte(baseOff + i);
        int len = 0;
        while (len < NameBytes && bytes[len] != 0) len++;
        return Encoding.UTF8.GetString(bytes, 0, len);
    }

    // Push a persona name into the SPSC FIFO. C++ Hider pops from the other
    // side at CFC PRE. Returns false if the queue is full (caller should
    // either drop the spawn or just retry; queue capacity is 16, well above
    // any realistic batched replica_spawn_bots N).
    public bool PushFifo(string name)
    {
        if (_va == null || string.IsNullOrEmpty(name)) return false;
        uint head = _va.ReadUInt32(FifoHeadOffset);
        uint tail = _va.ReadUInt32(FifoTailOffset);
        if (head - tail >= (uint)FifoCapacity) return false;  // full
        int slotIdx = (int)(head % (uint)FifoCapacity);
        var bytes = new byte[NameBytes];
        var src = Encoding.UTF8.GetBytes(name);
        int n = Math.Min(src.Length, NameBytes - 1);
        Array.Copy(src, bytes, n);
        int slotOff = FifoOffset + slotIdx * NameBytes;
        for (int i = 0; i < NameBytes; i++) _va.Write(slotOff + i, bytes[i]);
        // Release-store head AFTER body write so C++ side sees populated slot.
        Thread.MemoryBarrier();
        _va.Write(FifoHeadOffset, head + 1);
        return true;
    }

    public void Close()
    {
        try { _va?.Dispose(); } catch { }
        try { _mmf?.Dispose(); } catch { }
        _va = null;
        _mmf = null;
    }

    public void Dispose() => Close();
}
