// Mocked-game contract tests for the SabotageCommandCenterCrateCollide port (R12): the two
// real decision points the class exposes - IsValidToExecute and ExecuteCrateBehavior (see the
// module's own header for why OnCollide dispatch itself is out of scope) - covering every
// testCase in the R12 task packet.
//
// HeadlessSimGame's default two players (Players[0], nicknamed Enemy below, and CivilianPlayer)
// carry no map-authored alliance data, so - matching the documented workaround this module
// shares with CreateCrateDie.KillerIsAlliedWithVictim - tests that need a live ENEMIES
// relationship set it explicitly via Player.Enemies.

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Collide;

public class SabotageCommandCenterCrateCollideContractTests
{
    private const string Definitions = @"
SpecialPower TestSpecialPowerA
  Enum = SPECIAL_CASH_HACK
  ReloadTime = 100
End

SpecialPower TestSpecialPowerB
  Enum = SPECIAL_SPY_DRONE
  ReloadTime = 100
End

Object TestCommandCenter
  KindOf = COMMANDCENTER STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = SpecialPowerModule ModuleTag_SP1
    SpecialPowerTemplate = TestSpecialPowerA
  End
  Behavior = SpecialPowerModule ModuleTag_SP2
    SpecialPowerTemplate = TestSpecialPowerB
  End
End

Object PlainStructure
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
End

Object TestSaboteur
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
  Behavior = SabotageCommandCenterCrateCollide ModuleTag_Sabotage
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC0DE)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static Player Enemy(HeadlessSimGame game) => game.PlayerManager.Players[0];

    private static SabotageCommandCenterCrateCollide SabotageModuleOf(GameObject obj) =>
        obj.FindBehavior<SabotageCommandCenterCrateCollide>();

    // ---- IsValidToExecute ----

    [Fact]
    public void ValidTarget_EnemyCommandCenterAlive_IsValid()
    {
        var game = NewGame();
        game.CivilianPlayer.Enemies.Add(Enemy(game));

        var saboteur = game.SpawnObject("TestSaboteur", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("TestCommandCenter", Enemy(game), new Vector3(5, 0, 0));

        Assert.True(SabotageModuleOf(saboteur).IsValidToExecute(target));
    }

    [Fact]
    public void NonEnemyRelationship_RejectsTheTarget()
    {
        var game = NewGame();
        // Deliberately NOT adding to Enemies: default relationship reads NEUTRAL.

        var saboteur = game.SpawnObject("TestSaboteur", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("TestCommandCenter", Enemy(game), new Vector3(5, 0, 0));

        Assert.False(SabotageModuleOf(saboteur).IsValidToExecute(target));
    }

    [Fact]
    public void AlliedRelationship_RejectsTheTarget()
    {
        var game = NewGame();
        var enemy = Enemy(game);
        // An explicit ally is still not ENEMIES, so it must still be rejected.
        game.CivilianPlayer.Allies.Add(enemy);

        var saboteur = game.SpawnObject("TestSaboteur", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("TestCommandCenter", enemy, new Vector3(5, 0, 0));

        Assert.False(SabotageModuleOf(saboteur).IsValidToExecute(target));
    }

    [Fact]
    public void NonCommandCenterTarget_RejectsEvenWhenEnemy()
    {
        var game = NewGame();
        game.CivilianPlayer.Enemies.Add(Enemy(game));

        var saboteur = game.SpawnObject("TestSaboteur", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("PlainStructure", Enemy(game), new Vector3(5, 0, 0));

        Assert.False(SabotageModuleOf(saboteur).IsValidToExecute(target));
    }

    [Fact]
    public void DeadCommandCenter_RejectsEvenWhenEnemy()
    {
        var game = NewGame();
        game.CivilianPlayer.Enemies.Add(Enemy(game));

        var saboteur = game.SpawnObject("TestSaboteur", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("TestCommandCenter", Enemy(game), new Vector3(5, 0, 0));
        target.IsEffectivelyDead = true;

        Assert.False(SabotageModuleOf(saboteur).IsValidToExecute(target));
    }

    // ---- ExecuteCrateBehavior ----

    [Fact]
    public void GoalObjectMismatch_FailsAndConsumesNothing()
    {
        var game = NewGame();
        game.CivilianPlayer.Enemies.Add(Enemy(game));

        var saboteur = game.SpawnObject("TestSaboteur", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("TestCommandCenter", Enemy(game), new Vector3(5, 0, 0));
        var somethingElse = game.SpawnObject("PlainStructure", Enemy(game), new Vector3(10, 0, 0));

        var specialPowers = target.FindBehaviors<SpecialPowerModule>().ToList();
        AdvancePastRecharge(game, specialPowers);
        Assert.All(specialPowers, sp => Assert.True(sp.Ready));

        var result = SabotageModuleOf(saboteur).ExecuteCrateBehavior(target, somethingElse);

        Assert.False(result);
        // Nothing was reset: the powers are still ready, exactly as before the failed call.
        Assert.All(specialPowers, sp => Assert.True(sp.Ready));
    }

    [Fact]
    public void SuccessfulSabotage_ResetsEverySpecialPowerOnTheTarget()
    {
        var game = NewGame();
        game.CivilianPlayer.Enemies.Add(Enemy(game));

        var saboteur = game.SpawnObject("TestSaboteur", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("TestCommandCenter", Enemy(game), new Vector3(5, 0, 0));

        var specialPowers = target.FindBehaviors<SpecialPowerModule>().ToList();
        Assert.Equal(2, specialPowers.Count);
        AdvancePastRecharge(game, specialPowers);
        Assert.All(specialPowers, sp => Assert.True(sp.Ready));

        var result = SabotageModuleOf(saboteur).ExecuteCrateBehavior(target, target);

        Assert.True(result);
        Assert.All(specialPowers, sp =>
        {
            Assert.False(sp.Ready);
            Assert.Equal(0f, sp.ReadyProgress());
        });
    }

    [Fact]
    public void NullGoalObject_FailsJustLikeAMismatch()
    {
        var game = NewGame();
        game.CivilianPlayer.Enemies.Add(Enemy(game));

        var saboteur = game.SpawnObject("TestSaboteur", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("TestCommandCenter", Enemy(game), new Vector3(5, 0, 0));

        // No AI at all (or an AI with no current goal) reads as a null goal object.
        var result = SabotageModuleOf(saboteur).ExecuteCrateBehavior(target, null);

        Assert.False(result);
    }

    private static void AdvancePastRecharge(HeadlessSimGame game, List<SpecialPowerModule> specialPowers)
    {
        for (var i = 0; i < 60 && !specialPowers.All(sp => sp.Ready); i++)
        {
            game.Step();
        }
    }
}
