// Sim-side order structures (api-freeze-v1 F6; design-simcore-scaffolding §4.3).
//
// Everything past the IngestOrders phase boundary is float-free: wire float32 payloads
// (argument tag 0x01 and the Coord3D components of tag 0x06 - replay-format-recon.md) enter
// only as raw IEEE-754 bit patterns and are quantized to Fix64 via the blessed F4 boundary
// Fix64.FromWireFloat, identically on every peer. No float type appears anywhere on this
// surface. Argument kind numbering is the wire's own argument-type byte.

using System;
using System.Collections.Generic;
using OpenSage.SimCore.Numerics;

namespace OpenSage.SimCore.Orders
{
    /// <summary>
    /// Wire argument-type bytes as observed in the BFME2 replay/lockstep stream
    /// (replay-format-recon.md: sizes 0x00=4, 0x01=4, 0x02=1, 0x03=4, 0x04=4, 0x06=12,
    /// 0x07=12, 0x08=16, 0x09=4 in BFME2, 0x0A=4).
    /// </summary>
    public enum SimOrderArgKind : byte
    {
        Integer = 0,
        Fixed = 1,          // wire float32, quantized at ingestion
        Boolean = 2,
        ObjectId = 3,
        Unsigned = 4,
        Position = 6,       // wire Coord3D (3 x float32), quantized at ingestion
        ScreenPosition = 7,
        ScreenRectangle = 8, // one IRegion2D: x1,y1,x2,y2 (gamemessage-enum-map §1)
        Raw9 = 9,           // 4 bytes in BFME2; semantics unrecovered
        Raw10 = 10,
    }

    /// <summary>
    /// One order argument. Plain struct-of-fields rather than a union; only the fields implied
    /// by <see cref="Kind"/> are meaningful. Constructed exclusively through the factory
    /// methods so that float payloads can only enter as IEEE bits.
    /// </summary>
    public readonly struct SimOrderArg
    {
        public readonly SimOrderArgKind Kind;

        public readonly int Integer;
        public readonly uint Unsigned;
        public readonly bool Boolean;
        public readonly uint ObjectId;
        public readonly Fix64 Fixed;
        public readonly FixVector3 Position;
        public readonly int X0, Y0, X1, Y1; // ScreenPosition uses X0/Y0; ScreenRectangle all four

        private SimOrderArg(SimOrderArgKind kind, int integer = 0, uint unsigned = 0,
            bool boolean = false, uint objectId = 0, Fix64 @fixed = default,
            FixVector3 position = default, int x0 = 0, int y0 = 0, int x1 = 0, int y1 = 0)
        {
            Kind = kind;
            Integer = integer;
            Unsigned = unsigned;
            Boolean = boolean;
            ObjectId = objectId;
            Fixed = @fixed;
            Position = position;
            X0 = x0;
            Y0 = y0;
            X1 = x1;
            Y1 = y1;
        }

        public static SimOrderArg FromInteger(int value) => new(SimOrderArgKind.Integer, integer: value);

        public static SimOrderArg FromBoolean(bool value) => new(SimOrderArgKind.Boolean, boolean: value);

        public static SimOrderArg FromObjectId(uint value) => new(SimOrderArgKind.ObjectId, objectId: value);

        public static SimOrderArg FromUnsigned(uint value) => new(SimOrderArgKind.Unsigned, unsigned: value);

        /// <summary>
        /// The F4 wire-float boundary: the tag-0x01 float32 payload enters as its bit pattern
        /// and is quantized here, before the argument object exists.
        /// </summary>
        public static SimOrderArg FromWireFloat(uint ieeeBits) =>
            new(SimOrderArgKind.Fixed, @fixed: Fix64.FromWireFloat(ieeeBits));

        /// <summary>
        /// The tag-0x06 Coord3D payload (3 x float32), each component quantized via
        /// <see cref="Fix64.FromWireFloat"/>.
        /// </summary>
        public static SimOrderArg FromWirePosition(uint xBits, uint yBits, uint zBits) =>
            new(SimOrderArgKind.Position, position: new FixVector3(
                Fix64.FromWireFloat(xBits),
                Fix64.FromWireFloat(yBits),
                Fix64.FromWireFloat(zBits)));

        public static SimOrderArg FromScreenPosition(int x, int y) =>
            new(SimOrderArgKind.ScreenPosition, x0: x, y0: y);

        public static SimOrderArg FromScreenRectangle(int x0, int y0, int x1, int y1) =>
            new(SimOrderArgKind.ScreenRectangle, x0: x0, y0: y0, x1: x1, y1: y1);
    }

    /// <summary>
    /// One order as the sim sees it after ingestion: a recovered-vocabulary message type plus
    /// integer/Fix64 arguments, stamped with the deterministic dispatch identity
    /// (player index, then per-player submission index within the scheduled frame).
    /// </summary>
    public sealed class SimOrder
    {
        private readonly List<SimOrderArg> _arguments = new();

        public GameMessageType Type { get; }

        public int PlayerIndex { get; }

        public IReadOnlyList<SimOrderArg> Arguments => _arguments;

        public SimOrder(GameMessageType type, int playerIndex)
        {
            if (!GameMessageTypes.IsKnown(type))
            {
                // Holes in the recovered enum are malformed input (F6).
                throw new MalformedOrderException(type);
            }

            Type = type;
            PlayerIndex = playerIndex;
        }

        public void AddArgument(in SimOrderArg argument) => _arguments.Add(argument);
    }

    public sealed class MalformedOrderException : Exception
    {
        public MalformedOrderException(GameMessageType type)
            : base($"Unknown GameMessageType value {(int)type}: holes in the recovered enum are malformed input (api-freeze-v1 F6).")
        {
        }
    }

    public static class GameMessageTypes
    {
        /// <summary>
        /// True iff <paramref name="type"/> is one of the 381 recovered BFME2 message types.
        /// Values in the holes of the recovered table are malformed input. Membership is a
        /// binary search over the generated sorted value table (no reflection, no unordered
        /// collection - SIMCORE004/005).
        /// </summary>
        public static bool IsKnown(GameMessageType type) =>
            Array.BinarySearch(GameMessageTypeTable.SortedValues, (int)type) >= 0;
    }
}
