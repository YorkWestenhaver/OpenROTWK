// Baked-LUT trigonometry for the deterministic core (api-freeze-v1 F2,
// design-simcore-scaffolding §1.4, benchmarked in fix64-benchmark §B/§D):
//   - Sin/Cos: 16,384-entry quarter-symmetric sin table, raw-indexed, no interpolation
//     (65,536 effective steps per full turn; measured error ~3.2e-5 — OPEN-7 tracks
//     whether conformance ever forces interpolation; tables are swappable constants).
//   - Atan2: 1,025-entry atan table over the ratio [0,1] plus exactly one Fix64
//     division (the §1.4 guess+fixup division) for the octant-reduced ratio.
// Angles are radians in Q31.32. Table data lives in FixTrig.Tables.g.cs — a checked-in
// artifact of record emitted by src/tools/OpenSage.SimCore.TableGen; a unit test
// spot-checks entries against independently computed constants so a regenerated table
// cannot silently drift.

namespace OpenSage.SimCore.Numerics;

public static partial class FixTrig
{
    // Quarter table: SinTable[i] = round(sin((π/2) · i / 16384) · 2^32), i in [0, 16384).
    internal const int SinTableBits = 14;
    internal const int SinTableSize = 1 << SinTableBits;              // 16,384
    private const int TurnSteps = SinTableSize << 2;                  // 65,536 per full circle
    private const int QuarterMask = SinTableSize - 1;

    // Atan table: AtanTable[i] = round(atan(i / 1024) · 2^32), i in [0, 1024].
    internal const int AtanTableSteps = 1024;                         // 1,025 entries

    /// <summary>Sine of an angle in Q31.32 radians.</summary>
    public static Fix64 Sin(Fix64 angle)
    {
        return Fix64.FromRaw(SinFromTurnIndex(TurnIndex(angle)));
    }

    /// <summary>Cosine of an angle in Q31.32 radians.</summary>
    public static Fix64 Cos(Fix64 angle)
    {
        // cos(a) = sin(a + π/2): a quarter-turn index shift, exact in index space.
        return Fix64.FromRaw(SinFromTurnIndex((TurnIndex(angle) + SinTableSize) & (TurnSteps - 1)));
    }

    // Maps an angle to its step index in [0, 65536) around the circle.
    private static int TurnIndex(Fix64 angle)
    {
        var r = angle.RawValue % Fix64.PI_TIMES_2;
        if (r < 0)
        {
            r += Fix64.PI_TIMES_2;
        }
        // r < 2π·2^32 ≈ 2^34.7, so r · 65536 < 2^50.7: no long overflow.
        return (int)(r * TurnSteps / Fix64.PI_TIMES_2);
    }

    private static long SinFromTurnIndex(int turnIndex)
    {
        var quadrant = turnIndex >> SinTableBits;                     // 0..3
        var i = turnIndex & QuarterMask;
        switch (quadrant)
        {
            case 0:
                return SinTable[i];
            case 1:
                // sin(a) = sin(π − a); index 16384 − i, with the exact peak sin(π/2) = 1.
                return i == 0 ? Fix64.ONE : SinTable[SinTableSize - i];
            case 2:
                return -SinTable[i];
            default:
                return i == 0 ? -Fix64.ONE : -SinTable[SinTableSize - i];
        }
    }

    /// <summary>
    /// Four-quadrant arctangent, in Q31.32 radians in (−π, π]. Octant reduction plus
    /// one table lookup and at most one Fix64 division.
    /// </summary>
    public static Fix64 Atan2(Fix64 y, Fix64 x)
    {
        if (x == Fix64.Zero)
        {
            return y > Fix64.Zero ? Fix64.PiOver2
                 : y < Fix64.Zero ? -Fix64.PiOver2
                 : Fix64.Zero;
        }

        var ax = Fix64.Abs(x);
        var ay = Fix64.Abs(y);
        Fix64 baseAngle;
        if (ay <= ax)
        {
            baseAngle = AtanUnitRatio(ay / ax);
        }
        else
        {
            baseAngle = Fix64.PiOver2 - AtanUnitRatio(ax / ay);
        }

        if (x < Fix64.Zero)
        {
            baseAngle = Fix64.Pi - baseAngle;
        }
        return y < Fix64.Zero ? -baseAngle : baseAngle;
    }

    // t must be in [0, 1]; raw-indexed lookup, truncating index (raw-indexed spec).
    private static Fix64 AtanUnitRatio(Fix64 t)
    {
        var index = (int)(t.RawValue * AtanTableSteps >> Fix64.FRACTIONAL_PLACES);
        return Fix64.FromRaw(AtanTable[index]);
    }
}
