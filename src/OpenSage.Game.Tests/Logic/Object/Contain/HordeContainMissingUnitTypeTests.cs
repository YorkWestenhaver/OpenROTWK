// Regression tests for the R15 sweep crash class "HordeContainBehavior.Unpack NRE"
// (3 of the 19 r15-sweep20 map-load crashes; the reported top frame was the
// `createdObject.ParentHorde = GameObject` line of Unpack, reached via Scene3D.LoadObjects ->
// GameObject.SetMapObjectProperties).
//
// Root cause: a HordeContain RankInfo whose UnitType does not resolve to a loaded
// ObjectDefinition. `ScopedAssetCollection<ObjectDefinition>` is registered with no on-demand
// loader (AssetStore.cs), so GetByName on an unknown name returns null and the
// LazyAssetReference resolves to null; `GameLogic.CreateObject` then returns null for the null
// template ("TODO: Is this ever valid?") and Unpack dereferenced that null. AotR reaches this
// because some of its object definitions live in INI blocks the parser currently drops, while
// the hordes that reference them still load.
//
// Fixed behavior asserted here: an unresolvable rank is dropped from the formation at
// construction time with one contextual warning, the horde still forms from whatever ranks DO
// resolve, and nothing throws. Dropping (rather than keeping a null-template placeholder) also
// keeps EnqueuePayload's _pendingRegistrations reachable-to-zero, so a producing structure's
// door can still close.

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Contain;

public class HordeContainMissingUnitTypeTests
{
    private const string Definitions = @"
Object MissingUnitArcher
  KindOf = INFANTRY SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
End

Object MissingUnitSpearman
  KindOf = INFANTRY SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 60
  End
End

; Every rank references an object that was never defined - the AotR shape of the crash.
Object AllRanksMissingHorde
  KindOf = HORDE SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = HordeContain ModuleTag_Horde
    Slots = 3
    RankInfo = RankNumber:1 UnitType:NoSuchUnitDefinition Position:X:0 Y:-10 Position:X:0 Y:0 Position:X:0 Y:10
  End
End

; One resolvable rank, one dangling rank: the horde must still form its good rank.
Object PartlyMissingHorde
  KindOf = HORDE SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = HordeContain ModuleTag_Horde
    Slots = 4
    RankInfo = RankNumber:1 UnitType:MissingUnitArcher Position:X:0 Y:-10 Position:X:0 Y:10
    RankInfo = RankNumber:2 UnitType:AlsoNotDefined Position:X:20 Y:-10 Position:X:20 Y:10
  End
End

; UnitType explicitly NONE - IniParser.ParseObjectReference returns a null reference here,
; so the null must be tolerated one level earlier than the unresolved-name case.
Object NoneUnitTypeHorde
  KindOf = HORDE SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = HordeContain ModuleTag_Horde
    Slots = 2
    RankInfo = RankNumber:1 UnitType:NONE Position:X:0 Y:-10 Position:X:0 Y:10
  End
End

; Control: every rank resolves.
Object HealthyHorde
  KindOf = HORDE SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = HordeContain ModuleTag_Horde
    Slots = 3
    RankInfo = RankNumber:1 UnitType:MissingUnitArcher Position:X:0 Y:-10 Position:X:0 Y:10
    RankInfo = RankNumber:2 UnitType:MissingUnitSpearman Position:X:20 Y:0
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xF17D)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    /// <summary>
    /// Spawns a horde and unpacks it. SpawnObject does not run GameObject.SetMapObjectProperties,
    /// which is the only production caller of Unpack (GameObject.cs), so it is called explicitly -
    /// same pattern as HordeGarrisonContainContractTests.
    /// </summary>
    private static GameObject SpawnHorde(HeadlessSimGame game, string template)
    {
        var horde = game.SpawnObject(template, game.CivilianPlayer, Vector3.Zero);
        horde.FindBehavior<HordeContainBehavior>().Unpack();
        return horde;
    }

    private static List<GameObject> MembersOf(HeadlessSimGame game, GameObject horde) =>
        game.GameLogic.Objects.Where(o => o.ParentHorde == horde).ToList();

    [Fact]
    public void UnresolvableUnitType_Unpack_DoesNotThrow_AndCreatesNoMembers()
    {
        var game = NewGame();

        var horde = SpawnHorde(game, "AllRanksMissingHorde");

        Assert.Empty(MembersOf(game, horde));
    }

    [Fact]
    public void UnresolvableUnitType_ConstructionAndUnpack_LeaveTheHordeItselfAlive()
    {
        var game = NewGame();

        var horde = SpawnHorde(game, "AllRanksMissingHorde");

        // Degraded, not fatal: the horde object still exists and is usable, the map keeps loading.
        Assert.Contains(horde, game.GameLogic.Objects);
        Assert.NotNull(horde.FindBehavior<HordeContainBehavior>());
    }

    [Fact]
    public void PartiallyUnresolvableRanks_SpawnOnlyTheResolvableRank()
    {
        var game = NewGame();

        var horde = SpawnHorde(game, "PartlyMissingHorde");

        var members = MembersOf(game, horde);
        Assert.Equal(2, members.Count);
        Assert.All(members, m => Assert.Equal("MissingUnitArcher", m.Definition.Name));
    }

    [Fact]
    public void UnitTypeNone_DropsTheRank_WithoutThrowing()
    {
        var game = NewGame();

        var horde = SpawnHorde(game, "NoneUnitTypeHorde");

        Assert.Empty(MembersOf(game, horde));
    }

    [Fact]
    public void SelectAllAndSetTargetPoints_AreSafeOnAHordeWithNoResolvableRanks()
    {
        var game = NewGame();
        var horde = SpawnHorde(game, "AllRanksMissingHorde");
        var contain = horde.FindBehavior<HordeContainBehavior>();

        // The dropped-rank formation must not break the ordinary horde surface either.
        Assert.Empty(contain.SelectAll(true));
        contain.SetTargetPoints(new Vector3(100, 100, 0), new Vector3(1, 0, 0));
        Assert.Equal(Vector3.Zero, contain.GetFormationOffset(horde));
    }

    [Fact]
    public void FullyResolvableHorde_StillSpawnsEveryRank()
    {
        var game = NewGame();

        var horde = SpawnHorde(game, "HealthyHorde");

        var members = MembersOf(game, horde);
        Assert.Equal(3, members.Count);
        Assert.Equal(2, members.Count(m => m.Definition.Name == "MissingUnitArcher"));
        Assert.Equal(1, members.Count(m => m.Definition.Name == "MissingUnitSpearman"));
    }
}
