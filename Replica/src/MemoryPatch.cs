using System;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;

namespace Replica;

// Linux byte-patcher modeled after CSSharpFixes' PatchManager:
// resolve gamedata name -> mprotect RW -> memcpy bytes -> mprotect back
// -> remember originals so we can undo on plugin unload. Returns false
// on any failure; caller logs into telemetry. Windows path is best-
// effort (VirtualProtect).
//
// The gamedata-name route avoids reimplementing wildcard sig parsing —
// CSSharp's BaseMemoryFunction does it for us, and we only need the
// resolved entry address from `.Handle`.
public sealed class MemoryPatch
{
    public string Name { get; }
    public IntPtr Address { get; private set; }
    public bool Applied { get; private set; }
    public string? Error { get; private set; }

    private byte[]? _original;

    public MemoryPatch(string name) { Name = name; }

    public bool Apply(string gamedataName, byte[] patchBytes)
    {
        if (patchBytes.Length == 0) { Error = "empty patch"; return false; }

        try
        {
            var resolver = new MemoryFunctionVoid<IntPtr>(gamedataName);
            Address = resolver.Handle;
            if (Address == IntPtr.Zero) { Error = "address not found"; return false; }
        }
        catch (Exception ex) { Error = $"resolve: {ex.Message}"; return false; }

        _original = new byte[patchBytes.Length];
        Marshal.Copy(Address, _original, 0, patchBytes.Length);

        if (!Write(Address, patchBytes)) return false;
        Applied = true;
        return true;
    }

    public void Undo()
    {
        if (!Applied || _original == null) return;
        if (Write(Address, _original)) Applied = false;
    }

    private bool Write(IntPtr addr, byte[] bytes)
    {
        var pageSize = Environment.SystemPageSize;
        var pageStart = (long)addr & ~(pageSize - 1);
        var spanLen = ((long)addr + bytes.Length) - pageStart;
        var pages = (int)((spanLen + pageSize - 1) / pageSize) * pageSize;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // PROT_READ | PROT_WRITE | PROT_EXEC = 7
            if (mprotect((IntPtr)pageStart, (UIntPtr)pages, 7) != 0)
            { Error = $"mprotect RWX errno={Marshal.GetLastPInvokeError()}"; return false; }
            Marshal.Copy(bytes, 0, addr, bytes.Length);
            // PROT_READ | PROT_EXEC = 5
            mprotect((IntPtr)pageStart, (UIntPtr)pages, 5);
            return true;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // PAGE_EXECUTE_READWRITE = 0x40
            if (!VirtualProtect((IntPtr)pageStart, (UIntPtr)pages, 0x40, out var old))
            { Error = "VirtualProtect failed"; return false; }
            Marshal.Copy(bytes, 0, addr, bytes.Length);
            VirtualProtect((IntPtr)pageStart, (UIntPtr)pages, old, out _);
            return true;
        }

        Error = "unsupported OS";
        return false;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int mprotect(IntPtr addr, UIntPtr len, int prot);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr addr, UIntPtr size, uint newProt, out uint oldProt);
}
