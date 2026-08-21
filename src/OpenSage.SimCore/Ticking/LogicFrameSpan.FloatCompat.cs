// SIMCORE-EXEMPT: legacy float conveniences carried over from the OpenSage.Game float sim
// during the F11 subsystem-granular migration; scheduled for deletion, never for new sim code.
//
// These members existed on the type before it moved into SimCore (scaffolding step 4) and are
// still called by unmigrated OpenSage.Game code. Sim-side durations are produced only via the
// two blessed F4 boundaries (IniParser's quantizing parse functions and wire ingestion); once
// the calling subsystems port to Fix64, each member here is deleted in the same landing merge
// that deletes its float callers (api-freeze-v1 F11). The Percentage and SageGame overloads of
// the pre-move type could not follow it into SimCore (those types are not visible from here);
// their few call sites were rewritten instead.

using System;

namespace OpenSage.SimCore.Ticking;

public readonly partial struct LogicFrameSpan
{
    public static LogicFrameSpan OneSecond(float logicFramesPerSecond) => new((uint)logicFramesPerSecond);

    public static LogicFrameSpan FromMilliseconds(float milliseconds, float msPerLogicFrame) => new((uint)MathF.Ceiling(milliseconds / msPerLogicFrame));

    public static LogicFrameSpan FromSeconds(float seconds, float logicFramesPerSecond) => new((uint)MathF.Ceiling(seconds * logicFramesPerSecond));

    public static LogicFrameSpan operator *(LogicFrameSpan left, float right)
    {
        return new LogicFrameSpan((uint)MathF.Ceiling(left.Value * right));
    }

    public static LogicFrameSpan operator /(LogicFrameSpan left, float right)
    {
        return new LogicFrameSpan((uint)MathF.Ceiling(left.Value / right));
    }

    public static float operator /(LogicFrameSpan left, LogicFrameSpan right)
    {
        return left.Value / (float)right.Value;
    }
}
