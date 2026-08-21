using System;
using OpenSage.SimCore.Numerics;

namespace OpenSage.SimCore.Rng;

/// <summary>
/// The <see cref="ISimRandom"/> implementation: a thin counting wrapper over the
/// context-owned <see cref="LogicRandom"/> (freeze S3). It adds no randomness of its own -
/// every method forwards one-for-one - so the wrapped stream and the bare generator produce
/// identical sequences, and <see cref="DrawCount"/> is exactly the number of raw draws taken.
/// </summary>
public sealed class CountingSimRandom : ISimRandom
{
    private readonly LogicRandom _random;
    private ulong _drawCount;

    public CountingSimRandom(LogicRandom random)
    {
        _random = random ?? throw new ArgumentNullException(nameof(random));
    }

    /// <summary>The wrapped generator. Engine-only: this is what the CRC channel folds.</summary>
    public LogicRandom Random => _random;

    public ulong DrawCount => _drawCount;

    public uint NextUInt32()
    {
        _drawCount++;
        return _random.NextUInt32();
    }

    public int Next(int lo, int hi)
    {
        var delta = (uint)(hi - lo + 1);

        if (delta == 0)
        {
            // LogicRandom.Next takes no draw on the degenerate range; neither do we, or the
            // count would stop matching the raw stream.
            return _random.Next(lo, hi);
        }

        _drawCount++;
        return _random.Next(lo, hi);
    }

    public Fix64 NextFix64(Fix64 lo, Fix64 hi)
    {
        if (hi > lo)
        {
            _drawCount++;
        }

        return _random.NextFix64(lo, hi);
    }

    /// <summary>
    /// Resets the counter, for save/load and for harness runs that measure per-scenario draw
    /// budgets. Engine-only; modules have no reason to call it and see only
    /// <see cref="ISimRandom"/>.
    /// </summary>
    public void ResetDrawCount(ulong drawCount = 0) => _drawCount = drawCount;
}
