using System;
using System.Collections.Generic;
using OpenSage.Network.Wire;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Orders;
using Xunit;

namespace OpenSage.Network.Wire.Tests;

public class SimOrderArgCodecTests
{
    private static byte[] EncodeOne(in SimOrderArg arg)
    {
        var writer = new WireWriter();
        SimOrderArgCodec.Encode(writer, arg);
        return writer.ToArray();
    }

    private static WireDecodeResult<SimOrderArg> DecodeOne(byte[] bytes)
    {
        var reader = new WireReader(bytes);
        return SimOrderArgCodec.Decode(ref reader);
    }

    private static void AssertRoundTrips(in SimOrderArg arg)
    {
        var bytes = EncodeOne(arg);
        var result = DecodeOne(bytes);

        Assert.True(result.Success);
        var decoded = result.Value;

        Assert.Equal(arg.Kind, decoded.Kind);
        switch (arg.Kind)
        {
            case SimOrderArgKind.Integer:
                Assert.Equal(arg.Integer, decoded.Integer);
                break;
            case SimOrderArgKind.Fixed:
                Assert.Equal(arg.Fixed, decoded.Fixed);
                break;
            case SimOrderArgKind.Boolean:
                Assert.Equal(arg.Boolean, decoded.Boolean);
                break;
            case SimOrderArgKind.ObjectId:
                Assert.Equal(arg.ObjectId, decoded.ObjectId);
                break;
            case SimOrderArgKind.Unsigned:
                Assert.Equal(arg.Unsigned, decoded.Unsigned);
                break;
            case SimOrderArgKind.Position:
                Assert.Equal(arg.Position.X, decoded.Position.X);
                Assert.Equal(arg.Position.Y, decoded.Position.Y);
                Assert.Equal(arg.Position.Z, decoded.Position.Z);
                break;
            case SimOrderArgKind.ScreenPosition:
                Assert.Equal(arg.X0, decoded.X0);
                Assert.Equal(arg.Y0, decoded.Y0);
                break;
            case SimOrderArgKind.ScreenRectangle:
                Assert.Equal(arg.X0, decoded.X0);
                Assert.Equal(arg.Y0, decoded.Y0);
                Assert.Equal(arg.X1, decoded.X1);
                Assert.Equal(arg.Y1, decoded.Y1);
                break;
            default:
                throw new InvalidOperationException($"Test does not cover kind {arg.Kind}.");
        }
    }

    // ---- Exhaustive per-constructible-kind round trip -----------------------------------

    [Fact]
    public void Integer_RoundTrips()
    {
        AssertRoundTrips(SimOrderArg.FromInteger(0));
        AssertRoundTrips(SimOrderArg.FromInteger(int.MinValue));
        AssertRoundTrips(SimOrderArg.FromInteger(int.MaxValue));
        AssertRoundTrips(SimOrderArg.FromInteger(-12345));
    }

    [Fact]
    public void Boolean_RoundTrips()
    {
        AssertRoundTrips(SimOrderArg.FromBoolean(true));
        AssertRoundTrips(SimOrderArg.FromBoolean(false));
    }

    [Fact]
    public void ObjectId_RoundTrips()
    {
        AssertRoundTrips(SimOrderArg.FromObjectId(0));
        AssertRoundTrips(SimOrderArg.FromObjectId(uint.MaxValue));
        AssertRoundTrips(SimOrderArg.FromObjectId(42));
    }

    [Fact]
    public void Unsigned_RoundTrips()
    {
        AssertRoundTrips(SimOrderArg.FromUnsigned(0));
        AssertRoundTrips(SimOrderArg.FromUnsigned(uint.MaxValue));
    }

    [Fact]
    public void ScreenPosition_RoundTrips()
    {
        AssertRoundTrips(SimOrderArg.FromScreenPosition(0, 0));
        AssertRoundTrips(SimOrderArg.FromScreenPosition(int.MinValue, int.MaxValue));
        AssertRoundTrips(SimOrderArg.FromScreenPosition(-640, 480));
    }

