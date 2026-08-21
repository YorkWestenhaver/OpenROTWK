// Wire codec for OpenSage.SimCore.Orders.SimOrderArg (task N2; design-netcode.md R-N5, D13).
//
// R-N5 / D13's boundary ("float-shaped payloads cross as raw uint IEEE bit patterns; decode
// hands them to Fix64.FromWireFloat exactly once") is honoured here using Fix64's own two F4
// escapes as a matched pair: encode calls the blessed display escape ToFloatForDisplay() to
// produce the float32 bits, decode calls FromWireFloat() to consume them. Both already exist
// in SimCore for exactly this shape of round trip - a Fix64 that itself originated from
// FromWireFloat(bits) is, by construction, exactly representable in float32 (Q31.32 has far
// more precision than a 24-bit mantissa within FromWireFloat's non-saturating range), so
// ToFloatForDisplay's round-to-nearest recovers those same bits exactly. No new float-to-Fix64
// path is introduced, and no float ever appears on the SimOrderArg surface itself - only in
// the two bit-conversion calls at the wire edge, matching Fix64.Display.cs's own contract that
// its result "never re-enters sim state" (it is consumed here purely as a bit pattern, never
// read as a numeric float).
//
// SimOrderArg's Fixed/Position fields hold Fix64 already, not the pre-quantization bits, so
// this codec cannot (and must not try to) recover the *original* mouse-pick/camera-unproject
// bits that produced a given Fix64 - that quantization already happened once, at whatever
// upstream boundary first called SimOrderArg.FromWireFloat/FromWirePosition (out of N2's
// scope). What this codec guarantees is that an already-quantized SimOrder round-trips through
// the wire byte-for-byte in its Fix64 form, which is the property every peer actually needs.
//
// Raw9/Raw10 (SimOrderArgKind values 9/10, called out by name in design-netcode.md's arg-kind
// enumeration) currently have no SimCore factory - SimOrderArg's constructor is private and no
// public FromRaw9/FromRaw10 exists - so no live SimOrderArg can hold either kind today. Both
// are still full switch arms below (encode as a forward-compatible 4-byte placeholder reusing
// the Unsigned field; decode as an explicit WireDecodeStatus.UnconstructibleArgKind) so the
// switch stays exhaustive over the whole enum rather than silently dropping them.

using System;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Orders;

namespace OpenSage.Network.Wire;

internal static class SimOrderArgCodec
{
    /// <summary>
    /// Encodes one argument as a kind byte followed by its kind-specific payload. The switch
    /// below is written as an expression with no discard arm and every named
    /// <see cref="SimOrderArgKind"/> member listed explicitly (each arm produces a dummy
    /// <see cref="bool"/> purely so the compiler treats it as an exhaustiveness-checked switch
    /// expression, per the directory's scoped .editorconfig promoting CS8509 to an error) -
    /// a new SimOrderArgKind member added without updating this codec fails the build.
    /// </summary>
    public static void Encode(WireWriter writer, in SimOrderArg arg)
    {
        writer.WriteByte((byte)arg.Kind);

        _ = arg.Kind switch
        {
            SimOrderArgKind.Integer => EncodeInteger(writer, arg),
            SimOrderArgKind.Fixed => EncodeFixed(writer, arg),
            SimOrderArgKind.Boolean => EncodeBoolean(writer, arg),
            SimOrderArgKind.ObjectId => EncodeObjectId(writer, arg),
            SimOrderArgKind.Unsigned => EncodeUnsigned(writer, arg),
            SimOrderArgKind.Position => EncodePosition(writer, arg),
            SimOrderArgKind.ScreenPosition => EncodeScreenPosition(writer, arg),
            SimOrderArgKind.ScreenRectangle => EncodeScreenRectangle(writer, arg),
            // No SimCore factory constructs these kinds yet (see file header); encoded as a
            // generic 4-byte placeholder so a future SimCore factory has a wire shape waiting.
            SimOrderArgKind.Raw9 => EncodeRawPlaceholder(writer, arg),
            SimOrderArgKind.Raw10 => EncodeRawPlaceholder(writer, arg),
        };
    }

    private static bool EncodeInteger(WireWriter writer, in SimOrderArg arg)
    {
        writer.WriteInt32(arg.Integer);
        return true;
    }

    private static bool EncodeFixed(WireWriter writer, in SimOrderArg arg)
    {
        WriteWireFloat(writer, arg.Fixed);
        return true;
    }

    private static bool EncodeBoolean(WireWriter writer, in SimOrderArg arg)
    {
        writer.WriteBoolean(arg.Boolean);
        return true;
    }

    private static bool EncodeObjectId(WireWriter writer, in SimOrderArg arg)
    {
        writer.WriteUInt32(arg.ObjectId);
        return true;
    }

    private static bool EncodeUnsigned(WireWriter writer, in SimOrderArg arg)
    {
        writer.WriteUInt32(arg.Unsigned);
        return true;
    }

