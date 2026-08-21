// Mocked-game contract tests for the SabotageCommandCenterCrateCollide port (R12): the two
// real decision points the class exposes - IsValidToExecute and ExecuteCrateBehavior (see the
// module's own header for why OnCollide dispatch itself is out of scope) - covering every
// testCase in the R12 task packet.
//
// The fixture stands up a real third player for the victim. R13.5: this module now runs the
// shared CrateCollide::isValidToExecute gate first, and that gate rejects anything owned by the
// NEUTRAL player ("Nothing Neutral can pick up any type of crate") - the old fixture used
// PlayerManager.Players[0], which IS the neutral player, so every case would have been rejected
// for the wrong reason. Players carry no map-authored alliance data here, so - matching the
// documented workaround this module shares with CreateCrateDie.KillerIsAlliedWithVictim - tests
// that need a live ENEMIES relationship set it explicitly via Player.Enemies.

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
    ; R13.5: the shared gate rejects an AI-less target unless BuildingPickup covers a
    ; STRUCTURE - the real GPL requirement for a building-kinded victim.
    BuildingPickup = Yes
  End
End

Object TestSaboteurNoBuildingPickup
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
  Behavior = SabotageCommandCenterCrateCollide ModuleTag_Sabotage
  End
End

Object TestSaboteurRequiresCommandCenterAndStructure
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
  Behavior = SabotageCommandCenterCrateCollide ModuleTag_Sabotage
    BuildingPickup = Yes
    RequiredKindOf = COMMANDCENTER STRUCTURE
  End
End

Object TestSaboteurRequiresUnsatisfiableMask
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
  Behavior = SabotageCommandCenterCrateCollide ModuleTag_Sabotage
    BuildingPickup = Yes
    RequiredKindOf = COMMANDCENTER VEHICLE
  End
End

Object TestSaboteurForbidsStructure
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
  Behavior = SabotageCommandCenterCrateCollide ModuleTag_Sabotage
    BuildingPickup = Yes
    ForbiddenKindOf = STRUCTURE
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC0DE)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);

        // A real, non-neutral victim player: the shared CrateCollide gate rejects everything
        // owned by the neutral player, which is exactly what PlayerManager.Players[0] is.
        game.PlayerManager.OnNewGame(
            new[]
            {
                OpenSage.Data.Map.Player.CreateNeutralPlayer(),
                OpenSage.Data.Map.Player.CreateCivilianPlayer(),
                new OpenSage.Data.Map.Player { Name = "plyrVictim", Faction = "FactionVictim", IsHuman = true },
            },
            GameType.Skirmish);

        game.LoadIniText(Definitions);
        return game;
    }

    private static Player Enemy(HeadlessSimGame game) => game.PlayerManager.GetPlayerByName("plyrVictim");

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

    // ---- Shared CrateCollide::isValidToExecute base gate (R13.5, crate-gate) ----
    //
    // These cases are the base gate's, not this leaf's: each one uses a target that the
    // leaf's own three checks (alive, COMMANDCENTER, ENEMIES) would happily accept, so a
    // rejection here can only come from the hoisted gate.

    // "Nothing Neutral can pick up any type of crate" (CrateCollide.cpp).
    [Fact]
    public void NeutralOwnedTarget_IsRejectedByTheBaseGate()
    {
        var game = NewGame();
        var neutral = game.PlayerManager.NeutralPlayer;
        game.CivilianPlayer.Enemies.Add(neutral);

        var saboteur = game.SpawnObject("TestSaboteur", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("TestCommandCenter", neutral, new Vector3(5, 0, 0));

        Assert.False(SabotageModuleOf(saboteur).IsValidToExecute(target));
    }

    // "Must be a 'Unit' type thing" - a STRUCTURE with no AIUpdate needs BuildingPickup.
    [Fact]
    public void StructureTargetWithoutBuildingPickup_IsRejectedByTheBaseGate()
    {
        var game = NewGame();
        game.CivilianPlayer.Enemies.Add(Enemy(game));

        var saboteur = game.SpawnObject("TestSaboteurNoBuildingPickup", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("TestCommandCenter", Enemy(game), new Vector3(5, 0, 0));

        Assert.False(SabotageModuleOf(saboteur).IsValidToExecute(target));
    }

    // RequiredKindOf is a MASK (GPL isKindOfMulti): EVERY bit must be present. The old
    // single-value parse would have kept only "STRUCTURE" from this two-token line.
    [Fact]
    public void RequiredKindOfMask_AcceptsTargetCarryingEveryBit()
    {
        var game = NewGame();
        game.CivilianPlayer.Enemies.Add(Enemy(game));

        var saboteur = game.SpawnObject("TestSaboteurRequiresCommandCenterAndStructure", game.CivilianPlayer, Vector3.Zero);
        // TestCommandCenter is KindOf = COMMANDCENTER STRUCTURE - both required bits.
        var target = game.SpawnObject("TestCommandCenter", Enemy(game), new Vector3(5, 0, 0));

        Assert.True(SabotageModuleOf(saboteur).IsValidToExecute(target));
    }

    [Fact]
    public void RequiredKindOfMask_RejectsTargetMissingOneBit()
    {
        var game = NewGame();
        game.CivilianPlayer.Enemies.Add(Enemy(game));

        // Requires COMMANDCENTER *and* VEHICLE; the command center has only the former, so a
        // true mask rejects it. A single-value parse (last token wins = VEHICLE, unenforced)
        // would have accepted it.
        var saboteur = game.SpawnObject("TestSaboteurRequiresUnsatisfiableMask", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("TestCommandCenter", Enemy(game), new Vector3(5, 0, 0));

        Assert.False(SabotageModuleOf(saboteur).IsValidToExecute(target));
    }

    [Fact]
    public void ForbiddenKindOf_RejectsMatchingTarget()
    {
        var game = NewGame();
        game.CivilianPlayer.Enemies.Add(Enemy(game));

        var saboteur = game.SpawnObject("TestSaboteurForbidsStructure", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("TestCommandCenter", Enemy(game), new Vector3(5, 0, 0));

        Assert.False(SabotageModuleOf(saboteur).IsValidToExecute(target));
    }

    private static void AdvancePastRecharge(HeadlessSimGame game, List<SpecialPowerModule> specialPowers)
    {
        for (var i = 0; i < 60 && !specialPowers.All(sp => sp.Ready); i++)
        {
            game.Step();
        }
    }
}
