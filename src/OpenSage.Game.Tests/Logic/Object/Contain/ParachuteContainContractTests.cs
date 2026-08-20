// Mocked-game unit tests for the ParachuteContain port (R12), one test group per packet
// testCase against a HeadlessSimGame host - the same [create -> tick -> observable effect]
// shape as EjectPilotDieContractTests / RailedTransportDockUpdateContractTests.
//
// The headless host builds no Drawable model (no DrawModules at all), so the PARA_COG /
// PARA_ATTACH / PARA_MAN bone lookups always miss and every bone offset falls back to zero -
// the same fallback RailedTransportDockUpdateContractTests documents for DOCKEND/DOCKWAITING.
// That makes the rider-positioning tests below check the *mechanism* (the rider tracks
// GameObject.Translation + the recomputed offset, every frame) rather than a nonzero bone
// vector. TerrainLogic.IsUnderwater is permanently stubbed to false (TerrainLogic.cs
// TODO(Port)), so the water-landing-kill branch cannot be driven true from a headless test;
// it is not asserted here for that reason, not because it isn't wired.
//
// AudioSystem is null on HeadlessSimGame (no audio host), so ParachuteOpenSound playing is
// exercised only as "doesn't throw when opening" - the null-conditional call site itself.

using System;
using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Contain;

public class ParachuteContainContractTests
{
    // Bfme2 runs at 5 Hz. PitchRateMax/RollRateMax = 90 deg/sec -> pi/180*90/5 rad/frame.
    private static readonly float MaxRateRadPerFrame = MathF.PI / 180f * 90f / 5f;

