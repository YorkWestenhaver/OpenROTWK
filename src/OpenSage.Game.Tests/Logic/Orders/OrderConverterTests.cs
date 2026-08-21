using System;
using System.Numerics;
using OpenSage.Logic.Orders;
using OpenSage.Mathematics;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Orders;
using Xunit;

namespace OpenSage.Tests.Logic.Orders;

public class OrderConverterTests
{
    [Fact]
    public void UnmappedOrderType_FailsWithUnmappedStatus()
    {
        // ToggleOvercharge is a deliberate ZH-deletion (no BFME2 equivalent) - this is the
        // exact "unmapped Local order" state IOrderSubmitter's fallback contract exists for.
        var order = new Order(playerIndex: 0, OrderType.ToggleOvercharge);

        var result = OrderConverter.TryConvert(order);

        Assert.False(result.Success);
        Assert.Equal(OrderConversionStatus.Unmapped, result.Status);
        Assert.Null(result.Order);
    }

    [Fact]
    public void MappedZeroArgumentOrder_ConvertsWithCorrectTypeAndPlayer()
    {
        var order = new Order(playerIndex: 4, OrderType.StopMoving);

        var result = OrderConverter.TryConvert(order);

        Assert.True(result.Success);
        Assert.Equal(GameMessageType.MSG_DO_STOP, result.Order.Type);
        Assert.Equal(4, result.Order.PlayerIndex);
        Assert.Empty(result.Order.Arguments);
    }

    [Fact]
    public void IntegerBooleanObjectIdArguments_ConvertByValue()
    {
        var order = Order.CreateSetSelection(playerId: 2, new ObjectId(658));

        var result = OrderConverter.TryConvert(order);

        Assert.True(result.Success);
        Assert.Equal(GameMessageType.MSG_CREATE_SELECTED_GROUP, result.Order.Type);
        Assert.Equal(2, result.Order.Arguments.Count);

        Assert.Equal(SimOrderArgKind.Boolean, result.Order.Arguments[0].Kind);
        Assert.True(result.Order.Arguments[0].Boolean);

        Assert.Equal(SimOrderArgKind.ObjectId, result.Order.Arguments[1].Kind);
        Assert.Equal(658u, result.Order.Arguments[1].ObjectId);
    }

    // ---- F4 float boundary: floats cross ONLY via FromWireFloat ----

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(-1f)]
    [InlineData(1105.9589f)]
    [InlineData(float.MaxValue)]
    public void FloatArgument_QuantizesIdenticallyToDirectFromWireFloat(float value)
    {
        var order = new Order(playerIndex: 0, OrderType.BuildObject);
        order.AddIntegerArgument(0);
        order.AddPositionArgument(Vector3.Zero);
        order.AddFloatArgument(value);

        var result = OrderConverter.TryConvert(order);

        Assert.True(result.Success);
        var angleArg = result.Order.Arguments[2];
        Assert.Equal(SimOrderArgKind.Fixed, angleArg.Kind);

        // The independently-computed expected value: the same wire-bits -> Fix64.FromWireFloat
        // path OrderConverter must use internally. This is the assertion that guards against
        // any future edit sneaking in a direct float->Fix64 cast instead.
        var expected = Fix64.FromWireFloat(BitConverter.SingleToUInt32Bits(value));
        Assert.Equal(expected, angleArg.Fixed);
    }

    [Fact]
    public void PositionArgument_QuantizesEachComponentViaFromWireFloat()
    {
        var position = new Vector3(1105.9589f, 728.7699f, 18.75f);
        var order = Order.CreateMoveOrder(playerId: 1, position);

        var result = OrderConverter.TryConvert(order);

        Assert.True(result.Success);
        Assert.Equal(GameMessageType.MSG_DO_MOVETO, result.Order.Type);

        var positionArg = Assert.Single(result.Order.Arguments);
        Assert.Equal(SimOrderArgKind.Position, positionArg.Kind);

        var expected = new FixVector3(
            Fix64.FromWireFloat(BitConverter.SingleToUInt32Bits(position.X)),
            Fix64.FromWireFloat(BitConverter.SingleToUInt32Bits(position.Y)),
            Fix64.FromWireFloat(BitConverter.SingleToUInt32Bits(position.Z)));
        Assert.Equal(expected, positionArg.Position);
    }

    [Fact]
    public void NaNFloatArgument_TakesTheF4BoundarysOwnPolicy_WhichIsToReject()
    {
        // The point of this test is that the converter adds NO second policy of its own on top of
        // the F4 boundary - whatever Fix64.FromWireFloat does with a bit pattern is what the
        // converter does with it. For NaN that policy is rejection: FromWireFloat's documented
        // contract (Fix64.Parse.cs) throws ArgumentException on NaN bits, because a NaN has no
        // Q31.32 image and silently substituting one would be a desync waiting to happen.
        // Infinity, which *does* have a defined image (saturation to Min/MaxValue), is covered
        // separately - the two must not be lumped together.
        var order = new Order(playerIndex: 0, OrderType.BuildObject);
        order.AddIntegerArgument(0);
        order.AddPositionArgument(Vector3.Zero);
        order.AddFloatArgument(float.NaN);

        Assert.Throws<ArgumentException>(() => OrderConverter.TryConvert(order));
    }

    [Fact]
    public void InfiniteFloatArgument_SaturatesExactlyAsFromWireFloatDoes()
    {
        var order = new Order(playerIndex: 0, OrderType.BuildObject);
        order.AddIntegerArgument(0);
        order.AddPositionArgument(Vector3.Zero);
        order.AddFloatArgument(float.PositiveInfinity);

        var result = OrderConverter.TryConvert(order);

        Assert.True(result.Success);
        var expected = Fix64.FromWireFloat(BitConverter.SingleToUInt32Bits(float.PositiveInfinity));
        Assert.Equal(expected, result.Order.Arguments[2].Fixed);
    }

    [Fact]
    public void ScreenRectangleArgument_ConvertsToCornersNotWidthHeight()
    {
        var order = new Order(playerIndex: 0, OrderType.DrawBoxSelection);
        order.AddScreenRectangleArgument(new Rectangle(10, 20, 30, 40));

        var result = OrderConverter.TryConvert(order);

        Assert.True(result.Success);
        Assert.Equal(GameMessageType.MSG_AREA_SELECTION, result.Order.Type);

        var rectArg = Assert.Single(result.Order.Arguments);
        Assert.Equal(SimOrderArgKind.ScreenRectangle, rectArg.Kind);
        Assert.Equal(10, rectArg.X0);
        Assert.Equal(20, rectArg.Y0);
        Assert.Equal(40, rectArg.X1); // x + width, not width itself (IRegion2D is x1,y1,x2,y2)
        Assert.Equal(60, rectArg.Y1); // y + height
    }

    [Fact]
    public void NullOrder_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => OrderConverter.TryConvert(null));
    }
}
