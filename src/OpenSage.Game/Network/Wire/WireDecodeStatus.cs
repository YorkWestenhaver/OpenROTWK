// Typed decode failure vocabulary for the Network/Wire codecs (task N2). Every decode entry
// point in this directory returns one of these instead of throwing: wire bytes are untrusted
// input (they may be truncated, forged, or simply from an older/newer build), and "malformed
// input returns a typed failure and never throws an unhandled exception" is a hard requirement
// of the packet, not a style preference - a peer that can crash another peer by sending it a
// short packet is a denial-of-service bug in a lockstep engine where every peer must keep
// running in step.

namespace OpenSage.Network.Wire;

public enum WireDecodeStatus : byte
{
    /// <summary>Decode completed; the produced value is meaningful.</summary>
    Success = 0,

    /// <summary>The buffer ended before a field that was declared (by an earlier field, or by
    /// the wire format itself) could be fully read. Covers plain truncation.</summary>
    UnexpectedEndOfData,

    /// <summary>The frame's <see cref="WireProtocolVersion"/> does not match this build's.</summary>
    UnsupportedProtocolVersion,

    /// <summary>A frame's length prefix is negative, or exceeds <see cref="WireLimits.MaxFramePayloadBytes"/>.</summary>
    LengthPrefixInvalid,

    /// <summary>A <see cref="OpenSage.SimCore.Orders.SimOrder"/>'s declared argument count exceeds
    /// <see cref="WireLimits.MaxArgumentsPerOrder"/>.</summary>
    ArgumentCountOverflow,

    /// <summary>An <see cref="OrdersPacket"/>'s declared order count exceeds
    /// <see cref="WireLimits.MaxOrdersPerPacket"/>.</summary>
    OrderCountOverflow,

    /// <summary>The wire byte read as a <see cref="OpenSage.SimCore.Orders.SimOrderArgKind"/> tag
    /// is not one of the ten recovered values (F6; holes are malformed input).</summary>
    UnknownArgKind,

    /// <summary>The tag names a real <see cref="OpenSage.SimCore.Orders.SimOrderArgKind"/> (Raw9 /
    /// Raw10) that SimCore's <c>SimOrderArg</c> has no public factory to construct yet - decoding
    /// it would require reaching a private constructor from outside SimCore. Not a malformed-wire
    /// case; a forward-compatibility gap to close when SimCore grows the factory.</summary>
    UnconstructibleArgKind,

    /// <summary>A wire float32 bit pattern decodes to NaN, which is malformed sim input
    /// (<c>Fix64.FromWireFloat</c>'s own contract) - rejected before that call is ever made, so
    /// no exception crosses the decode boundary.</summary>
    MalformedWireFloat,

    /// <summary>A boolean-kind argument's payload byte was neither 0 nor 1.</summary>
    InvalidBooleanEncoding,

    /// <summary>The wire's <c>GameMessageType</c> value falls in a hole of the recovered table
    /// (api-freeze-v1 F6) - rejected before constructing a <c>SimOrder</c>, so
    /// <c>MalformedOrderException</c> never gets a chance to throw.</summary>
    UnknownMessageType,
}
