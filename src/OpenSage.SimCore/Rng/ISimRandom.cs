using OpenSage.SimCore.Numerics;

namespace OpenSage.SimCore.Rng;

/// <summary>
/// The only randomness surface simulation code can reach (api-freeze-v1 F5, seam S3):
/// <c>ISimContext.GameLogicRandom</c> is of this type. It is the draw-counting wrapper around
/// the context-owned <see cref="LogicRandom"/>, and the count it maintains is conformance
/// channel 5 (xfer-conformance-strategy §3) - a module that starts or stops drawing shows up
/// as a draw-count mismatch even when every downstream value happens to still agree.
/// </summary>
public interface ISimRandom
{
    /// <summary>
    /// Draws taken from this stream since the match began. Monotonic, checkpointed, and
    /// compared with <c>Tolerance.DrawCount</c> by the differential harness.
    /// </summary>
    ulong DrawCount { get; }

    /// <summary>The raw 32-bit draw.</summary>
    uint NextUInt32();

    /// <summary>An integer in the inclusive range [lo, hi], SAGE semantics.</summary>
    int Next(int lo, int hi);

    /// <summary>
    /// A <see cref="Fix64"/> in [lo, hi), by exact integer scaling - no float is involved at
    /// any point (F4: there are exactly two blessed float boundaries, and this is not one).
    /// </summary>
    Fix64 NextFix64(Fix64 lo, Fix64 hi);
}
