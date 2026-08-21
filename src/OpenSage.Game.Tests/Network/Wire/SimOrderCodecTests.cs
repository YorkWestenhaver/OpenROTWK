using System;
using OpenSage.Network.Wire;
using OpenSage.SimCore.Orders;
using Xunit;

namespace OpenSage.Network.Wire.Tests;

public class SimOrderCodecTests
{
    private static byte[] EncodeOne(SimOrder order)
    {
        var writer = new WireWriter();
        SimOrderCodec.Encode(writer, order);
        return writer.ToArray();
    }

    private static WireDecodeResult<SimOrder> DecodeOne(byte[] bytes)
    {
        var reader = new WireReader(bytes);
        return SimOrderCodec.Decode(ref reader);
    }

    [Fact]
    public void ZeroArgumentOrder_RoundTrips()
    {
        var order = new SimOrder(GameMessageType.MSG_DO_STOP, playerIndex: 3);

        var result = DecodeOne(EncodeOne(order));

        Assert.True(result.Success);
        Assert.Equal(GameMessageType.MSG_DO_STOP, result.Value.Type);
        Assert.Equal(3, result.Value.PlayerIndex);
        Assert.Empty(result.Value.Arguments);
    }

    [Fact]
    public void MixedArgumentOrder_RoundTrips()
    {
        var order = new SimOrder(GameMessageType.MSG_DO_MOVETO, playerIndex: 1);
        order.AddArgument(SimOrderArg.FromObjectId(77));
        order.AddArgument(SimOrderArg.FromWirePosition(
            BitConverter.SingleToUInt32Bits(10.5f),
            BitConverter.SingleToUInt32Bits(0f),
            BitConverter.SingleToUInt32Bits(-20.25f)));
        order.AddArgument(SimOrderArg.FromBoolean(true));

        var result = DecodeOne(EncodeOne(order));

        Assert.True(result.Success);
        var decoded = result.Value;
        Assert.Equal(GameMessageType.MSG_DO_MOVETO, decoded.Type);
        Assert.Equal(1, decoded.PlayerIndex);
        Assert.Equal(3, decoded.Arguments.Count);
        Assert.Equal(SimOrderArgKind.ObjectId, decoded.Arguments[0].Kind);
        Assert.Equal(77u, decoded.Arguments[0].ObjectId);
        Assert.Equal(SimOrderArgKind.Position, decoded.Arguments[1].Kind);
        Assert.Equal(SimOrderArgKind.Boolean, decoded.Arguments[2].Kind);
        Assert.True(decoded.Arguments[2].Boolean);
    }

    [Fact]
    public void PlayerIndex_RoundTripsAcrossFullIntRange()
    {
        foreach (var playerIndex in new[] { int.MinValue, -1, 0, 7, int.MaxValue })
        {
            var order = new SimOrder(GameMessageType.MSG_DO_STOP, playerIndex);
            var result = DecodeOne(EncodeOne(order));

            Assert.True(result.Success);
            Assert.Equal(playerIndex, result.Value.PlayerIndex);
        }
    }

    // ---- Malformed input: never throws, always a typed failure --------------------------

    [Fact]
    public void Decode_UnknownGameMessageTypeHole_ReturnsTypedFailure_NeverThrows()
    {
        // 1 is a documented hole in GameMessageType (between MSG_INVALID=0 and
        // MSG_RAW_MOUSE_BEGIN=2). The naive path (constructing a SimOrder directly) would
        // throw MalformedOrderException here; decode must intercept it first.
        var writer = new WireWriter();
        writer.WriteInt32(1);
        writer.WriteInt32(0);
        writer.WriteByte(0);

        var result = DecodeOne(writer.ToArray());

        Assert.False(result.Success);
        Assert.Equal(WireDecodeStatus.UnknownMessageType, result.Status);
    }

    [Fact]
    public void Decode_ArgumentCountOverflow_ReturnsTypedFailure_NeverThrows()
    {
        var writer = new WireWriter();
        writer.WriteInt32((int)GameMessageType.MSG_DO_STOP);
        writer.WriteInt32(0);
        writer.WriteByte(255); // over WireLimits.MaxArgumentsPerOrder (64), and the buffer
                               // doesn't remotely contain 255 arguments' worth of data either

        var result = DecodeOne(writer.ToArray());

        Assert.False(result.Success);
        Assert.Equal(WireDecodeStatus.ArgumentCountOverflow, result.Status);
    }

    [Fact]
    public void Encode_TooManyArguments_ThrowsArgumentException()
    {
        // Not wire input - a program error building an oversized SimOrder locally. This is
        // the one place the codec is allowed to throw: it's caller misuse of an in-memory
        // API, not untrusted wire bytes.
        var order = new SimOrder(GameMessageType.MSG_DO_STOP, playerIndex: 0);
        for (var i = 0; i < 65; i++)
        {
            order.AddArgument(SimOrderArg.FromBoolean(true));
        }

        var writer = new WireWriter();
        Assert.Throws<ArgumentException>(() => SimOrderCodec.Encode(writer, order));
    }

    [Fact]
    public void Decode_TruncatedAtEachField_ReturnsUnexpectedEndOfData_NeverThrows()
    {
        var full = new WireWriter();
        full.WriteInt32((int)GameMessageType.MSG_DO_STOP);
        full.WriteInt32(0);
        full.WriteByte(1);
        SimOrderArgCodec.Encode(full, SimOrderArg.FromBoolean(true));
        var fullBytes = full.ToArray();

        // Truncate at every prefix strictly shorter than the full encoding (except length 0,
        // covered separately) and confirm decode fails cleanly rather than reading garbage.
        for (var length = 1; length < fullBytes.Length; length++)
        {
            var truncated = fullBytes.AsSpan(0, length).ToArray();
            var result = DecodeOne(truncated);

            Assert.False(result.Success);
            Assert.Equal(WireDecodeStatus.UnexpectedEndOfData, result.Status);
        }
    }

    [Fact]
    public void Decode_EmptyBuffer_ReturnsUnexpectedEndOfData_NeverThrows()
    {
        var result = DecodeOne(Array.Empty<byte>());

        Assert.False(result.Success);
        Assert.Equal(WireDecodeStatus.UnexpectedEndOfData, result.Status);
    }
}