    [Fact]
    public void ScreenRectangle_RoundTrips()
    {
        AssertRoundTrips(SimOrderArg.FromScreenRectangle(0, 0, 0, 0));
        AssertRoundTrips(SimOrderArg.FromScreenRectangle(int.MinValue, int.MaxValue, -1, 1));
    }

    [Theory]
    [MemberData(nameof(WireFloatCorpus))]
    public void Fixed_RoundTrips_OverSeededCorpus(uint ieeeBits)
    {
        AssertRoundTrips(SimOrderArg.FromWireFloat(ieeeBits));
    }

    [Theory]
    [MemberData(nameof(WireFloatCorpus))]
    public void Position_RoundTrips_OverSeededCorpus(uint ieeeBits)
    {
        // Exercise all three components together with the same corpus value plus two
        // deliberately different ones, so X/Y/Z can't be silently swapped by the codec.
        AssertRoundTrips(SimOrderArg.FromWirePosition(ieeeBits, BitConverter.SingleToUInt32Bits(1.5f), BitConverter.SingleToUInt32Bits(-2.25f)));
    }

    /// <summary>
    /// A property-style corpus of "interesting" float32 bit patterns: zero, both signs of one
    /// and a fraction, denormals (including the smallest nonzero magnitude, which
    /// FromWireFloat truncates to Fix64.Zero - still a valid round trip of the *quantized*
    /// value), large-but-in-range magnitudes, and values right at Fix64's saturation edge.
    /// Deliberately excludes NaN bit patterns (covered separately as malformed input) and
    /// ±infinity (covered separately as a saturation case, not a round-trip case, since
    /// ToFloatForDisplay of the saturated Fix64 is finite, not infinite).
    /// </summary>
    public static IEnumerable<object[]> WireFloatCorpus()
    {
        float[] values =
        {
            0f, -0f, 1f, -1f, 0.5f, -0.5f, 1.5f, -2.25f, 100f, -100f,
            123456.789f, -123456.789f, float.Epsilon, -float.Epsilon,
            1e-10f, -1e-10f, 1e6f, -1e6f, 2147483000f, -2147483000f,
        };

        foreach (var value in values)
        {
            yield return new object[] { BitConverter.SingleToUInt32Bits(value) };
        }
    }

    [Fact]
    public void Fixed_PositiveAndNegativeInfinity_SaturateAndRoundTripStably()
    {
        // ±infinity saturates to Fix64.MinValue/MaxValue at FromWireFloat (documented, not a
        // round trip of the original bits) - but re-encoding *that* Fix64 and decoding again
        // must be a stable fixed point, since the codec is used to relay already-quantized
        // SimOrders (e.g. host redistribution), not just the first hop.
        var positiveInf = SimOrderArg.FromWireFloat(BitConverter.SingleToUInt32Bits(float.PositiveInfinity));
        var negativeInf = SimOrderArg.FromWireFloat(BitConverter.SingleToUInt32Bits(float.NegativeInfinity));

        Assert.Equal(Fix64.MaxValue, positiveInf.Fixed);
        Assert.Equal(Fix64.MinValue, negativeInf.Fixed);

        AssertRoundTrips(positiveInf);
        AssertRoundTrips(negativeInf);
    }

    // ---- Malformed input: never throws, always a typed failure --------------------------

    [Fact]
    public void Decode_UnknownArgKindByte_ReturnsTypedFailure_NeverThrows()
    {
        // 5 is the documented hole in SimOrderArgKind; 200 is nowhere near any real value.
        foreach (byte badKind in new byte[] { 5, 200, 255 })
        {
            var reader = new WireReader(new byte[] { badKind });
            var result = SimOrderArgCodec.Decode(ref reader);

            Assert.False(result.Success);
            Assert.Equal(WireDecodeStatus.UnknownArgKind, result.Status);
        }
    }

