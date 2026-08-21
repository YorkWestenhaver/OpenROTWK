// Mocked-game contract tests for the HordeGarrisonContain port (R12), one test per behavior
// branch the task packet's testCases enumerate.
//
// This module is legacy (GameObject, IGameEngine), matching its ModuleData ancestor
// (HordeTransportContainModuleData : BehaviorModuleData, plain System.Numerics.Vector3 fields,
// not [SimDataAudited]/Fix64) and every landed sibling in Logic/Object/Contain/ (GarrisonContain,
// TransportContain, HordeContain): mirroring DemoTrapUpdateContractTests/
// BunkerBusterBehaviorContractTests, there is no Xfer/shadow-copy CRC test here.
//
// The hordes in these tests use the legacy HordeContain/HordeContainBehavior pair (float
// RankInfo formation, GameObject.ParentHorde back-link), not the S6 SimHordeContain track -
// HordeGarrisonContainModuleData's own Vector3-typed EntryPosition/EntryOffset/ExitOffset
// fields signal it belongs to the float-legacy family, not the Fix64 Sim one. Unpack() is
// called explicitly because SpawnObject (unlike real map-object placement) does not run
// GameObject.SetMapObjectProperties, which is the only place that normally calls it.
//
// testCases 4 ("garrison fire ... via HordeAIUpdate") and 5 (AlternateFormation morph via
// RankInfo) are explicitly out of this module's scope per the task packet - both are jobs for
// HordeAIUpdate/HordeContainBehavior once/where they exist, not HordeGarrisonContain - so they
// have no tests here, matching how SimHordeContain's own header documents deferred items in
// comments rather than asserting on modules it doesn't own.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Contain;

