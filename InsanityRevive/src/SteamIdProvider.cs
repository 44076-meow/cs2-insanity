namespace InsanityRevive;

public interface ISteamIdProvider
{
    ulong Generate(int botSlot);
    string Mode { get; }
}

// Reserved synthetic range: 76561198_900_000_000 .. 76561198_999_999_999.
// Real Steam accounts populate the lower part of this prefix space, so
// staying above 900M minimizes any chance of collision with a live
// account. Slot is folded into the seed so the same slot under the same
// session always yields the same ID — useful for telemetry diffing.
public sealed class SyntheticSteamIdProvider : ISteamIdProvider
{
    public string Mode => "synthetic";

    private const ulong RangeBase = 76561198_900_000_000UL;
    private const ulong RangeSpan =          99_999_999UL;
    private readonly ulong _sessionSeed;

    public SyntheticSteamIdProvider(string sessionId)
    {
        unchecked
        {
            ulong h = 0xCBF29CE484222325UL;
            foreach (var c in sessionId) { h = (h ^ c) * 0x100000001B3UL; }
            _sessionSeed = h;
        }
    }

    public ulong Generate(int botSlot)
    {
        unchecked
        {
            ulong x = _sessionSeed ^ ((ulong)botSlot * 0x9E3779B97F4A7C15UL);
            x ^= x >> 33; x *= 0xFF51AFD7ED558CCDUL;
            x ^= x >> 33; x *= 0xC4CEB9FE1A85EC53UL;
            x ^= x >> 33;
            return RangeBase + (x % (RangeSpan + 1));
        }
    }
}
