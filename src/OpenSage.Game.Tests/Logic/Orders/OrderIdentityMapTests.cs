using System;
using OpenSage.Logic.Orders;
using OpenSage.SimCore.Orders;
using Xunit;

namespace OpenSage.Tests.Logic.Orders;

public class OrderIdentityMapTests
{
    // The anchor collision cases from the blackboard finding this map exists to fix
    // (L2-plan #2): at these three integer values, OrderType and GameMessageType disagree,
    // so a naive `(GameMessageType)(int)orderType` cast would be silently wrong for two of
    // them and only right for the third by coincidence.

    [Fact]
    public void Collision1001_SetSelection_MapsToCreateSelectedGroup()
    {
        // The one anchor value where the raw integers DO happen to agree - still must go
        // through the explicit table, not a cast.
        Assert.True(OrderIdentityMap.TryGetGameMessageType(OrderType.SetSelection, out var messageType));
        Assert.Equal(GameMessageType.MSG_CREATE_SELECTED_GROUP, messageType);
        Assert.Equal((int)OrderType.SetSelection, (int)GameMessageType.MSG_CREATE_SELECTED_GROUP);
    }

    [Fact]
    public void Collision1059_AttackObject_DoesNotMapToValueAt1059()
    {
        // GameMessageType's own value 1059 is MSG_COMBINE_HORDES_WITH_OBJECT (BFME2-only, no ZH
        // ancestor) - NOT what OrderType.AttackObject (also numbered 1059) means.
        Assert.True(OrderIdentityMap.TryGetGameMessageType(OrderType.AttackObject, out var messageType));
        Assert.Equal(GameMessageType.MSG_DO_ATTACK_OBJECT, messageType);
        Assert.NotEqual((GameMessageType)(int)OrderType.AttackObject, messageType);
        Assert.Equal(GameMessageType.MSG_COMBINE_HORDES_WITH_OBJECT, (GameMessageType)(int)OrderType.AttackObject);
    }

    [Fact]
    public void Collision1068_MoveTo_DoesNotMapToValueAt1068()
    {
        // GameMessageType's own value 1068 is MSG_ENTER (the real target of OrderType.Enter,
        // ZH 1066) - NOT what OrderType.MoveTo (also numbered 1068) means.
        Assert.True(OrderIdentityMap.TryGetGameMessageType(OrderType.MoveTo, out var messageType));
        Assert.Equal(GameMessageType.MSG_DO_MOVETO, messageType);
        Assert.NotEqual((GameMessageType)(int)OrderType.MoveTo, messageType);
        Assert.Equal(GameMessageType.MSG_ENTER, (GameMessageType)(int)OrderType.MoveTo);
    }

    [Fact]
    public void Enter_MapsToMsgEnter_TheOtherHalfOfThe1068Collision()
    {
        Assert.True(OrderIdentityMap.TryGetGameMessageType(OrderType.Enter, out var messageType));
        Assert.Equal(GameMessageType.MSG_ENTER, messageType);
    }

    [Fact]
    public void Checksum_MapsToLogicCrc()
    {
        Assert.True(OrderIdentityMap.TryGetGameMessageType(OrderType.Checksum, out var messageType));
        Assert.Equal(GameMessageType.MSG_LOGIC_CRC, messageType);
    }

    [Fact]
    public void Revive_MapsToSameNumber_ByRatifiedConstruction()
    {
        // dr-0033: OrderType.Revive already carries the recovered BFME2 value directly.
        Assert.True(OrderIdentityMap.TryGetGameMessageType(OrderType.Revive, out var messageType));
        Assert.Equal(GameMessageType.MSG_REVIVE, messageType);
        Assert.Equal((int)OrderType.Revive, (int)messageType);
    }

    [Theory]
    [InlineData(OrderType.CreateGroup0, GameMessageType.MSG_CREATE_TEAM0)]
    [InlineData(OrderType.SelectGroup9, GameMessageType.MSG_SELECT_TEAM9)]
    [InlineData(OrderType.BuildObject, GameMessageType.MSG_DOZER_CONSTRUCT)]
    [InlineData(OrderType.DrawBoxSelection, GameMessageType.MSG_AREA_SELECTION)]
    [InlineData(OrderType.RepairVehicle, GameMessageType.MSG_GET_REPAIRED)]
    [InlineData(OrderType.RepairStructure, GameMessageType.MSG_DO_REPAIR)]
    [InlineData(OrderType.GatherDumpSupplies, GameMessageType.MSG_DOCK)]
    [InlineData(OrderType.SelectWeapon, GameMessageType.MSG_SWITCH_WEAPONS)]
    public void RepresentativePairs_MapAsExpected(OrderType orderType, GameMessageType expected)
    {
        Assert.True(OrderIdentityMap.TryGetGameMessageType(orderType, out var messageType));
        Assert.Equal(expected, messageType);
    }

