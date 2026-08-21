// Wire codec for OpenSage.SimCore.Orders.SimOrder (task N2).
//
// Layout: int32 GameMessageType value, int32 PlayerIndex, byte argument count, then that many
// SimOrderArgCodec-encoded arguments back to back. The GameMessageType value is validated
// against GameMessageTypes.IsKnown BEFORE constructing a SimOrder, because SimOrder's own
// constructor throws MalformedOrderException on an unknown value (F6) - decode must never let
// that exception escape, so the check happens here, first.

using System;
using OpenSage.SimCore.Orders;

namespace OpenSage.Network.Wire;

internal static class SimOrderCodec
{
    public static void Encode(WireWriter writer, SimOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        var arguments = order.Arguments;
        if (arguments.Count > WireLimits.MaxArgumentsPerOrder)
        {
            throw new ArgumentException(
                $"Order carries {arguments.Count} arguments, over the wire cap of {WireLimits.MaxArgumentsPerOrder}.",
                nameof(order));
        }

        writer.WriteInt32((int)order.Type);
        writer.WriteInt32(order.PlayerIndex);
        writer.WriteByte((byte)arguments.Count);

        foreach (var argument in arguments)
        {
            SimOrderArgCodec.Encode(writer, argument);
        }
    }

    public static WireDecodeResult<SimOrder> Decode(ref WireReader reader)
    {
        if (!reader.TryReadInt32(out var typeValue))
        {
            return WireDecodeResult<SimOrder>.Fail(WireDecodeStatus.UnexpectedEndOfData);
        }

        if (!reader.TryReadInt32(out var playerIndex))
        {
            return WireDecodeResult<SimOrder>.Fail(WireDecodeStatus.UnexpectedEndOfData);
        }

        if (!reader.TryReadByte(out var argumentCount))
        {
            return WireDecodeResult<SimOrder>.Fail(WireDecodeStatus.UnexpectedEndOfData);
        }

        if (argumentCount > WireLimits.MaxArgumentsPerOrder)
        {
            return WireDecodeResult<SimOrder>.Fail(WireDecodeStatus.ArgumentCountOverflow);
        }

        var type = (GameMessageType)typeValue;
        if (!GameMessageTypes.IsKnown(type))
        {
            return WireDecodeResult<SimOrder>.Fail(WireDecodeStatus.UnknownMessageType);
        }

        // Safe now: IsKnown was checked above, so the SimOrder constructor cannot throw.
        var order = new SimOrder(type, playerIndex);

        for (var i = 0; i < argumentCount; i++)
        {
            var argumentResult = SimOrderArgCodec.Decode(ref reader);
            if (!argumentResult.Success)
            {
                return WireDecodeResult<SimOrder>.Fail(argumentResult.Status);
            }

            order.AddArgument(argumentResult.Value);
        }

        return WireDecodeResult<SimOrder>.Ok(order);
    }
}
