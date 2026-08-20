// Contract tests for the VeterancyCrateCollide port (R12): one test per packet testCase,
// driven directly through OnCollide on a real (headless) game, mirroring the shape of
// HordeMemberCollideContractTests and EjectPilotDieContractTests in this directory/batch.
//
// VeterancyCrateCollide is a legacy (non-[SimState]) CollideModule: it lives outside the
// SimCore float-quarantine scope (SimCoreScopedDirs.txt has no Collide/ entry yet), so these
// tests assert on ExperienceTracker/GameObject state directly rather than through the Xfer/CRC
// shadow-copy kit, which only the Sim-tagged module family in PortedModuleTestKit exercises.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Collide;

public class VeterancyCrateCollideContractTests
{
    private const string Definitions = @"
Locomotor TestGroundLoco
  Surfaces = GROUND
  Speed = 30
  TurnRate = 360
  Acceleration = 100
  Braking = 100
  Appearance = TWO_LEGS
  ZAxisBehavior = NO_Z_MOTIVE_FORCE
End

Locomotor TestAirLoco
  Surfaces = AIR
  Speed = 30
  TurnRate = 360
  Acceleration = 100
  Braking = 100
  Appearance = TWO_LEGS
  ZAxisBehavior = NO_Z_MOTIVE_FORCE
End

FXList FX_VetCrateTest
End

Object VetCrateTestUnit
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  IsTrainable = Yes
  ExperienceRequired = 0 100 200 300
End

Object VetCrateTestVehicleGround
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  IsTrainable = Yes
  ExperienceRequired = 0 100 200 300
  Behavior = AIUpdate ModuleTag_AI
  End
  Locomotor = SET_NORMAL TestGroundLoco
End

Object VetCrateTestVehicleAir
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  IsTrainable = Yes
  ExperienceRequired = 0 100 200 300
  Behavior = AIUpdate ModuleTag_AI
  End
  Locomotor = SET_NORMAL TestAirLoco
End

Object VetCrateRangeZero
  Behavior = VeterancyCrateCollide ModuleTag_Vet
    EffectRange = 0
  End
End

Object VetCrateRangeWide
  Behavior = VeterancyCrateCollide ModuleTag_Vet
    EffectRange = 200
  End
End

Object VetCrateOwnerVeterancy
  ExperienceRequired = 0 100 200 300
  Behavior = VeterancyCrateCollide ModuleTag_Vet
    EffectRange = 0
    AddsOwnerVeterancy = Yes
  End
End

Object VetCratePilot
  Behavior = VeterancyCrateCollide ModuleTag_Vet
    EffectRange = 0
    IsPilot = Yes
  End
End

Object VetCrateCapped
  Behavior = VeterancyCrateCollide ModuleTag_Vet
    EffectRange = 0
    AffectsUpToLevel = 1
  End
End

Object VetCrateFx
  Behavior = VeterancyCrateCollide ModuleTag_Vet
    EffectRange = 0
    ExecuteFX = FX_VetCrateTest
  End
End
";

