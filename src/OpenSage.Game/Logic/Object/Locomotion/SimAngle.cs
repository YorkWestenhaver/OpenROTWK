// Deterministic angle helpers for the S2 locomotor system (fresh code; GPL semantic
// reference: GameCommon.h normalizeAngle / stdAngleDiff - wrap into (-Pi, Pi]).
// All angles are plain Fix64 radians (api-freeze-v1 S2: FixedAngle does not exist).

using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;

namespace OpenSage.Logic.Object.Locomotion;

[SimState]
public static class SimAngle
{
    /// <summary>Wraps an angle into (-Pi, Pi], by raw modular arithmetic (exact).</summary>
    public static Fix64 Normalize(Fix64 angle)
    {
        var r = angle.RawValue % Fix64.PiTimes2.RawValue;
        if (r > Fix64.Pi.RawValue)
        {
            r -= Fix64.PiTimes2.RawValue;
        }
        else if (r <= -Fix64.Pi.RawValue)
        {
            r += Fix64.PiTimes2.RawValue;
        }
        return Fix64.FromRaw(r);
    }

    /// <summary>
    /// The signed shortest rotation from <paramref name="from"/> to <paramref name="to"/>
    /// (GPL stdAngleDiff shape): positive = counter-clockwise.
    /// </summary>
    public static Fix64 Diff(Fix64 to, Fix64 from) => Normalize(to - from);
}