    private const string Definitions = @"
GameData
  Gravity = -1.0
End

Locomotor ChuteFreeFallLoco
  Surfaces = AIR
  Speed = 40
  TurnRate = 360
  Acceleration = 100
  Braking = 100
  Appearance = TWO_LEGS
  ZAxisBehavior = NO_Z_MOTIVE_FORCE
  PitchStiffness = 0.1
  RollStiffness = 0.1
  PitchDamping = 0.9
  RollDamping = 0.9
End

Locomotor ChuteOpenLoco
  Surfaces = AIR
  Speed = 10
  TurnRate = 360
  Acceleration = 100
  Braking = 100
  Appearance = TWO_LEGS
  ZAxisBehavior = NO_Z_MOTIVE_FORCE
  PitchStiffness = 0.2
  RollStiffness = 0.2
  PitchDamping = 0.5
  RollDamping = 0.5
  CloseEnoughDist = 1
End

Object TestChute
  KindOf = AIRCRAFT PARACHUTE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = PhysicsBehavior ModuleTag_Physics
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
  Behavior = ParachuteContain ModuleTag_Contain
    PitchRateMax = 90
    RollRateMax = 90
    LowAltitudeDamping = 0.3
    ParachuteOpenDist = 50
    KillWhenLandingInWaterSlop = 10
    FreeFallDamagePercent = 50%
    AllowInsideKindOf = INFANTRY
  End
  Locomotor = SET_NORMAL ChuteOpenLoco
  Locomotor = SET_FREEFALL ChuteFreeFallLoco
End

Object TestRider
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x9A7A) // "chute"-ish
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static ParachuteContain ContainOf(GameObject obj) =>
        obj.BehaviorModules.OfType<ParachuteContain>().Single();

    private static (GameObject Chute, GameObject Rider, ParachuteContain Contain) SpawnWithRider(
        HeadlessSimGame game, in Vector3 position)
    {
        var chute = game.SpawnObject("TestChute", game.CivilianPlayer, position);
        var rider = game.SpawnObject("TestRider", game.CivilianPlayer, position);
        var contain = ContainOf(chute);
        contain.AddRider(rider);
        return (chute, rider, contain);
    }

    // ---------------------------------------------------------------- opens at ParachuteOpenDist

    [Fact]
    public void DescendingPastOpenDist_OpensChute_SwitchesModelConditionOnBothObjects()
    {
        var game = NewGame();
        var (chute, rider, contain) = SpawnWithRider(game, new Vector3(0, 0, 200));

        game.Step(); // establishes _startZ = 200 (well above 2 * ParachuteOpenDist over ground)

        Assert.False(contain.IsOpened);
        Assert.True(rider.ModelConditionFlags.Get(ModelConditionFlag.FreeFall));
        Assert.False(rider.ModelConditionFlags.Get(ModelConditionFlag.Parachuting));

        chute.UpdateTransform(new Vector3(0, 0, 140)); // descended 60 >= ParachuteOpenDist (50)
        game.Step(); // opening happens this frame - no PlayAudioEvent crash even with a null AudioSystem

        Assert.True(contain.IsOpened);
        Assert.True(chute.ModelConditionFlags.Get(ModelConditionFlag.Parachuting));
        Assert.False(chute.ModelConditionFlags.Get(ModelConditionFlag.FreeFall));
        Assert.True(rider.ModelConditionFlags.Get(ModelConditionFlag.Parachuting));
        Assert.False(rider.ModelConditionFlags.Get(ModelConditionFlag.FreeFall));
    }

    [Fact]
    public void FudgesStartHeight_WhenEjectedTooCloseToGroundToOpen()
    {
        var game = NewGame();
        // Ejected at 60 units up, but ParachuteOpenDist is 50: 60 < 2*50, so GPL fudges the
        // recorded start height up to groundHeight + 2*ParachuteOpenDist = 100, giving the
        // chute room to actually open instead of slamming into the ground unopened.
        var (chute, _, contain) = SpawnWithRider(game, new Vector3(0, 0, 60));

        game.Step();
        Assert.False(contain.IsOpened);

        // Only 40 units below the (fudged) start of 100 - not yet past ParachuteOpenDist.
        chute.UpdateTransform(new Vector3(0, 0, 60));
        game.Step();
        Assert.False(contain.IsOpened);

        // Now 55 below the fudged start - past the threshold.
        chute.UpdateTransform(new Vector3(0, 0, 45));
        game.Step();
        Assert.True(contain.IsOpened);
    }

    // ---------------------------------------------------------------- pitch/roll spring-damper

    [Fact]
    public void InitialSwayRate_StaysWithinConfiguredPitchAndRollRateMax()
    {
        var game = NewGame();
        for (var i = 0; i < 8; i++)
        {
            var contain = ContainOf(game.SpawnObject("TestChute", game.CivilianPlayer, new Vector3(i * 200, 0, 200)));
            Assert.InRange(contain.PitchRate, -MaxRateRadPerFrame, MaxRateRadPerFrame);
            Assert.InRange(contain.RollRate, -MaxRateRadPerFrame, MaxRateRadPerFrame);
        }
    }

    [Fact]
    public void SpringDamper_AppliesLocomotorStiffnessAndDampingOnceOpened()
    {
        var game = NewGame();
        var (chute, _, contain) = SpawnWithRider(game, new Vector3(0, 0, 200));

        var pitchRate0 = contain.PitchRate;
        var rollRate0 = contain.RollRate;

        game.Step(); // unopened: no spring-damper yet
        Assert.Equal(pitchRate0, contain.PitchRate);
        Assert.Equal(0f, contain.Pitch);

        chute.UpdateTransform(new Vector3(0, 0, 140)); // opens; rider stays high (140 > 20), no altitude damping
        game.Step();

        // ChuteOpenLoco: PitchStiffness/RollStiffness 0.2, PitchDamping/RollDamping 0.5.
        var expectedPitchRate = pitchRate0 + (-0.2f * 0f) + (-0.5f * pitchRate0);
        var expectedRollRate = rollRate0 + (-0.2f * 0f) + (-0.5f * rollRate0);

        Assert.True(MathF.Abs(expectedPitchRate - contain.PitchRate) < 1e-4f);
        Assert.True(MathF.Abs(expectedRollRate - contain.RollRate) < 1e-4f);
        Assert.True(MathF.Abs(expectedPitchRate - contain.Pitch) < 1e-4f);
        Assert.True(MathF.Abs(expectedRollRate - contain.Roll) < 1e-4f);
    }

    [Fact]
    public void LowAltitudeDamping_AddsToLocomotorDamping_WhenRiderWithinTwentyUnitsOfGround()
    {
        var game = NewGame();
        var (chute, rider, contain) = SpawnWithRider(game, new Vector3(0, 0, 200));

        game.Step(); // establishes _startZ = 200

        // Move both the chute and the rider down into the low-altitude damping band (<= 20)
        // AND past ParachuteOpenDist in the same move.
        chute.UpdateTransform(new Vector3(0, 0, 15));
        rider.UpdateTransform(new Vector3(0, 0, 15));

        var pitchRate0 = contain.PitchRate;
        game.Step();

        // LowAltitudeDamping (0.3) adds to ChuteOpenLoco's PitchDamping (0.5).
        var expectedPitchRate = pitchRate0 + (-0.2f * 0f) + (-(0.5f + 0.3f) * pitchRate0);
        Assert.True(MathF.Abs(expectedPitchRate - contain.PitchRate) < 1e-4f);
    }

    // ---------------------------------------------------------------- rider positioning

    [Fact]
    public void PositionRider_TracksParachuteOffset_RecalculatedEveryFrame()
    {
        var game = NewGame();
        var (chute, rider, contain) = SpawnWithRider(game, new Vector3(10, 20, 200));

        // onContaining positions the rider immediately, at the parachute's PARA_ATTACH offset
        // (zero in this bone-less headless host, so rider == parachute position).
        Assert.Equal(chute.Translation + contain.RiderAttachOffset, rider.Translation);

        game.Step();
        Assert.Equal(chute.Translation + contain.RiderAttachOffset, rider.Translation);

        // Move the parachute; positionRider must recompute the offset and re-track it next tick.
        chute.UpdateTransform(new Vector3(50, 60, 200));
        game.Step();

        Assert.Equal(new Vector3(50, 60, 200), rider.Translation);
        Assert.Equal(chute.Translation + contain.RiderAttachOffset, rider.Translation);
    }

    // ---------------------------------------------------------------- collisions

    [Fact]
    public void Collisions_DisabledPreOpen_EnabledPostOpen_GroundCollisionEjectsAndKills()
    {
        var game = NewGame();
        var (chute, rider, contain) = SpawnWithRider(game, new Vector3(0, 0, 200));

        game.Step();
        Assert.True(chute.TestStatus(ObjectStatus.NoCollisions));
        Assert.True(rider.TestStatus(ObjectStatus.NoCollisions));

        chute.UpdateTransform(new Vector3(0, 0, 140));
        game.Step();
        Assert.True(contain.IsOpened);
        Assert.False(chute.TestStatus(ObjectStatus.NoCollisions));
        Assert.False(rider.TestStatus(ObjectStatus.NoCollisions));

        // other == null means "collide with ground" (GPL ParachuteContain::onCollide).
        ((ICollideModule)contain).OnCollide(null, Vector3.Zero, Vector3.Zero);

        Assert.Null(contain.Rider);
        Assert.True(chute.BodyModule.Health <= 0f);
    }

    // ---------------------------------------------------------------- landing destination

    [Fact]
    public void OverrideDestination_TargetsTheOverrideOnceOpened()
    {
        // GPL also calls locomotor->setUltraAccurate(TRUE) / setCloseEnoughDist(10.0) here;
        // Locomotor.cs has no runtime setter for either yet (see ParachuteContain.cs header),
        // so only the landing-target half of this behavior is observable from outside.
        var game = NewGame();
        var (chute, _, contain) = SpawnWithRider(game, new Vector3(0, 0, 200));

        contain.SetOverrideDestination(new Vector3(500, 500, 0));

        game.Step();
        chute.UpdateTransform(new Vector3(0, 0, 140));
        game.Step(); // opens; targets the override instead of searching for a clear spot

        Assert.True(contain.IsOpened);
        Assert.True(contain.IsLandingOverrideSet);
        Assert.Contains(chute.AIUpdate.TargetPoints, p => p == new Vector3(500, 500, 0));
    }

    [Fact]
    public void NoOverride_FindsClearSpotWithinHundredUnitRadius_AndTargetsIt()
    {
        var game = NewGame();
        var (chute, _, contain) = SpawnWithRider(game, new Vector3(0, 0, 200));

        game.Step();
        chute.UpdateTransform(new Vector3(0, 0, 140));
        game.Step();

        Assert.True(contain.IsOpened);
        Assert.False(contain.IsLandingOverrideSet);
        var target = Assert.Single(chute.AIUpdate.TargetPoints);

        // No other objects nearby, so the very first ring sample (10 units out) is clear.
        var horizontalDistance = MathF.Sqrt((target.X - 0f) * (target.X - 0f) + (target.Y - 0f) * (target.Y - 0f));
        Assert.InRange(horizontalDistance, 0f, 100f);
        Assert.True(MathF.Abs(target.X - 10f) < 1e-3f);
        Assert.True(MathF.Abs(target.Y - 0f) < 1e-3f);
    }

    // ---------------------------------------------------------------- destroyed mid-air / water

    [Fact]
    public void ParachuteDestroyedWhileSignificantlyAboveTerrain_EjectsRiderWithFreeFallDamage()
    {
        var game = NewGame();
        var (chute, rider, contain) = SpawnWithRider(game, new Vector3(0, 0, 200));

        Assert.True(chute.IsSignificantlyAboveTerrain);
        Assert.Equal(100f, rider.BodyModule.Health);

        PortedModuleTestKit.TriggerDeath(chute);

        Assert.Null(contain.Rider);
        // MaxHealth (100) * FreeFallDamagePercent (50%).
        Assert.Equal(50f, rider.BodyModule.Health);
    }

    [Fact]
    public void ParachuteDestroyedNearGround_DoesNotApplyFreeFallDamage()
    {
        var game = NewGame();
        var (chute, rider, contain) = SpawnWithRider(game, new Vector3(0, 0, 1));

        Assert.False(chute.IsSignificantlyAboveTerrain);

        PortedModuleTestKit.TriggerDeath(chute);

        // GPL only detaches/damages the rider when significantly airborne; on the ground the
        // rider stays contained (mirrors the module never having a reason to eject him here).
        Assert.NotNull(contain.Rider);
        Assert.Equal(100f, rider.BodyModule.Health);
    }

    // ---------------------------------------------------------------- containment gate

    [Fact]
    public void IsValidContainerFor_RejectsASecondRider()
    {
        var game = NewGame();
        var chute = game.SpawnObject("TestChute", game.CivilianPlayer, new Vector3(0, 0, 200));
        var riderA = game.SpawnObject("TestRider", game.CivilianPlayer, new Vector3(0, 0, 200));
        var riderB = game.SpawnObject("TestRider", game.CivilianPlayer, new Vector3(0, 0, 200));
        var contain = ContainOf(chute);

        Assert.True(contain.IsValidContainerFor(riderA));
        contain.AddRider(riderA);
        Assert.False(contain.IsValidContainerFor(riderB));
    }
}