    [Theory]
    [InlineData(OrderType.EndGame)]
    [InlineData(OrderType.ClearSelection)]
    [InlineData(OrderType.ToggleOvercharge)]
    [InlineData(OrderType.HackInternet)]
    [InlineData(OrderType.SnipeVehicle)]
    [InlineData(OrderType.Unknown1097)]
    public void DeliberatelyUnmappedOrderTypes_MissCleanly(OrderType orderType)
    {
        Assert.False(OrderIdentityMap.TryGetGameMessageType(orderType, out _));
    }

    [Fact]
    public void ReverseLookup_RoundTripsEveryEntry()
    {
        // The bidirectional promise: every forward entry has a matching reverse entry that
        // round-trips back to the same OrderType.
        foreach (var orderType in AllMappedOrderTypes)
        {
            Assert.True(OrderIdentityMap.TryGetGameMessageType(orderType, out var messageType));
            Assert.True(OrderIdentityMap.TryGetOrderType(messageType, out var roundTripped));
            Assert.Equal(orderType, roundTripped);
        }
    }

    [Fact]
    public void ReverseLookup_MissesForAnUnmappedMessageType()
    {
        // MSG_COMBINE_HORDES_WITH_OBJECT (1059) has no OrderType ancestor at all - see the
        // 1059 collision test.
        Assert.False(OrderIdentityMap.TryGetOrderType(GameMessageType.MSG_COMBINE_HORDES_WITH_OBJECT, out _));
    }

    [Fact]
    public void CastleOrderTypes_ThreeAreMappedBothWays()
    {
        // R15 S9-05 closed BR-P4A's deferred follow-up: the castle OrderTypes now exist and
        // three of the four recorded pairings are real entries. This test used to assert the
        // opposite (that nothing at these message values resolved), by design - BR-P4A wrote it
        // to fail loudly the moment S9-05 landed, which is what brought it here.
        Assert.True(OrderIdentityMap.TryGetOrderType(GameMessageType.MSG_FOUNDATION_CONSTRUCT, out var foundationConstruct));
        Assert.Equal(OrderType.FoundationConstruct, foundationConstruct);

        Assert.True(OrderIdentityMap.TryGetOrderType(GameMessageType.MSG_CASTLE_UNPACK, out var castleUnpack));
        Assert.Equal(OrderType.CastleUnpack, castleUnpack);

        Assert.True(OrderIdentityMap.TryGetOrderType(GameMessageType.MSG_CASTLE_PACK, out var castlePack));
        Assert.Equal(OrderType.CastlePack, castlePack);
    }

    [Fact]
    public void CastleOrderTypes_MappedByMeaningNotByValue_TheCastWouldBeNonsense()
    {
        // The castle pairings are the sharpest case for this table's no-cast rule: their
        // OrderType values are engine-local 2xxx numbers (their recovered numbers were already
        // occupied in the ZH-derived OrderType enum), so casting the OrderType to a
        // GameMessageType silently produces a DIFFERENT, PERFECTLY VALID message.
        //
        // INT-R1B correction: this test originally asserted the cast landed on an undefined
        // value. It does not, and that is the worse outcome - the 2xxx band the castle members
        // took is fully populated in GameMessageType by the object-state messages, so a naive
        // cast type-checks, round-trips, and dispatches the wrong behaviour with no error to
        // catch. Enum.IsDefined would NOT have saved a caller here; only the table does.
        Assert.True(OrderIdentityMap.TryGetGameMessageType(OrderType.CastleUnpack, out var messageType));
        Assert.Equal(GameMessageType.MSG_CASTLE_UNPACK, messageType);
        Assert.NotEqual((int)OrderType.CastleUnpack, (int)messageType);

        // What the cast actually yields for each castle member: a defined object-state message
        // that has nothing to do with castles. Pinned so the danger stays legible.
        Assert.Equal(GameMessageType.MSG_OBJECT_POSITION, (GameMessageType)(int)OrderType.CastleUnpack);
        Assert.Equal(GameMessageType.MSG_OBJECT_ORIENTATION, (GameMessageType)(int)OrderType.CastlePack);
        Assert.Equal(GameMessageType.MSG_OBJECT_CREATED, (GameMessageType)(int)OrderType.FoundationConstruct);
        Assert.Equal(GameMessageType.MSG_OBJECT_DESTROYED, (GameMessageType)(int)OrderType.FoundationConstructCancel);
    }

