// Mocked-game contract tests for the Round-12 ProductionQueueHordeContain port (task packet
// testCases): instantiation/empty state, member entry (slot assignment, EntryPosition +
// EntryOffset steering, EnterSound cue, ObjectStatusOfContained bits), damage propagation
// (DamagePercentToUnits, including the 0% block), faction-stance filtering (Allow*Inside),
// member exit (round-robin NumberOfExitPaths, ExitOffset), and slot capacity/reuse - plus the
// shared shadow-copy base test and a mid-state save/load round-trip continuation.
//
// Definitions parse from INI text through the real parser, so the audited quantizing parse
// functions (ParseFix64Percentage, ParseFixVector3) are on the tested path.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Contain;

public class ProductionQueueHordeContainContractTests
{
    private const string Definitions = @"
Locomotor TestQueueLoco
  Surfaces = GROUND
  Speed = 30
  TurnRate = 360
  Acceleration = 100
  Braking = 100
  Appearance = TWO_LEGS
  ZAxisBehavior = NO_Z_MOTIVE_FORCE
End

Object QueueInfantry
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Locomotor = SET_NORMAL TestQueueLoco
End

Object QueueVehicle
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object QueueRange
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = ProductionQueueHordeContain ModuleTag_Contain
    ObjectStatusOfContained = INSIDE_GARRISON
    ContainMax = 5
    PassengerFilter = NONE +INFANTRY
    AllowEnemiesInside = No
    AllowNeutralInside = Yes
    AllowAlliesInside = Yes
    NumberOfExitPaths = 2
    DamagePercentToUnits = 50%
    EntryPosition = X:10 Y:0 Z:0
    EntryOffset = X:2 Y:0 Z:0
    ExitOffset = X:-10 Y:5 Z:0
    EnterSound = EnterQueueSound
  End
End

Object QueueRangeNoDamage
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = ProductionQueueHordeContain ModuleTag_Contain
    ContainMax = 3
    PassengerFilter = NONE +INFANTRY
    AllowNeutralInside = Yes
    NumberOfExitPaths = 1
    DamagePercentToUnits = 0%
  End
End

Object GatedRange
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = ProductionQueueHordeContain ModuleTag_Contain
    ContainMax = 3
    PassengerFilter = ALL
    AllowEnemiesInside = No
    AllowNeutralInside = No
    AllowAlliesInside = Yes
    NumberOfExitPaths = 1
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x9052)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static ProductionQueueHordeContain ContainOf(GameObject obj) =>
        obj.BehaviorModules.OfType<ProductionQueueHordeContain>().Single();

    // ---- instantiation / empty state ----

    [Fact]
    public void Instantiation_EmptyState_NoMemberAssignments()
    {
        var game = NewGame();
        var range = game.SpawnObject("QueueRange", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(range);

        Assert.Equal(5, contain.SlotCount);
        Assert.Equal(0, contain.MemberCount);
        Assert.False(contain.IsFull);
        Assert.Empty(contain.MemberIds);
    }

    // ---- member entry ----

    [Fact]
    public void MemberEntry_AssignsSlots_StatusBits_AndEnterSound()
    {
        var game = NewGame();
        var range = game.SpawnObject("QueueRange", game.CivilianPlayer, new Vector3(100, 100, 0));
        var contain = ContainOf(range);

        var members = new[]
        {
            game.SpawnObject("QueueInfantry", game.CivilianPlayer, new Vector3(0, 0, 0)),
            game.SpawnObject("QueueInfantry", game.CivilianPlayer, new Vector3(20, 0, 0)),
            game.SpawnObject("QueueInfantry", game.CivilianPlayer, new Vector3(40, 0, 0)),
        };

        foreach (var member in members)
        {
            Assert.True(contain.TryAddMember(member));
        }

        Assert.Equal(3, contain.MemberCount);
        Assert.Equal(3, contain.EnterSoundFiredCount);
        foreach (var member in members)
        {
            Assert.True(contain.SlotIndexOf(member.Id) >= 0);
            Assert.True(member.TestStatus(ObjectStatus.InsideGarrison));
        }

        // EntryPosition + EntryOffset (X:10+2, Y:0, Z:0), unrotated (spawn facing 0).
        var expected = contain.EntryWorldPosition();
        Assert.Equal(Fix64.FromDecimalLiteral("112"), expected.X);
        Assert.Equal(Fix64.FromDecimalLiteral("100"), expected.Y);
        Assert.Equal(Fix64.Zero, expected.Z);
    }

    [Fact]
    public void MemberEntry_SteersMemberTowardEntryWorldPosition()
    {
        var game = NewGame();
        var range = game.SpawnObject("QueueRange", game.CivilianPlayer, new Vector3(100, 100, 0));
        var contain = ContainOf(range);
        // Spawned close to the entry target (112,100,0) so the S2 locomotor converges well
        // within the warmup below regardless of acceleration/turn-rate ramp-up.
        var member = game.SpawnObject("QueueInfantry", game.CivilianPlayer, new Vector3(100, 100, 0));

        Assert.True(contain.TryAddMember(member));

        for (var i = 0; i < 20; i++)
        {
            game.Step();
        }

        var target = contain.EntryWorldPosition();
        var pos = member.FindBehavior<SimLocomotorUpdate>().Physics.Position;
        var closeEnoughSq = Fix64.FromDecimalLiteral("16"); // within 4 of the target
        var dx = pos.X - target.X;
        var dy = pos.Y - target.Y;
        Assert.True(dx * dx + dy * dy <= closeEnoughSq,
            $"member at ({pos.X},{pos.Y}) not near entry target ({target.X},{target.Y})");
    }

    [Fact]
    public void MemberEntry_IncompatibleKindOf_IsRejectedByPassengerFilter()
    {
        var game = NewGame();
        var range = game.SpawnObject("QueueRange", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(range);
        var vehicle = game.SpawnObject("QueueVehicle", game.CivilianPlayer, Vector3.Zero);

        Assert.False(contain.TryAddMember(vehicle));
        Assert.Equal(0, contain.MemberCount);
    }

    // ---- damage propagation ----

    [Fact]
    public void ContainerDamage_PropagatesDamagePercentToEachMember()
    {
        var game = NewGame();
        var range = game.SpawnObject("QueueRange", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(range);
        var members = new[]
        {
            game.SpawnObject("QueueInfantry", game.CivilianPlayer, Vector3.Zero),
            game.SpawnObject("QueueInfantry", game.CivilianPlayer, Vector3.Zero),
        };
        foreach (var member in members)
        {
            Assert.True(contain.TryAddMember(member));
        }

        PortedModuleTestKit.ApplyDamage(range, 20f);

        // DamagePercentToUnits 50% of the 20 actually dealt -> 10 to each seated member.
        foreach (var member in members)
        {
            Assert.Equal(90f, member.BodyModule.Health);
        }
    }

    [Fact]
    public void ContainerDamage_ZeroPercent_BlocksAllMemberDamage()
    {
        var game = NewGame();
        var range = game.SpawnObject("QueueRangeNoDamage", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(range);
        var member = game.SpawnObject("QueueInfantry", game.CivilianPlayer, Vector3.Zero);
        Assert.True(contain.TryAddMember(member));

        PortedModuleTestKit.ApplyDamage(range, 20f);

        Assert.Equal(100f, member.BodyModule.Health);
    }

    // ---- faction-stance filtering (Allow*Inside) ----

    [Fact]
    public void FactionFilter_AllyUnit_IsAccepted()
    {
        var game = NewGame();
        var range = game.SpawnObject("GatedRange", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(range);
        var ally = game.SpawnObject("QueueInfantry", game.PlayerManager.NeutralPlayer, Vector3.Zero);

        game.PlayerManager.NeutralPlayer.SetRelationship(game.CivilianPlayer, RelationshipType.Allies);
        range.Team = new Team(new TeamTemplate(game.TeamFactory, 701, "GatedTeam", game.CivilianPlayer, isSingleton: true), 701);
        ally.Team = new Team(new TeamTemplate(game.TeamFactory, 702, "AllyTeam", game.PlayerManager.NeutralPlayer, isSingleton: true), 702);

        Assert.True(contain.TryAddMember(ally));
    }

    [Fact]
    public void FactionFilter_EnemyUnit_IsRejected()
    {
        var game = NewGame();
        var range = game.SpawnObject("GatedRange", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(range);
        var enemy = game.SpawnObject("QueueInfantry", game.PlayerManager.NeutralPlayer, Vector3.Zero);

        game.PlayerManager.NeutralPlayer.SetRelationship(game.CivilianPlayer, RelationshipType.Enemies);
        range.Team = new Team(new TeamTemplate(game.TeamFactory, 703, "GatedTeam", game.CivilianPlayer, isSingleton: true), 703);
        enemy.Team = new Team(new TeamTemplate(game.TeamFactory, 704, "EnemyTeam", game.PlayerManager.NeutralPlayer, isSingleton: true), 704);

        Assert.False(contain.TryAddMember(enemy));
        Assert.Equal(0, contain.MemberCount);
    }

    [Fact]
    public void FactionFilter_NeutralUnit_IsRejected()
    {
        // Default relationship (no SetRelationship override) is Neutral - GatedRange's
        // AllowNeutralInside = No must reject it.
        var game = NewGame();
        var range = game.SpawnObject("GatedRange", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(range);
        var neutral = game.SpawnObject("QueueInfantry", game.PlayerManager.NeutralPlayer, Vector3.Zero);

        range.Team = new Team(new TeamTemplate(game.TeamFactory, 705, "GatedTeam", game.CivilianPlayer, isSingleton: true), 705);
        neutral.Team = new Team(new TeamTemplate(game.TeamFactory, 706, "NeutralTeam", game.PlayerManager.NeutralPlayer, isSingleton: true), 706);

        Assert.False(contain.TryAddMember(neutral));
        Assert.Equal(0, contain.MemberCount);
    }

    // ---- member exit ----

    [Fact]
    public void MemberExit_RotatesThroughExitPaths_AndAppliesExitOffset()
    {
        var game = NewGame();
        var range = game.SpawnObject("QueueRange", game.CivilianPlayer, new Vector3(100, 100, 0));
        var contain = ContainOf(range);
        var members = new[]
        {
            game.SpawnObject("QueueInfantry", game.CivilianPlayer, Vector3.Zero),
            game.SpawnObject("QueueInfantry", game.CivilianPlayer, Vector3.Zero),
            game.SpawnObject("QueueInfantry", game.CivilianPlayer, Vector3.Zero),
        };
        foreach (var member in members)
        {
            Assert.True(contain.TryAddMember(member));
        }

        // NumberOfExitPaths = 2: successive releases rotate 0, 1, 0.
        Assert.True(contain.TryRemoveMember(members[0].Id));
        Assert.Equal(0, contain.LastExitPathIndex);
        Assert.True(contain.TryRemoveMember(members[1].Id));
        Assert.Equal(1, contain.LastExitPathIndex);
        Assert.True(contain.TryRemoveMember(members[2].Id));
        Assert.Equal(0, contain.LastExitPathIndex);

        Assert.Equal(0, contain.MemberCount);
        foreach (var member in members)
        {
            Assert.False(member.TestStatus(ObjectStatus.InsideGarrison));
        }

        // ExitOffset (X:-10 Y:5 Z:0), unrotated (spawn facing 0).
        var exit = contain.ExitWorldPosition();
        Assert.Equal(Fix64.FromDecimalLiteral("90"), exit.X);
        Assert.Equal(Fix64.FromDecimalLiteral("105"), exit.Y);
    }

    // ---- slot capacity / reuse ----

    [Fact]
    public void SlotCapacity_RejectsWhenFull_AcceptsAfterVacate()
    {
        var game = NewGame();
        var range = game.SpawnObject("QueueRange", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(range);

        var members = new GameObject[5];
        for (var i = 0; i < members.Length; i++)
        {
            members[i] = game.SpawnObject("QueueInfantry", game.CivilianPlayer, Vector3.Zero);
            Assert.True(contain.TryAddMember(members[i]));
        }
        Assert.True(contain.IsFull);

        var overflow = game.SpawnObject("QueueInfantry", game.CivilianPlayer, Vector3.Zero);
        Assert.False(contain.TryAddMember(overflow));

        Assert.True(contain.TryRemoveMember(members[2].Id));
        Assert.False(contain.IsFull);

        Assert.True(contain.TryAddMember(overflow));
        Assert.Equal(2, contain.SlotIndexOf(overflow.Id)); // reused the vacated slot
    }

    // ---- shared base tests ----

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var range = game.SpawnObject("QueueRange", game.CivilianPlayer, Vector3.Zero);
        var live = ContainOf(range);
        var member = game.SpawnObject("QueueInfantry", game.CivilianPlayer, Vector3.Zero);
        live.TryAddMember(member);

        var shadowHost = game.SpawnObject("QueueRange", game.CivilianPlayer, new Vector3(50, 0, 0));
        var shadow = ContainOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidState_SaveLoad_ContinuesIdentically()
    {
        var a = RunScenario(roundTripBeforeStep: -1);
        var b = RunScenario(roundTripBeforeStep: 1);
        Assert.Equal(a, b);
    }

    private static float[] RunScenario(int roundTripBeforeStep)
    {
        var game = NewGame(seed: 0xFEED2);
        var range = game.SpawnObject("QueueRange", game.CivilianPlayer, Vector3.Zero);
        var contain = ContainOf(range);
        var members = new[]
        {
            game.SpawnObject("QueueInfantry", game.CivilianPlayer, Vector3.Zero),
            game.SpawnObject("QueueInfantry", game.CivilianPlayer, Vector3.Zero),
        };

        var healths = new float[members.Length];
        for (var i = 0; i < members.Length; i++)
        {
            if (i == roundTripBeforeStep)
            {
                PortedModuleTestKit.Load(contain, PortedModuleTestKit.Save(contain));
            }

            contain.TryAddMember(members[i]);
            PortedModuleTestKit.ApplyDamage(range, 10f);
            healths[i] = members[i].BodyModule.Health;
        }

        return healths;
    }
}
