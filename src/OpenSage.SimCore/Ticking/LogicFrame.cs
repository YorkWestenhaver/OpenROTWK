// The canonical logic-frame counter type (api-freeze-v1 F6; design-simcore-scaffolding §4.1).
//
// Moved from OpenSage.Game (OpenSage.Logic.Object) in scaffolding step 4: the frame counter is
// sim substrate, so the type lives in SimCore. It is a plain uint underneath, matching the
// original engine's unsigned counter semantics (the CRC cadence gate performs an unsigned
// divide - crc-byteorder §3.1). There is no float anywhere on this surface.

using System;

namespace OpenSage.SimCore.Ticking;

public readonly struct LogicFrame : IEquatable<LogicFrame>, IComparable<LogicFrame>
{
    public static readonly LogicFrame Zero = default;
    public static readonly LogicFrame MaxValue = new LogicFrame(uint.MaxValue);

    public readonly uint Value;

    public LogicFrame(uint value)
    {
        Value = value;
    }

    public static LogicFrame operator +(LogicFrame left, LogicFrameSpan right)
    {
        return new LogicFrame(left.Value + right.Value);
    }

    public static LogicFrame operator ++(LogicFrame left)
    {
        return new LogicFrame(left.Value + 1);
    }

    public static LogicFrameSpan operator -(LogicFrame left, LogicFrame right)
    {
        return new LogicFrameSpan(left.Value - right.Value);
    }

    public static LogicFrame operator -(LogicFrame left, uint right)
    {
        return new LogicFrame(left.Value - right);
    }

    public static bool operator <(LogicFrame left, LogicFrame right)
    {
        return left.Value < right.Value;
    }

    public static bool operator <=(LogicFrame left, LogicFrame right)
    {
        return left.Value <= right.Value;
    }

    public static bool operator >(LogicFrame left, LogicFrame right)
    {
        return left.Value > right.Value;
    }

    public static bool operator >=(LogicFrame left, LogicFrame right)
    {
        return left.Value >= right.Value;
    }

    public static bool operator ==(LogicFrame left, LogicFrame right)
    {
        return left.Value == right.Value;
    }

    public static bool operator !=(LogicFrame left, LogicFrame right)
    {
        return left.Value != right.Value;
    }

    public int CompareTo(LogicFrame other) => Value.CompareTo(other.Value);

    public override string ToString()
    {
        return Value.ToString();
    }

    public override bool Equals(object? obj) => obj is LogicFrame frame && Equals(frame);

    public bool Equals(LogicFrame other) => Value == other.Value;

    public override int GetHashCode() => Value.GetHashCode();
}
