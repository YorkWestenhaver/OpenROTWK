// Mocked-game contract tests for the SabotagePowerPlantCrateCollide port (R12): the crate's
// real OnCollide dispatch, driven directly (the way PartitionCellManager would call it),
// covering every branch the task packet's testCases enumerate.
//
// This module is legacy (GameObject, IGameEngine), not [SimState] - the Collide category has
// no SimCore host yet - so there is no Xfer/shadow-copy CRC test here (PortedModuleTestKit's
// CRC helpers are for ported [SimState] modules only); Load() persistence follows the same
// legacy positional pattern as its landed CrateCollide siblings.

using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Collide;

public class SabotagePowerPlantCrateCollideContractTests
{
    private const string Definitions = @"
Object Saboteur
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
  Behavior = SabotagePowerPlantCrateCollide ModuleTag_Sabotage
    SabotagePowerDuration = 5
    BuildingPickup = Yes
  End
End

Object PowerPlant
  KindOf = STRUCTURE FS_POWER
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object Barracks
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC0DE)
    {
        var game = new HeadlessSimGame(SageGame.CncGeneralsZeroHour, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static SabotagePowerPlantCrateCollide ModuleOf(GameObject obj) =>
        obj.FindBehavior<SabotagePowerPlantCrateCollide>();

    // Makes the two players mutual enemies, mirroring EnemyNearUpdateContractTests'
    // Player.Enemies convention.
    private static void MakeEnemies(Player a, Player b)
    {
        a.AddEnemy(b);
        b.AddEnemy(a);
    }

    /// <summary>
    /// Runs one full engine tick (GameLogic.Update + the PlayerManager tick Scene3D.LogicTick
    /// would otherwise drive) so Player.LogicTick's power-restore check actually runs -
    /// HeadlessSimGame.Step() only advances GameLogic (design note: no Scene3D host ticks
    /// PlayerManager in the headless harness), so the test drives it explicitly.
    /// </summary>
    private static void FullStep(HeadlessSimGame game)
    {
        // Real frame order (Game.cs): GameLogic.Update() (advances CurrentFrame) THEN
        // Scene3D.LogicTick (which drives PlayerManager.LogicTick) - matched here so the
        // restore check observes the frame the way it would in a real tick.
        game.Step();
        game.PlayerManager.LogicTick();
    }

    [Fact]
    public void ValidSabotage_OutagePersistsForDuration_ThenAutoRestores()
    {
        var game = NewGame();
        // The saboteur's own owner isn't neutral-checked; only the TARGET is, so the
        // saboteur can be owned by the neutral player (mirrors EnemyNearUpdateContractTests'
        // "Grunt owned by neutral" shape) while the target (the enemy) is the civilian player.
        var saboteur = game.SpawnObject("Saboteur", game.PlayerManager.NeutralPlayer, Vector3.Zero);
        var plant = game.SpawnObject("PowerPlant", game.CivilianPlayer, Vector3.Zero);
        MakeEnemies(game.PlayerManager.NeutralPlayer, game.CivilianPlayer);

        // GPL requires the saboteur to have been explicitly AI-ordered onto THIS specific
        // object (ai->getGoalObject() != other rejects otherwise, including the default
        // no-order case) - "prevent an unintentional conversion simply by having the
        // terrorist walk too close to it."
        saboteur.AIUpdate.SetCurrentVictim(plant.Id);

        Assert.False(game.CivilianPlayer.HasInsufficientPower);

        ModuleOf(saboteur).OnCollide(plant, Vector3.Zero, Vector3.Zero);

        Assert.True(game.CivilianPlayer.HasInsufficientPower);
        Assert.True(saboteur.IsDestroyed);

        // SabotagePowerDuration = 5: still out at frame 4, restored by frame 5.
        for (var i = 0; i < 4; i++)
        {
            FullStep(game);
            Assert.True(game.CivilianPlayer.HasInsufficientPower);
        }
        FullStep(game);
        Assert.False(game.CivilianPlayer.HasInsufficientPower);
    }

    [Fact]
    public void UnorderedContact_NoGoalObjectSet_Rejected()
    {
        // GPL's ai->getGoalObject() != other rejects whenever the AI's goal object is not
        // exactly `other` - including (and especially) the default/common case where no
        // goal object has ever been set, i.e. a saboteur that merely collides with an enemy
        // power plant without ever being explicitly ordered to sabotage it (attack-move,
        // patrol, wandering into it, ...) must NOT trigger the outage.
        var game = NewGame();
        var saboteur = game.SpawnObject("Saboteur", game.PlayerManager.NeutralPlayer, Vector3.Zero);
        var plant = game.SpawnObject("PowerPlant", game.CivilianPlayer, Vector3.Zero);
        MakeEnemies(game.PlayerManager.NeutralPlayer, game.CivilianPlayer);

        // Deliberately no SetCurrentVictim call: CurrentVictimId stays at its default
        // (Invalid), matching a fresh/un-ordered AIUpdate's null goal object in GPL.

        ModuleOf(saboteur).OnCollide(plant, Vector3.Zero, Vector3.Zero);

        Assert.False(game.CivilianPlayer.HasInsufficientPower);
        Assert.False(saboteur.IsDestroyed);
    }

    [Fact]
    public void DeadTarget_Rejected()
    {
        var game = NewGame();
        var saboteur = game.SpawnObject("Saboteur", game.PlayerManager.NeutralPlayer, Vector3.Zero);
        var plant = game.SpawnObject("PowerPlant", game.CivilianPlayer, Vector3.Zero);
        MakeEnemies(game.PlayerManager.NeutralPlayer, game.CivilianPlayer);

        plant.IsEffectivelyDead = true;

        ModuleOf(saboteur).OnCollide(plant, Vector3.Zero, Vector3.Zero);

        Assert.False(game.CivilianPlayer.HasInsufficientPower);
        Assert.False(saboteur.IsDestroyed);
    }

    [Fact]
    public void NonPowerBuilding_Rejected()
    {
        var game = NewGame();
        var saboteur = game.SpawnObject("Saboteur", game.PlayerManager.NeutralPlayer, Vector3.Zero);
        var barracks = game.SpawnObject("Barracks", game.CivilianPlayer, Vector3.Zero);
        MakeEnemies(game.PlayerManager.NeutralPlayer, game.CivilianPlayer);

        ModuleOf(saboteur).OnCollide(barracks, Vector3.Zero, Vector3.Zero);

        Assert.False(game.CivilianPlayer.HasInsufficientPower);
        Assert.False(saboteur.IsDestroyed);
    }

    [Fact]
    public void NonEnemy_Rejected()
    {
        var game = NewGame();
        var saboteur = game.SpawnObject("Saboteur", game.PlayerManager.NeutralPlayer, Vector3.Zero);
        var plant = game.SpawnObject("PowerPlant", game.CivilianPlayer, Vector3.Zero);
        // Deliberately no MakeEnemies: default relationship is not ENEMIES.

        ModuleOf(saboteur).OnCollide(plant, Vector3.Zero, Vector3.Zero);

        Assert.False(game.CivilianPlayer.HasInsufficientPower);
        Assert.False(saboteur.IsDestroyed);
    }

    [Fact]
    public void AIGoalMismatch_Rejected()
    {
        var game = NewGame();
        var saboteur = game.SpawnObject("Saboteur", game.PlayerManager.NeutralPlayer, Vector3.Zero);
        var plant = game.SpawnObject("PowerPlant", game.CivilianPlayer, Vector3.Zero);
        var decoy = game.SpawnObject("Barracks", game.CivilianPlayer, new Vector3(50, 0, 0));
        MakeEnemies(game.PlayerManager.NeutralPlayer, game.CivilianPlayer);

        // The hijacker AI ordered this saboteur at a DIFFERENT object than the one it collided
        // with - isValidToExecute passes (the plant is a valid victim in isolation), but
        // executeCrateBehavior's own goal check must veto it.
        saboteur.AIUpdate.SetCurrentVictim(decoy.Id);

        ModuleOf(saboteur).OnCollide(plant, Vector3.Zero, Vector3.Zero);

        Assert.False(game.CivilianPlayer.HasInsufficientPower);
        Assert.False(saboteur.IsDestroyed);
    }

    [Fact]
    public void LocalPlayerVictim_QueuesBuildingSabotagedEvaEvent()
    {
        var game = NewGame();
        var saboteur = game.SpawnObject("Saboteur", game.PlayerManager.NeutralPlayer, Vector3.Zero);
        var plant = game.SpawnObject("PowerPlant", game.CivilianPlayer, Vector3.Zero);
        MakeEnemies(game.PlayerManager.NeutralPlayer, game.CivilianPlayer);
        game.LocalPlayer = game.CivilianPlayer;
        saboteur.AIUpdate.SetCurrentVictim(plant.Id); // explicit sabotage order, required to succeed

        ModuleOf(saboteur).OnCollide(plant, Vector3.Zero, Vector3.Zero);

        Assert.Contains("BuildingSabotaged", game.CivilianPlayer.PendingEvaEvents);
    }

    [Fact]
    public void NonLocalPlayerVictim_DoesNotQueueEvaEvent()
    {
        var game = NewGame();
        var saboteur = game.SpawnObject("Saboteur", game.PlayerManager.NeutralPlayer, Vector3.Zero);
        var plant = game.SpawnObject("PowerPlant", game.CivilianPlayer, Vector3.Zero);
        MakeEnemies(game.PlayerManager.NeutralPlayer, game.CivilianPlayer);
        game.LocalPlayer = game.PlayerManager.NeutralPlayer; // the attacker, not the victim
        saboteur.AIUpdate.SetCurrentVictim(plant.Id); // explicit sabotage order, required to succeed

        ModuleOf(saboteur).OnCollide(plant, Vector3.Zero, Vector3.Zero);

        Assert.Empty(game.CivilianPlayer.PendingEvaEvents);
    }
}
