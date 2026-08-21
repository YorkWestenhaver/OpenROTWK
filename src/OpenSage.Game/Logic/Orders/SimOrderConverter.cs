// R15 bridge P4b (dr-0039, packet BR-P4B): SimCore SimOrder -> legacy Order conversion.
//
// The exact mirror of BR-P4A's OrderConverter (Order -> SimOrder), and the second half of the
// one order pipe: everything a headed game executes now leaves OrderIngest as a ScheduledOrder
// and comes back through here before OrderProcessor runs it. Kept in its own file rather than
// bolted onto OrderConverter so P4a's file stays exactly as its packet cited it.
//
// THE FLOAT BOUNDARY, in the outbound direction. SimOrderArg.Fixed/Position hold Fix64 values
// that got there through Fix64.FromWireFloat(bits) - the F4 ingestion boundary - and the legacy
// Order/OrderProcessor surface is float-typed. Recovering the float uses Fix64's other blessed
// F4 escape, ToFloatForDisplay(), exactly as Network/Wire/SimOrderArgCodec.cs already does for
// the same round trip and for the same reason: a Fix64 that originated from FromWireFloat is
// exactly representable in float32 (Q31.32 carries far more precision than a 24-bit mantissa
// inside FromWireFloat's non-saturating range), so ToFloatForDisplay's round-to-nearest returns
// the identical value. No new float -> Fix64 path is introduced here, and nothing this function
// produces re-enters sim state as a Fix64 - it re-enters the LEGACY float-typed dispatcher,
// which is the pre-SimCore path P4b is bridging, not sim substrate.
//
// The consequence worth stating plainly: an order that crosses this boundary carries the
// QUANTIZED value, not the original mouse-pick float. That is the point. Both peers quantize
// identically at ingestion, so both peers dispatch identical values - which the 0-frame,
// never-quantized legacy path could not promise.
//
// Unmapped GameMessageType is not an error here: OrderIdentityMap maps 61 of the recovered 381
// message types, so a scheduled order can legitimately arrive with a type the legacy dispatcher
// has no OrderType for. TryConvertBack fails cleanly and the caller logs and skips it
// (HeadedSimSystems.DispatchOrder) - never guesses, never casts one enum to the other
// (L2-plan #2: the two numberings collide at identical integers with different meanings).

using System;
using OpenSage.Mathematics;
using OpenSage.SimCore.Orders;

namespace OpenSage.Logic.Orders;

/// <summary>
/// Converts a SimCore <see cref="SimOrder"/> back into the legacy <see cref="Order"/> shape
/// <see cref="OrderProcessor"/> executes. Stateless; safe to call from anywhere.
/// </summary>
public static class SimOrderConverter
{
    /// <summary>
    /// Converts <paramref name="simOrder"/> back to a legacy order.
    /// </summary>
    /// <returns>
    /// <c>false</c> if <see cref="OrderIdentityMap"/> has no <see cref="OrderType"/> for the
    /// order's message type, or if it carries an argument kind the legacy shape has no
    /// counterpart for (<see cref="SimOrderArgKind.Unsigned"/>,
    /// <see cref="SimOrderArgKind.Raw9"/>, <see cref="SimOrderArgKind.Raw10"/> - the recovered
    /// wire table gives their byte width but not their semantics). Both are "log and skip",
    /// not "crash": see this file's header.
    /// </returns>
    public static bool TryConvertBack(SimOrder simOrder, out Order legacyOrder)
    {
        ArgumentNullException.ThrowIfNull(simOrder);

        if (!OrderIdentityMap.TryGetOrderType(simOrder.Type, out var orderType))
        {
            legacyOrder = null;
            return false;
        }

        var order = new Order(simOrder.PlayerIndex, orderType);

        for (var i = 0; i < simOrder.Arguments.Count; i++)
        {
            if (!TryAddArgument(order, simOrder.Arguments[i]))
            {
                legacyOrder = null;
                return false;
            }
        }

        legacyOrder = order;
        return true;
    }

    private static bool TryAddArgument(Order order, in SimOrderArg argument)
    {
        switch (argument.Kind)
        {
            case SimOrderArgKind.Integer:
                order.AddIntegerArgument(argument.Integer);
                return true;

            case SimOrderArgKind.Fixed:
                // The F4 display escape, used as SimOrderArgCodec uses it: recovering the
                // float32 a FromWireFloat-originated Fix64 came from, exactly.
                order.AddFloatArgument(argument.Fixed.ToFloatForDisplay());
                return true;

            case SimOrderArgKind.Boolean:
                order.AddBooleanArgument(argument.Boolean);
                return true;

            case SimOrderArgKind.ObjectId:
                order.AddObjectIdArgument(new ObjectId(argument.ObjectId));
                return true;

            case SimOrderArgKind.Position:
                order.AddPositionArgument(new System.Numerics.Vector3(
                    argument.Position.X.ToFloatForDisplay(),
                    argument.Position.Y.ToFloatForDisplay(),
                    argument.Position.Z.ToFloatForDisplay()));
                return true;

            case SimOrderArgKind.ScreenPosition:
                order.AddScreenPositionArgument(new Point2D(argument.X0, argument.Y0));
                return true;

            case SimOrderArgKind.ScreenRectangle:
                // OrderConverter wrote (X, Y, Right, Bottom) into (X0, Y0, X1, Y1); the two
                // corners are what Rectangle.FromCorners takes back.
                order.AddScreenRectangleArgument(Rectangle.FromCorners(
                    new Point2D(argument.X0, argument.Y0),
                    new Point2D(argument.X1, argument.Y1)));
                return true;

            default:
                // Unsigned/Raw9/Raw10: no legacy OrderArgumentType counterpart, and nothing
                // safe to guess. Never fabricated.
                return false;
        }
    }
}
