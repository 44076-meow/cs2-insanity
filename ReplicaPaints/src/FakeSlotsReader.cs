using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace ReplicaPaints;

// Read-only mirror of the slot-management byte array maintained by
// Replica's PoolMmap. We never write — Replica is the single
// writer of the pool.
//
// The pool layout has grown over time (v4..v7), but the fields we
// need have been stable since v4:
//   [0..3]   magic   = 0x46534E49 ('INSF')
//   [4..7]   version (we accept >= MinAcceptedVersion)
//   [16..]   managed[120] (uint8: 0 = unmanaged, 1 = Replica fake-client)
//
// If the file isn't there (Revive not loaded yet) or the header looks
// wrong, IsManaged() always returns false — that's the safe default:
// no managed-bot loadouts get applied. Real players are unaffected
// because they're filtered by IsBot beforehand.
public sealed class FakeSlotsReader : IDisposable
{
    public const string DefaultPath  = "/tmp/replica_fake_slots.bin";
    public const uint   ExpectedMagic        = 0x46534E49u;
    public const uint   MinAcceptedVersion   = 4u;
    public const int    SlotCount            = 120;
    public const int    ManagedOffset        = 16;
    public const int    NamesOffset          = ManagedOffset + SlotCount;  // 136
    public const int    NameBytes            = 32;  // null-terminated UTF-8
    public const int    MinPoolBytes         = NamesOffset + SlotCount * NameBytes;  // 3976

    private MemoryMappedFile?           _mmf;
    private MemoryMappedViewAccessor?   _va;
    private string                      _path = "";

    public bool   IsOpen => _va != null;
    public string Path   => _path;

    /// <summary>Try to attach to Replica's pool file. Returns true on success.
    /// Failure is non-fatal — the caller can retry on a later spawn event
    /// (e.g. if Replica boots later than us).</summary>
    public bool TryOpen(string path)
    {
        Close();
        _path = path;
        try
        {
            if (!File.Exists(path))
            {
                // A missing pool is NOT a debug-grade event: every bot then
                // reads as unmanaged → classified human → default loadouts,
                // silently, forever. Exactly this hid a stale pre-rebrand
                // pool_path in settings.json for weeks (2026-06-13 find).
                // If the configured path is custom and dead while the
                // default pool exists, assume stale config and fall back.
                if (!string.Equals(path, DefaultPath, StringComparison.Ordinal)
                    && File.Exists(DefaultPath))
                {
                    Log.Warn($"FakeSlotsReader: configured pool_path '{path}' missing but " +
                             $"default '{DefaultPath}' exists — stale config? Falling back. " +
                             "Update settings.json pool_path to silence this.");
                    return TryOpen(DefaultPath);
                }
                Log.Warn($"FakeSlotsReader: pool file missing ({path}) — every bot will " +
                         "read as unmanaged (human/default loadouts) until it appears");
                return false;
            }
            var info = new FileInfo(path);
            if (info.Length < MinPoolBytes)
            {
                Log.Warn($"FakeSlotsReader: pool file too small ({info.Length} < {MinPoolBytes})");
                return false;
            }

            _mmf = MemoryMappedFile.CreateFromFile(
                path, FileMode.Open, null, info.Length, MemoryMappedFileAccess.Read);
            _va  = _mmf.CreateViewAccessor(0, info.Length, MemoryMappedFileAccess.Read);

            uint magic   = _va.ReadUInt32(0);
            uint version = _va.ReadUInt32(4);
            if (magic != ExpectedMagic || version < MinAcceptedVersion)
            {
                Log.Warn($"FakeSlotsReader: bad header magic=0x{magic:X8} ver={version}");
                Close();
                return false;
            }
            Log.Info($"FakeSlotsReader: attached to {path} (v{version}, {SlotCount} slots)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"FakeSlotsReader: open failed: {ex.Message}");
            Close();
            return false;
        }
    }

    /// <summary>True iff the slot is marked as a Replica-managed fake-client.
    /// Returns false for unmanaged slots, engine-spawned bots (`bot_add`),
    /// real players, or when the pool isn't attached.</summary>
    public bool IsManaged(int slot)
    {
        if (_va == null) return false;
        if (slot < 0 || slot >= SlotCount) return false;
        return _va.ReadByte(ManagedOffset + slot) != 0;
    }

    /// <summary>Read the persona name Replica wrote into the slot. Returns
    /// empty string if pool isn't attached or the slot has no name yet.
    /// We prefer this over `CCSPlayerController.PlayerName` for managed
    /// bots because Replica overwrites the engine name asynchronously
    /// after `bot_add`: on early ticks PlayerName can still be the engine
    /// default (`Bot01`, …), then flip to the persona name a few frames
    /// later. The pool entry, by contrast, is written synchronously by
    /// Replica *before* it pre-marks the slot as managed, so once
    /// IsManaged returns true the name is already there.</summary>
    public string ReadName(int slot)
    {
        if (_va == null) return "";
        if (slot < 0 || slot >= SlotCount) return "";
        int baseOff = NamesOffset + slot * NameBytes;
        Span<byte> bytes = stackalloc byte[NameBytes];
        for (int i = 0; i < NameBytes; i++) bytes[i] = _va.ReadByte(baseOff + i);
        int len = 0;
        while (len < NameBytes && bytes[len] != 0) len++;
        if (len == 0) return "";
        return Encoding.UTF8.GetString(bytes[..len]);
    }

    public void Close()
    {
        try { _va?.Dispose();  } catch { }
        try { _mmf?.Dispose(); } catch { }
        _va  = null;
        _mmf = null;
    }

    public void Dispose() => Close();
}
