// The Orders packet (design-netcode.md §3.1: "{ frame, SimOrder[] }", "any -> all (via host),
// every logic frame, per peer, even when empty" - the lockstep payload).
//
// §3.1 makes the empty-frame case mandatory, not an optimisation: "an Orders packet with zero
// orders is a required heartbeat" that lets NetTransport's arrival barrier be a simple count of
// one packet per peer per frame. This type reflects that in its wire shape directly - the
// order count is always written and read as an explicit field, so a zero-order packet still
// encodes to a real, non-empty, self-describing byte sequence (frame + count=0), never to
// "nothing". Decoding zero bytes at all is a distinct, ordinary UnexpectedEndOfData failure,
// not the same thing as decoding a valid empty-orders packet.
//
// Payload layout (inside the WireFrame envelope): uint32 LogicFrame value, uint16 order count,
// then that many SimOrderCodec-encoded orders back to back.

using System;
using System.Collections.Generic;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Network.Wire;

public sealed class OrdersPacket
{
    public LogicFrame Frame { get; }

    public IReadOnlyList<SimOrder> Orders { get; }

    public OrdersPacket(LogicFrame frame, IReadOnlyList<SimOrder> orders)
    {
        ArgumentNullException.ThrowIfNull(orders);

        if (orders.Count > WireLimits.MaxOrdersPerPacket)
        {
            throw new ArgumentException(
                $"Packet carries {orders.Count} orders, over the wire cap of {WireLimits.MaxOrdersPerPacket}.",
                nameof(orders));
        }

        Frame = frame;
        Orders = orders;
    }

    /// <summary>
    /// Encodes the full wire frame, including the <see cref="WireFrame"/> envelope. The order
    /// count is always written explicitly - this always produces a real, decodable byte
    /// sequence, even for a zero-order (heartbeat) packet.
    /// </summary>
    public byte[] Encode(byte senderPlayerIndex)
    {
        var payload = new WireWriter();
        payload.WriteUInt32(Frame.Value);
        payload.WriteUInt16((ushort)Orders.Count); // safe: ctor already capped Orders.Count

        foreach (var order in Orders)
        {
            SimOrderCodec.Encode(payload, order);
        }

        return WireFrame.Encode(WireProtocolVersion.Current, senderPlayerIndex, payload.WrittenSpan);
    }

    /// <summary>
    /// Decodes a full wire frame produced by <see cref="Encode"/>. Never throws on malformed
    /// input; returns a failing <see cref="WireDecodeResult{T}"/> instead, including for a
    /// protocol-version mismatch (<see cref="WireDecodeStatus.UnsupportedProtocolVersion"/>).
    /// </summary>
    public static WireDecodeResult<OrdersPacket> TryDecode(ReadOnlySpan<byte> data, out byte senderPlayerIndex)
    {
        var frameStatus = WireFrame.TryDecode(data, out var protocolVersion, out senderPlayerIndex, out var payload);
        if (frameStatus != WireDecodeStatus.Success)
        {
            return WireDecodeResult<OrdersPacket>.Fail(frameStatus);
        }

        if (protocolVersion != WireProtocolVersion.Current)
        {
            return WireDecodeResult<OrdersPacket>.Fail(WireDecodeStatus.UnsupportedProtocolVersion);
        }

        var reader = new WireReader(payload);

        if (!reader.TryReadUInt32(out var frameValue))
        {
            return WireDecodeResult<OrdersPacket>.Fail(WireDecodeStatus.UnexpectedEndOfData);
        }

        if (!reader.TryReadUInt16(out var orderCount))
        {
            return WireDecodeResult<OrdersPacket>.Fail(WireDecodeStatus.UnexpectedEndOfData);
        }

        if (orderCount > WireLimits.MaxOrdersPerPacket)
        {
            return WireDecodeResult<OrdersPacket>.Fail(WireDecodeStatus.OrderCountOverflow);
        }

        var orders = new List<SimOrder>(orderCount);
        for (var i = 0; i < orderCount; i++)
        {
            var orderResult = SimOrderCodec.Decode(ref reader);
            if (!orderResult.Success)
            {
                return WireDecodeResult<OrdersPacket>.Fail(orderResult.Status);
            }

            orders.Add(orderResult.Value);
        }

        return WireDecodeResult<OrdersPacket>.Ok(new OrdersPacket(new LogicFrame(frameValue), orders));
    }
}
