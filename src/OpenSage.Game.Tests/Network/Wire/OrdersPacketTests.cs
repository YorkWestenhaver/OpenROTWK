using System;
using System.Collections.Generic;
using OpenSage.Network.Wire;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Network.Wire.Tests;

public class OrdersPacketTests
{
    [Fact]
    public void MultipleOrders_RoundTrip()
    {
        var order1 = new SimOrder(GameMessageType.MSG_DO_STOP, playerIndex: 0);
        var order2 = new SimOrder(GameMessageType.MSG_DO_MOVETO, playerIndex: 2);
        order2.AddArgument(SimOrderArg.FromObjectId(9));

        var packet = new OrdersPacket(new LogicFrame(42), new List<SimOrder> { order1, order2 });

        var bytes = packet.Encode(senderPlayerIndex: 5);
        var result = OrdersPacket.TryDecode(bytes, out var senderPlayerIndex);

        Assert.True(result.Success);
        Assert.Equal((byte)5, senderPlayerIndex);
        Assert.Equal(42u, result.Value.Frame.Value);
        Assert.Equal(2, result.Value.Orders.Count);
        Assert.Equal(GameMessageType.MSG_DO_STOP, result.Value.Orders[0].Type);
        Assert.Equal(GameMessageType.MSG_DO_MOVETO, result.Value.Orders[1].Type);
        Assert.Equal(9u, result.Value.Orders[1].Arguments[0].ObjectId);
    }

    /// <summary>
    /// design-netcode.md §3.1: "an Orders packet with zero orders is a required heartbeat" -
    /// this must produce real, decodable bytes, not "nothing" (an empty byte array, or a
    /// packet that only round-trips when it has content).
    /// </summary>
    [Fact]
    public void ZeroOrderPacket_EncodesToRealBytes_AndRoundTrips()
    {
        var packet = new OrdersPacket(new LogicFrame(7), Array.Empty<SimOrder>());

        var bytes = packet.Encode(senderPlayerIndex: 1);

        Assert.NotEmpty(bytes);

        var result = OrdersPacket.TryDecode(bytes, out var senderPlayerIndex);

        Assert.True(result.Success);
        Assert.Equal((byte)1, senderPlayerIndex);
        Assert.Equal(7u, result.Value.Frame.Value);
        Assert.Empty(result.Value.Orders);
    }

    [Fact]
    public void TryDecode_TrulyEmptyInput_IsADistinctFailure_FromAZeroOrderPacket()
    {
        var result = OrdersPacket.TryDecode(Array.Empty<byte>(), out _);

        Assert.False(result.Success);
        Assert.Equal(WireDecodeStatus.UnexpectedEndOfData, result.Status);
    }

    [Fact]
    public void TryDecode_ProtocolVersionMismatch_ReturnsTypedFailure_NeverThrows()
    {
        var packet = new OrdersPacket(new LogicFrame(1), Array.Empty<SimOrder>());
        var bytes = packet.Encode(senderPlayerIndex: 0);

        // Corrupt the ProtocolVersion header field (first two bytes, little-endian) to a
        // value this build does not speak.
        bytes[0] = 0xFF;
        bytes[1] = 0xFF;

        var result = OrdersPacket.TryDecode(bytes, out _);

        Assert.False(result.Success);
        Assert.Equal(WireDecodeStatus.UnsupportedProtocolVersion, result.Status);
    }

    [Fact]
    public void TryDecode_OrderCountOverflow_ReturnsTypedFailure_NeverThrows()
    {
        var payload = new WireWriter();
        payload.WriteUInt32(1);
        payload.WriteUInt16(60000); // over WireLimits.MaxOrdersPerPacket (1024)

        var bytes = WireFrame.Encode(WireProtocolVersion.Current, senderPlayerIndex: 0, payload.WrittenSpan);
        var result = OrdersPacket.TryDecode(bytes, out _);

        Assert.False(result.Success);
        Assert.Equal(WireDecodeStatus.OrderCountOverflow, result.Status);
    }

    [Fact]
    public void Constructor_TooManyOrders_ThrowsArgumentException()
    {
        var orders = new List<SimOrder>();
        for (var i = 0; i < 1025; i++)
        {
            orders.Add(new SimOrder(GameMessageType.MSG_DO_STOP, playerIndex: 0));
        }

        Assert.Throws<ArgumentException>(() => new OrdersPacket(new LogicFrame(0), orders));
    }

    [Fact]
    public void TryDecode_TruncatedPayload_ReturnsUnexpectedEndOfData_NeverThrows()
    {
        var order = new SimOrder(GameMessageType.MSG_DO_STOP, playerIndex: 0);
        var packet = new OrdersPacket(new LogicFrame(3), new List<SimOrder> { order });
        var fullBytes = packet.Encode(senderPlayerIndex: 0);

        for (var length = 7; length < fullBytes.Length; length++)
        {
            var truncated = fullBytes.AsSpan(0, length).ToArray();
            var result = OrdersPacket.TryDecode(truncated, out _);

            Assert.False(result.Success);
            Assert.Equal(WireDecodeStatus.UnexpectedEndOfData, result.Status);
        }
    }
}