    private static bool EncodePosition(WireWriter writer, in SimOrderArg arg)
    {
        WriteWireFloat(writer, arg.Position.X);
        WriteWireFloat(writer, arg.Position.Y);
        WriteWireFloat(writer, arg.Position.Z);
        return true;
    }

    private static bool EncodeScreenPosition(WireWriter writer, in SimOrderArg arg)
    {
        writer.WriteInt32(arg.X0);
        writer.WriteInt32(arg.Y0);
        return true;
    }

    private static bool EncodeScreenRectangle(WireWriter writer, in SimOrderArg arg)
    {
        writer.WriteInt32(arg.X0);
        writer.WriteInt32(arg.Y0);
        writer.WriteInt32(arg.X1);
        writer.WriteInt32(arg.Y1);
        return true;
    }

    private static bool EncodeRawPlaceholder(WireWriter writer, in SimOrderArg arg)
    {
        writer.WriteUInt32(arg.Unsigned);
        return true;
    }

    private static void WriteWireFloat(WireWriter writer, Fix64 value)
    {
        var bits = BitConverter.SingleToUInt32Bits(value.ToFloatForDisplay());
        writer.WriteUInt32(bits);
    }

    /// <summary>
    /// Decodes one argument. The kind byte is validated against the ten recovered
    /// <see cref="SimOrderArgKind"/> values before the inner switch runs, so the switch itself
    /// can be a plain exhaustive expression over the enum (same CS8509-as-error contract as
    /// <see cref="Encode"/>) without needing its own catch-all for unrecognised bytes.
    /// </summary>
    public static WireDecodeResult<SimOrderArg> Decode(ref WireReader reader)
    {
        if (!reader.TryReadByte(out var kindByte))
        {
            return WireDecodeResult<SimOrderArg>.Fail(WireDecodeStatus.UnexpectedEndOfData);
        }

        if (!TryGetKnownKind(kindByte, out var kind))
        {
            return WireDecodeResult<SimOrderArg>.Fail(WireDecodeStatus.UnknownArgKind);
        }

        return kind switch
        {
            SimOrderArgKind.Integer => DecodeInteger(ref reader),
            SimOrderArgKind.Fixed => DecodeFixed(ref reader),
            SimOrderArgKind.Boolean => DecodeBoolean(ref reader),
            SimOrderArgKind.ObjectId => DecodeObjectId(ref reader),
            SimOrderArgKind.Unsigned => DecodeUnsigned(ref reader),
            SimOrderArgKind.Position => DecodePosition(ref reader),
            SimOrderArgKind.ScreenPosition => DecodeScreenPosition(ref reader),
            SimOrderArgKind.ScreenRectangle => DecodeScreenRectangle(ref reader),
            SimOrderArgKind.Raw9 => WireDecodeResult<SimOrderArg>.Fail(WireDecodeStatus.UnconstructibleArgKind),
            SimOrderArgKind.Raw10 => WireDecodeResult<SimOrderArg>.Fail(WireDecodeStatus.UnconstructibleArgKind),
        };
    }

    private static WireDecodeResult<SimOrderArg> DecodeInteger(ref WireReader reader)
    {
        if (!reader.TryReadInt32(out var value))
        {
            return WireDecodeResult<SimOrderArg>.Fail(WireDecodeStatus.UnexpectedEndOfData);
        }

        return WireDecodeResult<SimOrderArg>.Ok(SimOrderArg.FromInteger(value));
    }

    private static WireDecodeResult<SimOrderArg> DecodeFixed(ref WireReader reader)
    {
        if (!reader.TryReadUInt32(out var bits))
        {
            return WireDecodeResult<SimOrderArg>.Fail(WireDecodeStatus.UnexpectedEndOfData);
        }

        if (IsNaNBits(bits))
        {
            return WireDecodeResult<SimOrderArg>.Fail(WireDecodeStatus.MalformedWireFloat);
        }

        return WireDecodeResult<SimOrderArg>.Ok(SimOrderArg.FromWireFloat(bits));
    }

    private static WireDecodeResult<SimOrderArg> DecodeBoolean(ref WireReader reader)
    {
        if (!reader.TryReadByte(out var raw))
        {
            return WireDecodeResult<SimOrderArg>.Fail(WireDecodeStatus.UnexpectedEndOfData);
        }

        // Strict: only 0/1 are valid on this wire (design choice, not a retail fact) - any
        // other byte value is ambiguous and therefore malformed input, not "truthy".
        return raw switch
        {
            0 => WireDecodeResult<SimOrderArg>.Ok(SimOrderArg.FromBoolean(false)),
            1 => WireDecodeResult<SimOrderArg>.Ok(SimOrderArg.FromBoolean(true)),
            _ => WireDecodeResult<SimOrderArg>.Fail(WireDecodeStatus.InvalidBooleanEncoding),
        };
    }

