// R15 bridge P4a (dr-0039, packet BR-P4A): legacy Order -> SimCore SimOrder conversion.
//
// The float-free boundary (design-simcore-scaffolding §4.3, SimOrder.cs's own header): once an
// order crosses into SimCore.Orders.SimOrder, no float32 may appear anywhere on it again - wire
// float32 payloads (OrderArgumentType.Float and the three components of .Position) enter ONLY
// as their raw IEEE-754 bit pattern, quantized via the blessed F4 boundary
// Fix64.FromWireFloat/SimOrderArg.FromWireFloat/FromWirePosition, identically on every peer.
// This file is the one place a legacy `Order`'s float fields are read at all; every float
// crossing below goes through BitConverter.SingleToUInt32Bits(...) into FromWireFloat /
// FromWirePosition and nothing else - never an implicit or explicit cast to Fix64, never
// Fix64.FromFloat or a raw constructor.
//
// This converter only runs for OrderTypes that OrderIdentityMap actually maps (TryConvert
// fails cleanly, as OrderConversionStatus.Unmapped, otherwise) - IOrderSubmitter's contract is
// what routes an Unmapped result back to the legacy local dispatch path instead.

using System;
using OpenSage.SimCore.Orders;

namespace OpenSage.Logic.Orders;

public enum OrderConversionStatus
{
    Ok = 0,

    /// <summary>OrderIdentityMap has no GameMessageType for this order's OrderType.</summary>
    Unmapped,

    /// <summary>
    /// The order carries an argument type SimOrderArgKind has no wire-safe counterpart for
    /// (OrderArgumentType.Unknown4/Unknown5/Unknown9/Unknown10 - the recovered wire table
    /// documents these byte widths but not their semantics, so there is nothing safe to
    /// construct a SimOrderArg from).
    /// </summary>
    UnmappedArgumentType,
}

/// <summary>
/// The outcome of <see cref="OrderConverter.TryConvert"/>: either the converted
/// <see cref="SimOrder"/>, or a typed reason it could not be converted.
/// </summary>
public readonly struct OrderConversionResult
{
    public OrderConversionStatus Status { get; }

    /// <summary>Non-null only when <see cref="Status"/> is <see cref="OrderConversionStatus.Ok"/>.</summary>
    public SimOrder Order { get; }

    public bool Success => Status == OrderConversionStatus.Ok;

    private OrderConversionResult(OrderConversionStatus status, SimOrder order)
    {
        Status = status;
        Order = order;
    }

    public static OrderConversionResult Ok(SimOrder order) =>
        new(OrderConversionStatus.Ok, order ?? throw new ArgumentNullException(nameof(order)));

    public static OrderConversionResult Failure(OrderConversionStatus status)
    {
        if (status == OrderConversionStatus.Ok)
        {
            throw new ArgumentException("Ok is not a failure status.", nameof(status));
        }
        return new OrderConversionResult(status, null);
    }
}

/// <summary>
/// Converts a legacy <see cref="Order"/> into a SimCore <see cref="SimOrder"/> through
/// <see cref="OrderIdentityMap"/>. Stateless; safe to call from anywhere.
/// </summary>
public static class OrderConverter
{
    public static OrderConversionResult TryConvert(Order legacyOrder)
    {
        ArgumentNullException.ThrowIfNull(legacyOrder);

        if (!OrderIdentityMap.TryGetGameMessageType(legacyOrder.OrderType, out var messageType))
        {
            return OrderConversionResult.Failure(OrderConversionStatus.Unmapped);
        }

        var simOrder = new SimOrder(messageType, legacyOrder.PlayerIndex);

        foreach (var argument in legacyOrder.Arguments)
        {
            if (!TryConvertArgument(argument, out var simArgument))
            {
                return OrderConversionResult.Failure(OrderConversionStatus.UnmappedArgumentType);
            }

            simOrder.AddArgument(simArgument);
        }

        return OrderConversionResult.Ok(simOrder);
    }

    private static bool TryConvertArgument(OrderArgument argument, out SimOrderArg converted)
    {
        switch (argument.ArgumentType)
        {
            case OrderArgumentType.Integer:
                converted = SimOrderArg.FromInteger(argument.Value.Integer);
                return true;

            case OrderArgumentType.Float:
                // The F4 boundary: the ONLY legal way a float32 crosses into sim state.
                converted = SimOrderArg.FromWireFloat(
                    BitConverter.SingleToUInt32Bits(argument.Value.Float));
                return true;

            case OrderArgumentType.Boolean:
                converted = SimOrderArg.FromBoolean(argument.Value.Boolean);
                return true;

            case OrderArgumentType.ObjectId:
                converted = SimOrderArg.FromObjectId(argument.Value.ObjectId.Index);
                return true;

            case OrderArgumentType.Position:
                var position = argument.Value.Position;
                // Same F4 boundary, applied to each of the three Coord3D components.
                converted = SimOrderArg.FromWirePosition(
                    BitConverter.SingleToUInt32Bits(position.X),
                    BitConverter.SingleToUInt32Bits(position.Y),
                    BitConverter.SingleToUInt32Bits(position.Z));
                return true;

            case OrderArgumentType.ScreenPosition:
                var screenPosition = argument.Value.ScreenPosition;
                converted = SimOrderArg.FromScreenPosition(screenPosition.X, screenPosition.Y);
                return true;

            case OrderArgumentType.ScreenRectangle:
                var rectangle = argument.Value.ScreenRectangle;
                converted = SimOrderArg.FromScreenRectangle(
                    rectangle.X, rectangle.Y, rectangle.Right, rectangle.Bottom);
                return true;

            default:
                // Unknown4/Unknown5/Unknown9/Unknown10: byte width recovered, semantics not -
                // nothing safe to construct. Never guessed (this file's header).
                converted = default;
                return false;
        }
    }
}
