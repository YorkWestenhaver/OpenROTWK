namespace OpenSage.SimCore.Numerics;

/// <summary>
/// A process-stable hash for fixed-point value types.
/// </summary>
/// <remarks>
/// <c>System.HashCode</c> mixes in a seed generated once per process, so two runs of the same
/// binary — or two peers in the same match — produce different hashes for identical sim state.
/// Anything derived from such a hash (bucket order, a debug fingerprint, a tie-break) diverges
/// silently. SIMCORE005 therefore bans <c>System.HashCode</c> inside the quarantine and the
/// SimCore value types fold their raw Q31.32 words here instead: FNV-1a over the 64-bit raws,
/// then a xor-fold to <see cref="int"/>. Same raws in, same hash out, on every machine and
/// every run.
/// </remarks>
internal static class DeterministicHash
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong Begin() => OffsetBasis;

    public static ulong Add(ulong hash, long value)
    {
        var bits = (ulong)value;

        for (var i = 0; i < 8; i++)
        {
            hash ^= (bits >> (i * 8)) & 0xFF;
            hash *= Prime;
        }

        return hash;
    }

    public static int Finish(ulong hash) => (int)(hash ^ (hash >> 32));

    public static int Combine(long a, long b) => Finish(Add(Add(Begin(), a), b));

    public static int Combine(long a, long b, long c) => Finish(Add(Add(Add(Begin(), a), b), c));
}
