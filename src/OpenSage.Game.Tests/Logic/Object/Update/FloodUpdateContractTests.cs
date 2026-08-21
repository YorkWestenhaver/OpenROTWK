// Mocked-game contract tests for the FloodUpdate swarm-spawn port (api-freeze-v1 §6 fitness
// item 4 shape): spawn-time control-point rotation (AngleOfFlow / DirectionIsRelative),
// per-frame Bezier curve following at a constant arc-length speed, Z-offset control points,
// despawn on completion, spawner-death cleanup - plus the shadow-copy base test and a
// run-twice bit-determinism check.
//
// Definitions parse from INI text through the real parser, so the audited quantizing parse
// functions (ParseAngleDegrees, ParseFix64, ParseFixVector3) are on the tested path.

using System.Linq;
using System.Numerics;
using OpenSage;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class FloodUpdateContractTests
{
    // Straight collinear control points -> arc length is exactly |P3 - P0| = 100, so the
    // frame count to completion is an exact ceil(100 / speed) for every MemberSpeed test.
    private const string Definitions = @"
Object FloodMemberUnit
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object FloodSpawnerCurve
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = FloodUpdate ModuleTag_Flood
    AngleOfFlow = 0
    DirectionIsRelative = No
    FloodMember
      MemberTemplateName = FloodMemberUnit
      ControlPointOffsetOne = X:0 Y:0 Z:0
      ControlPointOffsetTwo = X:10 Y:20 Z:0
      ControlPointOffsetThree = X:20 Y:-20 Z:0
      ControlPointOffsetFour = X:30 Y:0 Z:0
      MemberSpeed = 6
    End
    FloodMember
      MemberTemplateName = FloodMemberUnit
      ControlPointOffsetOne = X:0 Y:5 Z:0
      ControlPointOffsetTwo = X:10 Y:5 Z:0
      ControlPointOffsetThree = X:20 Y:5 Z:0
      ControlPointOffsetFour = X:30 Y:5 Z:0
      MemberSpeed = 10
    End
  End
End

Object FloodSpawnerRotate90
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = FloodUpdate ModuleTag_Flood
    AngleOfFlow = 90
    DirectionIsRelative = No
    FloodMember
      MemberTemplateName = FloodMemberUnit
      ControlPointOffsetOne = X:0 Y:0 Z:0
      ControlPointOffsetTwo = X:10 Y:0 Z:0
      ControlPointOffsetThree = X:20 Y:0 Z:0
      ControlPointOffsetFour = X:30 Y:0 Z:0
      MemberSpeed = 30
    End
  End
End

Object FloodSpawnerRelative
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = FloodUpdate ModuleTag_Flood
    AngleOfFlow = 0
    DirectionIsRelative = Yes
    FloodMember
      MemberTemplateName = FloodMemberUnit
      ControlPointOffsetOne = X:0 Y:0 Z:0
      ControlPointOffsetTwo = X:10 Y:0 Z:0
      ControlPointOffsetThree = X:20 Y:0 Z:0
      ControlPointOffsetFour = X:30 Y:0 Z:0
      MemberSpeed = 5
    End
  End
End

Object FloodSpawnerSpeeds
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = FloodUpdate ModuleTag_Flood
    AngleOfFlow = 0
    DirectionIsRelative = No
    FloodMember
      MemberTemplateName = FloodMemberUnit
      ControlPointOffsetOne = X:0 Y:0 Z:0
      ControlPointOffsetTwo = X:33 Y:0 Z:0
      ControlPointOffsetThree = X:66 Y:0 Z:0
      ControlPointOffsetFour = X:100 Y:0 Z:0
      MemberSpeed = 16
    End
    FloodMember
      MemberTemplateName = FloodMemberUnit
      ControlPointOffsetOne = X:0 Y:10 Z:0
      ControlPointOffsetTwo = X:33 Y:10 Z:0
      ControlPointOffsetThree = X:66 Y:10 Z:0
      ControlPointOffsetFour = X:100 Y:10 Z:0
      MemberSpeed = 21
    End
    FloodMember
      MemberTemplateName = FloodMemberUnit
      ControlPointOffsetOne = X:0 Y:20 Z:0
      ControlPointOffsetTwo = X:33 Y:20 Z:0
      ControlPointOffsetThree = X:66 Y:20 Z:0
      ControlPointOffsetFour = X:100 Y:20 Z:0
      MemberSpeed = 25
    End
  End
End

Object FloodSpawnerElevated
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = FloodUpdate ModuleTag_Flood
    AngleOfFlow = 0
    DirectionIsRelative = No
    FloodMember
      MemberTemplateName = FloodMemberUnit
      ControlPointOffsetOne = X:0 Y:0 Z:0
      ControlPointOffsetTwo = X:10 Y:0 Z:40
      ControlPointOffsetThree = X:20 Y:0 Z:40
      ControlPointOffsetFour = X:30 Y:0 Z:0
      MemberSpeed = 5
    End
  End
End

Object FloodSpawnerSlow
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = FloodUpdate ModuleTag_Flood
    AngleOfFlow = 0
    DirectionIsRelative = No
    FloodMember
      MemberTemplateName = FloodMemberUnit
      ControlPointOffsetOne = X:0 Y:0 Z:0
      ControlPointOffsetTwo = X:100 Y:0 Z:0
      ControlPointOffsetThree = X:200 Y:0 Z:0
      ControlPointOffsetFour = X:300 Y:0 Z:0
      MemberSpeed = 5
    End
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xF10D) =>
        new HeadlessSimGame(SageGame.Bfme2, seed);

    private static HeadlessSimGame NewLoadedGame(uint seed = 0xF10D)
    {
        var game = NewGame(seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static FloodUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<FloodUpdate>().Single();

    private static void Step(HeadlessSimGame game, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            game.Step();
        }
    }

    // ------------------------------------------------------------------------------------------
    // Spawn: multiple FloodMembers, each on its own 4-control-point Bezier curve.
    // ------------------------------------------------------------------------------------------
    [Fact]
    public void Spawn_CreatesOneMemberPerFloodMemberEntry_OnItsOwnCurve()
    {
        var game = NewLoadedGame();
        var spawner = game.SpawnObject("FloodSpawnerCurve", game.CivilianPlayer, new Vector3(1000, 1000, 0));
        var flood = ModuleOf(spawner);

        // The module's sleepy-update registration wakes it on the frame after spawn (the
        // same shape HeightDieUpdateContractTests/DemoTrapUpdateContractTests document), so
        // this first Step() is a no-op; EnsureInitialized (which spawns the members) runs on
        // the second.
        game.Step();
        game.Step();

        Assert.Equal(2, flood.MemberCount);
        Assert.Equal(2, flood.ActiveMemberCount);
    }

    [Fact]
    public void CurvedMember_BowsAwayFromTheStraightLineBetweenItsEndpoints()
    {
        var game = NewLoadedGame();
        var spawner = game.SpawnObject("FloodSpawnerCurve", game.CivilianPlayer, new Vector3(1000, 1000, 0));
        var flood = ModuleOf(spawner);

        // First entry's control points bow through Y:+20 then Y:-20 around a Y:0 chord -
        // a straight-line mover would sit at Y:1000 (spawner Y) the whole way; a genuine
        // Bezier follower must leave that line partway through.
        // (Primer Step(): see Spawn_CreatesOneMemberPerFloodMemberEntry_OnItsOwnCurve.)
        game.Step();
        game.Step();
        var member = FindMemberByTemplate(game, spawner, memberIndex: 0);
        Assert.NotNull(member);

        var sawOffLine = false;
        for (var i = 0; i < 10 && !member.IsDestroyed; i++)
        {
            if (System.MathF.Abs(member.Transform.Translation.Y - 1000f) > 1f)
            {
                sawOffLine = true;
            }
            game.Step();
        }
        Assert.True(sawOffLine, "member never left the straight chord between its endpoints");
    }

    // ------------------------------------------------------------------------------------------
    // AngleOfFlow: absolute rotation of every control point at spawn.
    // ------------------------------------------------------------------------------------------
    [Fact]
    public void AngleOfFlow90_RotatesMembersToMovePerpendicularToTheirAuthoredAxis()
    {
        var game = NewLoadedGame();
        var spawnerPos = new Vector3(2000, 2000, 0);
        var spawner = game.SpawnObject("FloodSpawnerRotate90", game.CivilianPlayer, spawnerPos);
        // (Primer Step(): see Spawn_CreatesOneMemberPerFloodMemberEntry_OnItsOwnCurve.)
        game.Step();
        game.Step();

        var member = SoleMember(game, spawner);
        Assert.NotNull(member);

        // Authored curve runs along local +X (ControlPointOffsetFour = X:30 Y:0); rotated 90
        // degrees it must run along +Y instead: X stays at the spawner, Y moves away from it.
        Assert.True(System.MathF.Abs(member.Transform.Translation.X - spawnerPos.X) < 1f,
            $"member at {member.Transform.Translation} drifted off the spawner's X");
        Assert.True(member.Transform.Translation.Y > spawnerPos.Y + 1f,
            $"member at {member.Transform.Translation} did not move along +Y");
    }

    // ------------------------------------------------------------------------------------------
    // DirectionIsRelative: AngleOfFlow adds the spawner's own facing.
    // ------------------------------------------------------------------------------------------
    [Fact]
    public void DirectionIsRelative_SpawnerFacing180_InvertsTheFloodDirection()
    {
        var game = NewLoadedGame();
        var spawnerPos = new Vector3(3000, 3000, 0);
        var spawner = game.SpawnObject("FloodSpawnerRelative", game.CivilianPlayer, spawnerPos);

        // Face the spawner backwards (180 deg around Z) BEFORE the flood ever spawns -
        // EnsureInitialized reads the transform directly (not a locomotor), so this is
        // visible on frame 0 regardless of module update order.
        spawner.UpdateTransform(rotation: Quaternion.CreateFromAxisAngle(Vector3.UnitZ, System.MathF.PI));
        // (Primer Step(): see Spawn_CreatesOneMemberPerFloodMemberEntry_OnItsOwnCurve.)
        game.Step();
        game.Step();

        var member = SoleMember(game, spawner);
        Assert.NotNull(member);

        // Authored curve runs along local +X; with the spawner facing 180 deg the flood must
        // run along -X instead of +X.
        Assert.True(member.Transform.Translation.X < spawnerPos.X - 1f,
            $"member at {member.Transform.Translation} did not move along -X");
    }

    [Fact]
    public void DirectionIsRelative_SpawnerFacingZero_MatchesAbsoluteAngleOfFlow()
    {
        var game = NewLoadedGame();
        var spawnerPos = new Vector3(3500, 3500, 0);
        var spawner = game.SpawnObject("FloodSpawnerRelative", game.CivilianPlayer, spawnerPos);
        // (Primer Step(): see Spawn_CreatesOneMemberPerFloodMemberEntry_OnItsOwnCurve.)
        game.Step();
        game.Step();

        var member = SoleMember(game, spawner);
        Assert.NotNull(member);
        Assert.True(member.Transform.Translation.X > spawnerPos.X + 1f,
            $"member at {member.Transform.Translation} did not move along +X");
    }

    // ------------------------------------------------------------------------------------------
    // MemberSpeed: constant arc-length speed -> exact ceil(totalLength / speed) frame counts.
    // ------------------------------------------------------------------------------------------
    [Theory]
    [InlineData(0, 7)]   // speed 16, length 100 -> ceil(100/16) = 7
    [InlineData(1, 5)]   // speed 21, length 100 -> ceil(100/21) = 5
    [InlineData(2, 4)]   // speed 25, length 100 -> ceil(100/25) = 4
    public void MemberSpeed_ReachesEndpointAtTheExpectedFrameCount(int memberIndex, int expectedFrames)
    {
        var game = NewLoadedGame();
        var spawner = game.SpawnObject("FloodSpawnerSpeeds", game.CivilianPlayer, new Vector3(4000, 4000, 0));
        var flood = ModuleOf(spawner);

        // Primer Step(): see Spawn_CreatesOneMemberPerFloodMemberEntry_OnItsOwnCurve - the
        // module's sleepy-update registration wakes it one frame after spawn, so this first
        // Step() is a no-op.
        game.Step();
        game.Step(); // EnsureInitialized + first advance
        var memberId = flood.GetMember(memberIndex).MemberId;
        var member = game.GameLogic.GetObjectById(memberId);
        Assert.NotNull(member);

        for (var frame = 2; frame <= expectedFrames; frame++)
        {
            Assert.False(member.IsDestroyed, $"member {memberIndex} finished too early (frame {frame - 1})");
            game.Step();
        }

        Assert.True(member.IsDestroyed,
            $"member {memberIndex} (speed test) did not finish by frame {expectedFrames}");
    }

    // ------------------------------------------------------------------------------------------
    // Z-offset control points: elevate then settle back down.
    // ------------------------------------------------------------------------------------------
    [Fact]
    public void ZOffsetControlPoints_ElevateTheMemberMidFlightThenReturnToZero()
    {
        var game = NewLoadedGame();
        var spawner = game.SpawnObject("FloodSpawnerElevated", game.CivilianPlayer, new Vector3(5000, 5000, 0));
        // (Primer Step(): see Spawn_CreatesOneMemberPerFloodMemberEntry_OnItsOwnCurve.)
        game.Step();
        game.Step();
        var member = SoleMember(game, spawner);
        Assert.NotNull(member);

        var peakZ = 0f;
        for (var i = 0; i < 40 && member != null && !member.IsDestroyed; i++)
        {
            peakZ = System.MathF.Max(peakZ, member.Transform.Translation.Z);
            game.Step();
        }

        Assert.True(peakZ > 5f, $"member never rose above the endpoints (peak Z {peakZ})");
    }

    // ------------------------------------------------------------------------------------------
    // Despawn: spawner destroyed mid-animation -> the still-travelling member is cleaned up.
    // ------------------------------------------------------------------------------------------
    [Fact]
    public void SpawnerDestroyedMidFlight_StillActiveMemberIsCleanedUp()
    {
        var game = NewLoadedGame();
        var spawner = game.SpawnObject("FloodSpawnerSlow", game.CivilianPlayer, new Vector3(6000, 6000, 0));
        var flood = ModuleOf(spawner);

        // (Primer Step(): see Spawn_CreatesOneMemberPerFloodMemberEntry_OnItsOwnCurve.)
        game.Step();
        game.Step(); // member spawns and takes its first (small) step - far from the endpoint
        var memberId = flood.GetMember(0).MemberId;
        var member = game.GameLogic.GetObjectById(memberId);
        Assert.NotNull(member);
        Assert.False(member.IsDestroyed);
        Assert.True(flood.ActiveMemberCount > 0);

        game.GameLogic.DestroyObject(spawner);

        // OnDestroy runs synchronously inside DestroyObject: the member must already be
        // cleaned up (snapped to its endpoint and despawned), not left frozen mid-flight.
        Assert.True(member.IsDestroyed,
            "flood member was left stranded when its spawner was destroyed mid-animation");
    }

    // ------------------------------------------------------------------------------------------
    // Xfer: shadow-copy CRC equality mid-behavior + run-twice determinism.
    // ------------------------------------------------------------------------------------------
    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewLoadedGame();
        var host = game.SpawnObject("FloodSpawnerCurve", game.CivilianPlayer, new Vector3(7000, 7000, 0));
        Step(game, 3);
        var live = ModuleOf(host);

        var shadowHost = game.SpawnObject("FloodSpawnerCurve", game.CivilianPlayer, new Vector3(7500, 7500, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void RunTwice_FloodAnimation_IsBitDeterministic()
    {
        var gameA = NewLoadedGame(seed: 0xBEEF);
        var gameB = NewLoadedGame(seed: 0xBEEF);
        var spawnerA = gameA.SpawnObject("FloodSpawnerCurve", gameA.CivilianPlayer, new Vector3(8000, 8000, 0));
        var spawnerB = gameB.SpawnObject("FloodSpawnerCurve", gameB.CivilianPlayer, new Vector3(8000, 8000, 0));
        var floodA = ModuleOf(spawnerA);
        var floodB = ModuleOf(spawnerB);

        Step(gameA, 10);
        Step(gameB, 10);

        Assert.Equal(floodA.MemberCount, floodB.MemberCount);
        for (var i = 0; i < floodA.MemberCount; i++)
        {
            var a = floodA.GetMember(i);
            var b = floodB.GetMember(i);
            Assert.Equal(a.DistanceTraveled.RawValue, b.DistanceTraveled.RawValue);
            Assert.Equal(a.Done, b.Done);
            var objA = gameA.GameLogic.GetObjectById(a.MemberId);
            var objB = gameB.GameLogic.GetObjectById(b.MemberId);
            if (objA == null || objB == null)
            {
                Assert.Equal(objA == null, objB == null);
                continue;
            }
            Assert.Equal(objA.Transform.Translation, objB.Transform.Translation);
        }
    }

    // ---- helpers ----

    private static GameObject SoleMember(HeadlessSimGame game, GameObject spawner)
    {
        var flood = ModuleOf(spawner);
        Assert.Equal(1, flood.MemberCount);
        return game.GameLogic.GetObjectById(flood.GetMember(0).MemberId);
    }

    private static GameObject FindMemberByTemplate(HeadlessSimGame game, GameObject spawner, int memberIndex)
    {
        var flood = ModuleOf(spawner);
        return game.GameLogic.GetObjectById(flood.GetMember(memberIndex).MemberId);
    }
}