    [Fact]
    public void Decode_Raw9And10_ReturnUnconstructibleArgKind_NeverThrows()
    {
        foreach (var kind in new[] { SimOrderArgKind.Raw9, SimOrderArgKind.Raw10 })
        {
            var reader = new WireReader(new byte[] { (byte)kind, 0, 0, 0, 0 });
            var result = SimOrderArgCodec.Decode(ref reader);

            Assert.False(result.Success);
            Assert.Equal(WireDecodeStatus.UnconstructibleArgKind, result.Status);
        }
    }

    [Fact]
    public void Decode_InvalidBooleanByte_ReturnsTypedFailure_NeverThrows()
    {
        var reader = new WireReader(new[] { (byte)SimOrderArgKind.Boolean, (byte)2 });
        var result = SimOrderArgCodec.Decode(ref reader);

        Assert.False(result.Success);
        Assert.Equal(WireDecodeStatus.InvalidBooleanEncoding, result.Status);
    }

    [Fact]
    public void Decode_NaNWireFloat_ReturnsTypedFailure_NeverThrows()
    {
        var nanBits = BitConverter.SingleToUInt32Bits(float.NaN);
        var payload = new WireWriter();
        payload.WriteUInt32(nanBits);
        var bytes = new byte[1 + payload.Length];
        bytes[0] = (byte)SimOrderArgKind.Fixed;
        payload.WrittenSpan.CopyTo(bytes.AsSpan(1));

        var reader = new WireReader(bytes);
        var result = SimOrderArgCodec.Decode(ref reader);

        Assert.False(result.Success);
        Assert.Equal(WireDecodeStatus.MalformedWireFloat, result.Status);
    }

    [Fact]
    public void Decode_NaNInsidePosition_ReturnsTypedFailure_NeverThrows()
    {
        var okBits = BitConverter.SingleToUInt32Bits(1f);
        var nanBits = BitConverter.SingleToUInt32Bits(float.NaN);

        var payload = new WireWriter();
        payload.WriteUInt32(okBits);
        payload.WriteUInt32(okBits);
        payload.WriteUInt32(nanBits); // Z is the poisoned component
        var bytes = new byte[1 + payload.Length];
        bytes[0] = (byte)SimOrderArgKind.Position;
        payload.WrittenSpan.CopyTo(bytes.AsSpan(1));

        var reader = new WireReader(bytes);
        var result = SimOrderArgCodec.Decode(ref reader);

        Assert.False(result.Success);
        Assert.Equal(WireDecodeStatus.MalformedWireFloat, result.Status);
    }

    [Theory]
    [InlineData(SimOrderArgKind.Integer)]
    [InlineData(SimOrderArgKind.Fixed)]
    [InlineData(SimOrderArgKind.Boolean)]
    [InlineData(SimOrderArgKind.ObjectId)]
    [InlineData(SimOrderArgKind.Unsigned)]
    [InlineData(SimOrderArgKind.Position)]
    [InlineData(SimOrderArgKind.ScreenPosition)]
    [InlineData(SimOrderArgKind.ScreenRectangle)]
    public void Decode_TruncatedAfterKindByte_ReturnsUnexpectedEndOfData_NeverThrows(SimOrderArgKind kind)
    {
        // Every constructible kind has a nonzero payload, so a buffer holding only the kind
        // byte must fail as truncation, not succeed with zeroed fields.
        var reader = new WireReader(new[] { (byte)kind });
        var result = SimOrderArgCodec.Decode(ref reader);

        Assert.False(result.Success);
        Assert.Equal(WireDecodeStatus.UnexpectedEndOfData, result.Status);
    }

    [Fact]
    public void Decode_EmptyBuffer_ReturnsUnexpectedEndOfData_NeverThrows()
    {
        var reader = new WireReader(Array.Empty<byte>());
        var result = SimOrderArgCodec.Decode(ref reader);

        Assert.False(result.Success);
        Assert.Equal(WireDecodeStatus.UnexpectedEndOfData, result.Status);
    }
}