    private static WireDecodeResult<SimOrderArg> DecodeObjectId(ref WireReader reader)
    {
        if (!reader.TryReadUInt32(out var value))
        {
            return WireDecodeResult<SimOrderArg>.Fail(WireDecodeStatus.UnexpectedEndOfData);
        }

        return WireDecodeResult<SimOrderArg>.Ok(SimOrderArg.FromObjectId(value));
    }

    private static WireDecodeResult<SimOrderArg> DecodeUnsigned(ref WireReader reader)
    {
        if (!reader.TryReadUInt32(out var value))
        {
            return WireDecodeResult<SimOrderArg>.Fail(WireDecodeStatus.UnexpectedEndOfData);
        }

        return WireDecodeResult<SimOrderArg>.Ok(SimOrderArg.FromUnsigned(value));
    }

    private static WireDecodeResult<SimOrderArg> DecodePosition(ref WireReader reader)
    {
        if (!reader.TryReadUInt32(out var xBits))
        {
            return WireDecodeResult<SimOrderArg>.Fail(WireDecodeStatus.UnexpectedEndOfData);
        }

        if (!reader.TryReadUInt32(out var yBits))
        {
            return WireDecodeResult<SimOrderArg>.Fail(WireDecodeStatus.UnexpectedEndOfData);
        }

        if (!reader.TryReadUInt32(out var zBits))
        {
            return WireDecodeResult<SimOrderArg>.Fail(WireDecodeStatus.UnexpectedEndOfData);
        }

        if (IsNaNBits(xBits) || IsNaNBits(yBits) || IsNaNBits(zBits))
        {
            return WireDecodeResult<SimOrderArg>.Fail(WireDecodeStatus.MalformedWireFloat);
        }

        return WireDecodeResult<SimOrderArg>.Ok(SimOrderArg.FromWirePosition(xBits, yBits, zBits));
    }

    private static WireDecodeResult<SimOrderArg> DecodeScreenPosition(ref WireReader reader)
    {
        if (!reader.TryReadInt32(out var x))
        {
            return WireDecodeResult<SimOrderArg>.Fail(WireDecodeStatus.UnexpectedEndOfData);
        }

        if (!reader.TryReadInt32(out var y))
        {
            return WireDecodeResult<SimOrderArg>.Fail(WireDecodeStatus.UnexpectedEndOfData);
        }

        return WireDecodeResult<SimOrderArg>.Ok(SimOrderArg.FromScreenPosition(x, y));
    }

    private static WireDecodeResult<SimOrderArg> DecodeScreenRectangle(ref WireReader reader)
    {
        if (!reader.TryReadInt32(out var x0))
        {
            return WireDecodeResult<SimOrderArg>.Fail(WireDecodeStatus.UnexpectedEndOfData);
        }

        if (!reader.TryReadInt32(out var y0))
        {
            return WireDecodeResult<SimOrderArg>.Fail(WireDecodeStatus.UnexpectedEndOfData);
        }

        if (!reader.TryReadInt32(out var x1))
        {
            return WireDecodeResult<SimOrderArg>.Fail(WireDecodeStatus.UnexpectedEndOfData);
        }

        if (!reader.TryReadInt32(out var y1))
        {
            return WireDecodeResult<SimOrderArg>.Fail(WireDecodeStatus.UnexpectedEndOfData);
        }

        return WireDecodeResult<SimOrderArg>.Ok(SimOrderArg.FromScreenRectangle(x0, y0, x1, y1));
    }

    private static bool TryGetKnownKind(byte kindByte, out SimOrderArgKind kind)
    {
        kind = (SimOrderArgKind)kindByte;
        return kind is SimOrderArgKind.Integer
            or SimOrderArgKind.Fixed
            or SimOrderArgKind.Boolean
            or SimOrderArgKind.ObjectId
            or SimOrderArgKind.Unsigned
            or SimOrderArgKind.Position
            or SimOrderArgKind.ScreenPosition
            or SimOrderArgKind.ScreenRectangle
            or SimOrderArgKind.Raw9
            or SimOrderArgKind.Raw10;
    }

    /// <summary>
    /// Mirrors the NaN test <c>Fix64.FromWireFloat</c> itself applies (exponent all-ones,
    /// mantissa nonzero) so this codec can reject the bit pattern before calling it, rather
    /// than relying on catching the <see cref="ArgumentException"/> that method throws for
    /// NaN input - malformed wire input must fail as a typed <see cref="WireDecodeStatus"/>,
    /// not an exception (F4/F6). Plain IEEE-754 bit layout, not a Ghidra/binary-derived fact.
    /// </summary>
    private static bool IsNaNBits(uint bits)
    {
        var biasedExponent = (bits >> 23) & 0xFF;
        var mantissa = bits & 0x7FFFFF;
        return biasedExponent == 0xFF && mantissa != 0;
    }
}
