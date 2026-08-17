// Duration companion of LogicFrame (api-freeze-v1 F6). Moved from OpenSage.Game in scaffolding
// step 4. This file is the integer-only core; the float-typed legacy conveniences the migrating
// OpenSage.Game float sim still leans on live in LogicFrameSpan.FloatCompat.cs under the
// SIMCORE-EXEMPT protocol, and are deleted subsystem-by-subsystem as porting proceeds (F11).

using System;

namespace OpenSage.SimCore.Ticking
{
    public readonly partial struct LogicFrameSpan : IEquatable<LogicFrameSpan>, IComparable<LogicFrameSpan>
    {
        public static readonly LogicFrameSpan Zero = new LogicFrameSpan(0);
        public static readonly LogicFrameSpan One = new LogicFrameSpan(1);

        public readonly uint Value;

        public LogicFrameSpan(uint value)
        {
            Value = value;
        }

        public static LogicFrameSpan operator +(LogicFrameSpan left, LogicFrameSpan right)
        {
            return new LogicFrameSpan(left.Value + right.Value);
        }

        public static LogicFrameSpan operator ++(LogicFrameSpan left)
        {
            return new LogicFrameSpan(left.Value + 1);
        }

        public static LogicFrameSpan operator -(LogicFrameSpan left, LogicFrameSpan right)
        {
            return new LogicFrameSpan(left.Value - right.Value);
        }

        public static LogicFrameSpan operator --(LogicFrameSpan left)
        {
            return new LogicFrameSpan(left.Value - 1);
        }

        public static bool operator ==(LogicFrameSpan left, LogicFrameSpan right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(LogicFrameSpan left, LogicFrameSpan right)
        {
            return !(left == right);
        }

        public static bool operator >(LogicFrameSpan left, LogicFrameSpan right)
        {
            return left.Value > right.Value;
        }

        public static bool operator <(LogicFrameSpan left, LogicFrameSpan right)
        {
            return left.Value < right.Value;
        }

        public static bool operator >=(LogicFrameSpan left, LogicFrameSpan right)
        {
            return left.Value >= right.Value;
        }

        public static bool operator <=(LogicFrameSpan left, LogicFrameSpan right)
        {
            return left.Value <= right.Value;
        }

        public static LogicFrameSpan Max(in LogicFrameSpan a, in LogicFrameSpan b)
        {
            return a.Value > b.Value ? a : b;
        }

        public int CompareTo(LogicFrameSpan other) => Value.CompareTo(other.Value);

        public override bool Equals(object? obj)
        {
            return obj is LogicFrameSpan logicFrameSpan && Equals(logicFrameSpan);
        }

        public bool Equals(LogicFrameSpan other)
        {
            return Value == other.Value;
        }

        public override int GetHashCode()
        {
            return (int)Value;
        }

        public override string ToString()
        {
            return Value.ToString();
        }
    }
}