public class HordeGarrisonContainContractTests
{
    private const string Definitions = @"
Object Archer
  KindOf = INFANTRY SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
End

Object Knight
  KindOf = INFANTRY SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 80
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
End

Object ArcherHorde
  KindOf = HORDE SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = HordeContain ModuleTag_Horde
    Slots = 5
    RankInfo = RankNumber:1 UnitType:Archer Position:X:0 Y:-20 Position:X:0 Y:-10 Position:X:0 Y:0 Position:X:0 Y:10 Position:X:0 Y:20
  End
End

Object KnightHorde
  KindOf = HORDE SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = HordeContain ModuleTag_Horde
    Slots = 4
    RankInfo = RankNumber:1 UnitType:Knight Position:X:0 Y:-15 Position:X:0 Y:-5 Position:X:0 Y:5 Position:X:0 Y:15
  End
End

Object Settlement
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = HordeGarrisonContain ModuleTag_Garrison
    ContainMax = 8
    MaxHordeCapacity = 8
    EntryPosition = X:0 Y:0 Z:0
    EntryOffset = X:5 Y:0 Z:0
    ExitOffset = X:0 Y:-30 Z:0
    EjectPassengersOnDeath = Yes
  End
End

Object SettlementKillsOnDeath
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = HordeGarrisonContain ModuleTag_Garrison
    ContainMax = 8
    MaxHordeCapacity = 8
    EntryPosition = X:0 Y:0 Z:0
    EntryOffset = X:5 Y:0 Z:0
    ExitOffset = X:0 Y:-30 Z:0
    EjectPassengersOnDeath = No
  End
End

Object SettlementSmallCapacity
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = HordeGarrisonContain ModuleTag_Garrison
    ContainMax = 8
    MaxHordeCapacity = 3
    EntryPosition = X:0 Y:0 Z:0
    EntryOffset = X:5 Y:0 Z:0
    ExitOffset = X:0 Y:-30 Z:0
    EjectPassengersOnDeath = Yes
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xA1D5)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    /// <summary>Spawns a horde and unpacks it (the SetMapObjectProperties path SpawnObject skips).</summary>
    private static GameObject SpawnHorde(HeadlessSimGame game, string template, Vector3 position)
    {
        var horde = game.SpawnObject(template, game.CivilianPlayer, position);
        horde.FindBehavior<HordeContainBehavior>().Unpack();
        return horde;
    }

    private static System.Collections.Generic.List<GameObject> HordeMembers(HeadlessSimGame game, GameObject horde) =>
        game.GameLogic.Objects.Where(o => o.ParentHorde == horde).ToList();

    // ---- testCase 1: garrison entry ----

    [Fact]
    public void GarrisonEntry_AllMembersSeatInFormation_HordeStaysSelectable_MembersInvisible()
    {
        var game = NewGame();
        var horde = SpawnHorde(game, "ArcherHorde", new Vector3(0, 100, 0));
        var settlement = game.SpawnObject("Settlement", game.CivilianPlayer, new Vector3(0, 0, 0));
        var garrison = settlement.FindBehavior<HordeGarrisonContain>();
        var members = HordeMembers(game, horde);
        Assert.Equal(5, members.Count);

        Assert.True(garrison.TryGarrisonHorde(horde));

        Assert.Equal(5, garrison.OccupiedSlots);
        foreach (var member in members)
        {
            Assert.True(member.Hidden);
            Assert.False(member.IsSelectable);
            Assert.Equal(settlement.Id, member.ContainerId);
        }

        // The horde object itself was never added to the container: unlike a passenger of a
        // (still-unported) HordeTransportContain, it stays visible and command-selectable.
        Assert.False(horde.Hidden);
        Assert.True(horde.IsSelectable);
    }

    // ---- testCase 2: overfill rejection ----

    [Fact]
    public void OverfillRejection_OnlyFreeSlotIsFilled_AlreadySeatedMembersUnaffected()
    {
        var game = NewGame();
        var settlement = game.SpawnObject("Settlement", game.CivilianPlayer, new Vector3(0, 0, 0));
        var garrison = settlement.FindBehavior<HordeGarrisonContain>();

        // Seat 7 lone archers directly (RegisterMember path) to leave exactly 1 free slot out
        // of ContainMax = 8, without depending on TryGarrisonHorde's own bookkeeping.
        for (var i = 0; i < 7; i++)
        {
            var archer = game.SpawnObject("Archer", game.CivilianPlayer, new Vector3(0, 100 + i, 0));
            Assert.True(garrison.RegisterMember(archer));
        }
        Assert.Equal(7, garrison.OccupiedSlots);
        var seatedBefore = garrison.ContainedMemberIds.ToList();

        var firstNew = game.SpawnObject("Archer", game.CivilianPlayer, new Vector3(0, 200, 0));
        var secondNew = game.SpawnObject("Archer", game.CivilianPlayer, new Vector3(0, 201, 0));

        Assert.True(garrison.RegisterMember(firstNew));
        Assert.Equal(8, garrison.OccupiedSlots);

        Assert.False(garrison.RegisterMember(secondNew));
        Assert.Equal(8, garrison.OccupiedSlots);
        Assert.True(secondNew.IsSelectable);
        Assert.False(secondNew.Hidden);

        // The 7 already-seated members are untouched by the rejected attempt.
        Assert.Equal(seatedBefore, garrison.ContainedMemberIds.Take(7).ToList());
    }

    [Fact]
    public void MaxHordeCapacity_RejectsWholeHordeThatIsTooBig()
    {
        var game = NewGame();
        // 5 members vs MaxHordeCapacity = 3, with ContainMax = 8 (plenty of raw slots) - only
        // the horde-size gate can reject this, not the slot gate.
        var horde = SpawnHorde(game, "ArcherHorde", new Vector3(0, 100, 0));
        var settlement = game.SpawnObject("SettlementSmallCapacity", game.CivilianPlayer, new Vector3(0, 0, 0));
        var garrison = settlement.FindBehavior<HordeGarrisonContain>();
        var members = HordeMembers(game, horde);

        Assert.False(garrison.TryGarrisonHorde(horde));

        Assert.Equal(0, garrison.OccupiedSlots);
        foreach (var member in members)
        {
            Assert.False(member.Hidden);
            Assert.True(member.IsSelectable);
        }
    }

    // ---- testCase 3: garrison exit ----

    [Fact]
    public void GarrisonExit_MembersRestoredAtExitOffset_FirstMemberIssuesPathfindOrder()
    {
        var game = NewGame();
        var horde = SpawnHorde(game, "KnightHorde", new Vector3(0, 100, 0));
        var settlement = game.SpawnObject("Settlement", game.CivilianPlayer, new Vector3(0, 0, 0));
        var garrison = settlement.FindBehavior<HordeGarrisonContain>();
        var members = HordeMembers(game, horde);
        Assert.True(garrison.TryGarrisonHorde(horde));

        Assert.True(garrison.ExitGarrisonHorde(horde));

        Assert.Equal(0, garrison.OccupiedSlots);
        foreach (var member in members)
        {
            Assert.False(member.Hidden);
            Assert.True(member.IsSelectable);
            Assert.Equal(ObjectId.Invalid, member.ContainerId);
            // Exit anchor = container position (0,0,0) + ExitOffset (0,-30,0); the horde's own
            // RankInfo formation offset is added on top, so every member lands near that anchor.
            Assert.InRange(member.Translation.Y, -45f, -15f);
        }

        var first = members[0];
        Assert.NotEmpty(first.AIUpdate.TargetPoints);
    }

    // ---- testCase 6: garrison destruction ----

    [Fact]
    public void ContainerDeath_EjectPassengersOnDeath_SpillsMembersOutAlive()
    {
        var game = NewGame();
        var horde = SpawnHorde(game, "ArcherHorde", new Vector3(0, 100, 0));
        var settlement = game.SpawnObject("Settlement", game.CivilianPlayer, new Vector3(0, 0, 0));
        var garrison = settlement.FindBehavior<HordeGarrisonContain>();
        var members = HordeMembers(game, horde);
        Assert.True(garrison.TryGarrisonHorde(horde));
        game.Step();

        settlement.Kill();
        game.Step();

        Assert.Equal(0, garrison.OccupiedSlots);
        foreach (var member in members)
        {
            Assert.False(member.IsEffectivelyDead);
            Assert.False(member.Hidden);
            Assert.True(member.IsSelectable);
        }
    }

    [Fact]
    public void ContainerDeath_NoEjectPassengersOnDeath_KillsMembers()
    {
        var game = NewGame();
        var horde = SpawnHorde(game, "ArcherHorde", new Vector3(0, 100, 0));
        var settlement = game.SpawnObject("SettlementKillsOnDeath", game.CivilianPlayer, new Vector3(0, 0, 0));
        var garrison = settlement.FindBehavior<HordeGarrisonContain>();
        var members = HordeMembers(game, horde);
        Assert.True(garrison.TryGarrisonHorde(horde));
        game.Step();

        settlement.Kill();
        game.Step();

        Assert.Equal(0, garrison.OccupiedSlots);
        foreach (var member in members)
        {
            Assert.True(member.IsEffectivelyDead);
        }
    }
}