    [Fact]
    public void CastleOrderTypes_ExplicitObjectFormIsNotRepresentable()
    {
        // MSG_CASTLE_UNPACK_EXPLICIT_OBJECT selects the camp by NAME, and OrderArgumentType has
        // no string member, so no OrderType member exists for it and nothing maps to it. This
        // is a permanent "not representable" state pending an L3 -> L2 escalation (Order needs
        // a string argument type), not a pending entry - see OrderIdentityMap's header.
        Assert.False(OrderIdentityMap.TryGetOrderType(GameMessageType.MSG_CASTLE_UNPACK_EXPLICIT_OBJECT, out _));

        // FoundationConstructCancel exists as an OrderType but has no recovered counterpart
        // (1050 is MSG_DOZER_CONSTRUCT), so it is deliberately unmapped - same rule as Revive
        // having no CancelRevive.
        Assert.False(OrderIdentityMap.TryGetGameMessageType(OrderType.FoundationConstructCancel, out _));
    }

    [Fact]
    public void Count_MatchesTheLiteralEntryCount()
    {
        // 61 at BR-P4A + the 3 castle entries S9-05 added.
        Assert.Equal(64, OrderIdentityMap.Count);
        Assert.Equal(AllMappedOrderTypes.Length, OrderIdentityMap.Count);
    }

    private static readonly OrderType[] AllMappedOrderTypes =
    {
        OrderType.SetSelection, OrderType.Deselect,
        OrderType.CreateGroup0, OrderType.CreateGroup1, OrderType.CreateGroup2, OrderType.CreateGroup3,
        OrderType.CreateGroup4, OrderType.CreateGroup5, OrderType.CreateGroup6, OrderType.CreateGroup7,
        OrderType.CreateGroup8, OrderType.CreateGroup9,
        OrderType.SelectGroup0, OrderType.SelectGroup1, OrderType.SelectGroup2, OrderType.SelectGroup3,
        OrderType.SelectGroup4, OrderType.SelectGroup5, OrderType.SelectGroup6, OrderType.SelectGroup7,
        OrderType.SelectGroup8, OrderType.SelectGroup9,
        OrderType.UseWeapon, OrderType.SpecialPower, OrderType.SpecialPowerAtLocation,
        OrderType.SpecialPowerAtObject, OrderType.SetRallyPoint, OrderType.PurchaseScience,
        OrderType.BeginUpgrade, OrderType.CancelUpgrade, OrderType.CreateUnit, OrderType.CancelUnit,
        OrderType.BuildObject, OrderType.CancelBuild, OrderType.Sell, OrderType.ExitContainer,
        OrderType.Evacuate, OrderType.CombatDrop, OrderType.DrawBoxSelection, OrderType.AttackObject,
        OrderType.ForceAttackObject, OrderType.ForceAttackGround, OrderType.RepairVehicle,
        OrderType.RepairStructure, OrderType.ResumeBuild, OrderType.Enter,
        OrderType.GatherDumpSupplies, OrderType.MoveTo, OrderType.AttackMove, OrderType.AddWaypoint,
        OrderType.GuardMode, OrderType.StopMoving, OrderType.Scatter, OrderType.Cheer,
        OrderType.SelectWeapon, OrderType.DirectParticleCannon, OrderType.ToggleFormationMode,
        OrderType.SetCameraPosition, OrderType.Checksum, OrderType.SelectClearMines, OrderType.Revive,
        // S9-05 castle orders (FoundationConstructCancel is deliberately absent - unmapped).
        OrderType.FoundationConstruct, OrderType.CastleUnpack, OrderType.CastlePack,
    };
}
