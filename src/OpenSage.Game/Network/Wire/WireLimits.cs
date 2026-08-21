namespace OpenSage.Network.Wire;

/// <summary>
/// Defensive caps on attacker/corruption-controlled counts and lengths read off the wire.
/// A declared count is never trusted to pre-size a collection or drive a loop on its own -
/// every one of these is checked before it is used, so a forged huge value fails as a typed
/// <see cref="WireDecodeStatus"/> instead of attempting an unbounded allocation.
/// </summary>
internal static class WireLimits
{
    /// <summary>
    /// Generous headroom over any real BFME2 order's argument list (the richest recovered
    /// message types take an object id plus a handful of scalars/positions - nowhere near
    /// this many), while still bounding a forged argument count.
    /// </summary>
    public const int MaxArgumentsPerOrder = 64;

    /// <summary>
    /// Far more than one lockstep tick could ever legitimately carry (8 slots x several
    /// orders each), while still bounding a forged order count.
    /// </summary>
    public const int MaxOrdersPerPacket = 1024;

    /// <summary>
    /// Comfortably above the worst-case <see cref="MaxOrdersPerPacket"/> x <see cref="MaxArgumentsPerOrder"/>
    /// payload (roughly 850 KB at the codec's per-argument byte costs), while still bounding a
    /// forged length prefix.
    /// </summary>
    public const int MaxFramePayloadBytes = 4 * 1024 * 1024;
}