    private static HeadlessSimGame NewGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2Rotwk, matchSeed: 0x5A17u);

        // Two non-neutral, non-civilian players with distinct identities, so pilot-mode
        // same-owner/different-owner checks have something real to compare.
        game.PlayerManager.OnNewGame(
            new[]
            {
                OpenSage.Data.Map.Player.CreateNeutralPlayer(),
                OpenSage.Data.Map.Player.CreateCivilianPlayer(),
                new OpenSage.Data.Map.Player { Name = "plyrAlpha", Faction = "FactionAlpha" },
                new OpenSage.Data.Map.Player { Name = "plyrBravo", Faction = "FactionBravo" },
            },
            GameType.Skirmish);

        game.LoadIniText(Definitions);
        return game;
    }

    private static Player PlayerAlpha(HeadlessSimGame game) => game.PlayerManager.GetPlayerByName("plyrAlpha");
    private static Player PlayerBravo(HeadlessSimGame game) => game.PlayerManager.GetPlayerByName("plyrBravo");

    private static VeterancyCrateCollide CrateModuleOf(GameObject crate) => crate.FindBehavior<VeterancyCrateCollide>();

    // ---- "Unit collision with EffectRange=0 grants exactly 1 level to collider" ----

    [Fact]
    public void RangeZero_GrantsExactlyOneLevelToCollider()
    {
        var game = NewGame();
        var crate = game.SpawnObject("VetCrateRangeZero", game.CivilianPlayer, Vector3.Zero);
        var unit = game.SpawnObject("VetCrateTestUnit", PlayerAlpha(game), Vector3.Zero);

        CrateModuleOf(crate).OnCollide(unit, Vector3.Zero, Vector3.Zero);

        Assert.Equal(VeterancyLevel.Veteran, unit.Rank);
        Assert.True(crate.IsDestroyed);
    }

    // ---- "Multiple units within EffectRange radius all receive experience grant" ----

    [Fact]
    public void RangeGreaterThanZero_GrantsToEveryUnitOfTheColliderPlayerInRange()
    {
        var game = NewGame();
        var crate = game.SpawnObject("VetCrateRangeWide", game.CivilianPlayer, Vector3.Zero);

        var collider = game.SpawnObject("VetCrateTestUnit", PlayerAlpha(game), new Vector3(0, 0, 0));
        var nearbyAlly = game.SpawnObject("VetCrateTestUnit", PlayerAlpha(game), new Vector3(20, 0, 0));
        var nearbyEnemy = game.SpawnObject("VetCrateTestUnit", PlayerBravo(game), new Vector3(10, 0, 0));

        CrateModuleOf(crate).OnCollide(collider, Vector3.Zero, Vector3.Zero);

        Assert.Equal(VeterancyLevel.Veteran, collider.Rank);
        Assert.Equal(VeterancyLevel.Veteran, nearbyAlly.Rank);
        Assert.Equal(VeterancyLevel.Regular, nearbyEnemy.Rank);
    }

    // ---- "AddsOwnerVeterancy=true grants crate owner veterancy level instead of 1" ----

    [Fact]
    public void AddsOwnerVeterancy_GrantsTheCratesOwnVeterancyLevel()
    {
        var game = NewGame();
        var crate = game.SpawnObject("VetCrateOwnerVeterancy", game.CivilianPlayer, Vector3.Zero);
        crate.ExperienceTracker.SetVeterancyLevel(VeterancyLevel.Elite, provideFeedback: false);

        var unit = game.SpawnObject("VetCrateTestUnit", PlayerAlpha(game), Vector3.Zero);

        CrateModuleOf(crate).OnCollide(unit, Vector3.Zero, Vector3.Zero);

        Assert.Equal(VeterancyLevel.Elite, unit.Rank);
    }

    // ---- "IsPilot=true rejects vehicles not controlled by crate owner" ----

    [Fact]
    public void Pilot_RejectsVehicleControlledByADifferentPlayer()
    {
        var game = NewGame();
        var crate = game.SpawnObject("VetCratePilot", PlayerAlpha(game), Vector3.Zero);
        var otherPlayersVehicle = game.SpawnObject("VetCrateTestVehicleGround", PlayerBravo(game), Vector3.Zero);

        CrateModuleOf(crate).OnCollide(otherPlayersVehicle, Vector3.Zero, Vector3.Zero);

        Assert.Equal(VeterancyLevel.Regular, otherPlayersVehicle.Rank);
        Assert.False(crate.IsDestroyed);
    }

    [Fact]
    public void Pilot_AcceptsVehicleControlledBySameOwner()
    {
        var game = NewGame();
        var crate = game.SpawnObject("VetCratePilot", PlayerAlpha(game), Vector3.Zero);
        var ownVehicle = game.SpawnObject("VetCrateTestVehicleGround", PlayerAlpha(game), Vector3.Zero);

        CrateModuleOf(crate).OnCollide(ownVehicle, Vector3.Zero, Vector3.Zero);

        Assert.Equal(VeterancyLevel.Veteran, ownVehicle.Rank);
        Assert.True(crate.IsDestroyed);
    }

    // ---- "IsPilot=true rejects airborne-locomotor units" ----

    [Fact]
    public void Pilot_RejectsAirborneLocomotorVehicle()
    {
        var game = NewGame();
        var crate = game.SpawnObject("VetCratePilot", PlayerAlpha(game), Vector3.Zero);
        var airVehicle = game.SpawnObject("VetCrateTestVehicleAir", PlayerAlpha(game), Vector3.Zero);
        Assert.True(airVehicle.IsUsingAirborneLocomotor());

        CrateModuleOf(crate).OnCollide(airVehicle, Vector3.Zero, Vector3.Zero);

        Assert.Equal(VeterancyLevel.Regular, airVehicle.Rank);
        Assert.False(crate.IsDestroyed);
    }

    // ---- "Dead or incapacitated units are rejected from reward" ----

    [Fact]
    public void EffectivelyDeadUnit_IsRejected()
    {
        var game = NewGame();
        var crate = game.SpawnObject("VetCrateRangeZero", game.CivilianPlayer, Vector3.Zero);
        var unit = game.SpawnObject("VetCrateTestUnit", PlayerAlpha(game), Vector3.Zero);

        PortedModuleTestKit.TriggerDeath(unit);
        Assert.True(unit.IsEffectivelyDead);

        CrateModuleOf(crate).OnCollide(unit, Vector3.Zero, Vector3.Zero);

        Assert.Equal(VeterancyLevel.Regular, unit.Rank);
        Assert.False(crate.IsDestroyed);
    }

    // ---- "Units at or above AffectsUpToLevel cap reject the experience (BFME)" ----

    [Fact]
    public void AffectsUpToLevel_RejectsUnitsAtOrAboveTheCap()
    {
        var game = NewGame();

        var crateBelowCap = game.SpawnObject("VetCrateCapped", game.CivilianPlayer, Vector3.Zero);
        var unitBelowCap = game.SpawnObject("VetCrateTestUnit", PlayerAlpha(game), Vector3.Zero);
        Assert.Equal(VeterancyLevel.Regular, unitBelowCap.Rank);

        CrateModuleOf(crateBelowCap).OnCollide(unitBelowCap, Vector3.Zero, Vector3.Zero);
        Assert.Equal(VeterancyLevel.Veteran, unitBelowCap.Rank);

        var crateAtCap = game.SpawnObject("VetCrateCapped", game.CivilianPlayer, Vector3.Zero);
        var unitAtCap = game.SpawnObject("VetCrateTestUnit", PlayerAlpha(game), Vector3.Zero);
        unitAtCap.ExperienceTracker.SetVeterancyLevel(VeterancyLevel.Veteran, provideFeedback: false);

        CrateModuleOf(crateAtCap).OnCollide(unitAtCap, Vector3.Zero, Vector3.Zero);

        Assert.Equal(VeterancyLevel.Veteran, unitAtCap.Rank);
        Assert.False(crateAtCap.IsDestroyed);
    }

    // ---- "ExecuteFX asset plays on crate activation (BFME)" ----

    [Fact]
    public void ExecuteFX_PlaysOnActivation_AsPartOfASuccessfulGrant()
    {
        var game = NewGame();
        var crate = game.SpawnObject("VetCrateFx", game.CivilianPlayer, Vector3.Zero);
        var unit = game.SpawnObject("VetCrateTestUnit", PlayerAlpha(game), Vector3.Zero);

        var data = Assert.IsType<VeterancyCrateCollideModuleData>(
            game.AssetStore.ObjectDefinitions.GetByName("VetCrateFx")
                .Behaviors.Values.Single(b => b.Data is VeterancyCrateCollideModuleData).Data);
        Assert.NotNull(data.ExecuteFX);
        Assert.NotNull(data.ExecuteFX.Value);

        // Executing the FX list (empty of nuggets in this headless host) must not throw, and
        // must happen only as part of a successful activation: the crate is consumed and the
        // unit is promoted in the same call.
        CrateModuleOf(crate).OnCollide(unit, Vector3.Zero, Vector3.Zero);

        Assert.Equal(VeterancyLevel.Veteran, unit.Rank);
        Assert.True(crate.IsDestroyed);
    }
}
