// Mocked-game contract tests for the SlaughterHordeContain port (R12): the upgrade-gated
// entry gate (PassengerFilter + capacity + the Allow*Inside faction barrier), status-flag
// application on entry/release, the ExitOffset move order issued before removal, and the
// death-triggered CashBackPercent refund - covering every testCase in the R12 task packet.
//
// HeadlessSimGame's default two players (Players[0], nicknamed Enemy below, and
// CivilianPlayer) carry no map-authored alliance data, so - the same documented workaround
// SabotageCommandCenterCrateCollideContractTests uses - tests that need a live ENEMIES
// relationship set it explicitly via Player.Enemies.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using OpenSage.Tests.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Contain;

public class SlaughterHordeContainContractTests
{
    private const string Definitions = @"
Upgrade Upgrade_UnlockPen
  Type = PLAYER
End

Locomotor TestPenLoco
  Surfaces = GROUND
  Speed = 30
  TurnRate = 360
  Acceleration = 100
  Braking = 100
  Appearance = TWO_LEGS
  ZAxisBehavior = NO_Z_MOTIVE_FORCE
End

Object TestVictim
  KindOf = INFANTRY
  BuildCost = 100
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Locomotor = SET_NORMAL TestPenLoco
End

Object TestPen
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Behavior = SlaughterHordeContain ModuleTag_Pen
    StartsActive = Yes
    ContainMax = 1
    CashBackPercent = 75%
    AllowEnemiesInside = No
    AllowAlliesInside = Yes
    AllowNeutralInside = Yes
    PassengerFilter = ALL
    ObjectStatusOfContained = UNSELECTABLE
    EntryPosition = X:0 Y:0 Z:0
    ExitOffset = X:20 Y:0 Z:0
  End
  Locomotor = SET_NORMAL TestPenLoco
End

Object GatedPen
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = SlaughterHordeContain ModuleTag_Pen
    TriggeredBy = Upgrade_UnlockPen
    ContainMax = 4
    PassengerFilter = ALL
    ObjectStatusOfContained = UNSELECTABLE
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x51A)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static SlaughterHordeContain ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<SlaughterHordeContain>().Single();

    private static Player Enemy(HeadlessSimGame game) => game.PlayerManager.Players[0];

    [Fact]
    public void TryContain_AddsCompatibleMember_AndAppliesContainedStatus()
    {
        var game = NewGame();
        var pen = game.SpawnObject("TestPen", game.CivilianPlayer, Vector3.Zero);
        var victim = game.SpawnObject("TestVictim", game.CivilianPlayer, new Vector3(5, 0, 0));
        var module = ModuleOf(pen);

        Assert.True(module.TryContain(victim));

        Assert.Equal(1, module.ContainedCount);
        Assert.True(module.IsContained(victim));
        Assert.True(victim.TestStatus(ObjectStatus.Unselectable));
    }

    [Fact]
    public void TryContain_RefusesEnemyWhenAllowEnemiesInsideIsNo()
    {
        var game = NewGame();
        game.CivilianPlayer.Enemies.Add(Enemy(game));

        var pen = game.SpawnObject("TestPen", game.CivilianPlayer, Vector3.Zero);
        var victim = game.SpawnObject("TestVictim", Enemy(game), new Vector3(5, 0, 0));
        var module = ModuleOf(pen);

        Assert.False(module.TryContain(victim));
        Assert.Equal(0, module.ContainedCount);
        Assert.False(victim.TestStatus(ObjectStatus.Unselectable));
    }

    [Fact]
    public void TryContain_EnforcesContainMax_ReleaseFreesASlot()
    {
        var game = NewGame();
        var pen = game.SpawnObject("TestPen", game.CivilianPlayer, Vector3.Zero);
        var first = game.SpawnObject("TestVictim", game.CivilianPlayer, new Vector3(5, 0, 0));
        var second = game.SpawnObject("TestVictim", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = ModuleOf(pen);

        Assert.True(module.TryContain(first));
        Assert.False(module.TryContain(second));   // ContainMax = 1, already full
        Assert.Equal(1, module.ContainedCount);

        Assert.True(module.Release(first));
        Assert.True(module.TryContain(second));    // the freed slot admits the next candidate
        Assert.Equal(1, module.ContainedCount);
        Assert.True(module.IsContained(second));
    }

    [Fact]
    public void ContainedMember_Death_PaysCashBackPercentOfBuildCostToTheOwner()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        var pen = game.SpawnObject("TestPen", owner, Vector3.Zero);
        var victim = game.SpawnObject("TestVictim", owner, new Vector3(5, 0, 0));
        var module = ModuleOf(pen);

        // Warm-up frames: a freshly created object's update modules only join the sleepy
        // update list on the frame after CreateObject, so the pen's own Update() (the one
        // that reaps and refunds) does not run until the registration frame has passed.
        game.Step();
        game.Step();

        Assert.True(module.TryContain(victim));
        Assert.Equal(0u, owner.BankAccount.Money);

        PortedModuleTestKit.TriggerDeath(victim);
        // The pen's own Update() reaps the dead member and pays the refund. A wake frame set
        // from outside Update() (TryContain's SetWakeFrame) lands on the frame after the
        // current one, and GameLogic advances its frame counter after the module loop, so the
        // reaping Update() runs on the second Step, not the first.
        game.Step();
        game.Step();

        // BuildCost 100 * CashBackPercent 75% = 75.
        Assert.Equal(75u, owner.BankAccount.Money);
        Assert.Equal(0, module.ContainedCount);
    }

    [Fact]
    public void Release_RoutesTheMemberTowardExitOffset_BeforeRemoval()
    {
        var game = NewGame();
        var pen = game.SpawnObject("TestPen", game.CivilianPlayer, Vector3.Zero);
        var victim = game.SpawnObject("TestVictim", game.CivilianPlayer, new Vector3(0, 5, 0));
        var module = ModuleOf(pen);

        // Two frames tick both SimLocomotorUpdate modules' lazy transform ingestion
        // (TransformInitialized) so RouteMemberTo has a real Fix64 anchor to steer from -
        // a freshly created object's modules only join the sleepy update list on the frame
        // after CreateObject, so the first Step does not yet run their Update().
        game.Step();
        game.Step();

        Assert.True(module.TryContain(victim));

        var memberMover = victim.FindBehavior<SimLocomotorUpdate>();
        memberMover.Stop();
        Assert.Equal(SimMoveMode.Maintain, memberMover.Mode);

        Assert.True(module.Release(victim));

        // Release() issues the ExitOffset move order (SetTargetPosition, which always sets
        // MoveToPosition) BEFORE dropping the member from the contained list.
        Assert.Equal(SimMoveMode.MoveToPosition, memberMover.Mode);
        Assert.Equal(0, module.ContainedCount);
        Assert.False(victim.TestStatus(ObjectStatus.Unselectable));
    }

    [Fact]
    public void UpgradeGate_RefusesUntilTriggered_ThenAdmits()
    {
        var game = NewGame();
        var pen = game.SpawnObject("GatedPen", game.CivilianPlayer, Vector3.Zero);
        var victim = game.SpawnObject("TestVictim", game.CivilianPlayer, new Vector3(5, 0, 0));
        var module = ModuleOf(pen);

        Assert.False(module.IsActive);
        Assert.False(module.TryContain(victim));
        Assert.Equal(0, module.ContainedCount);

        module.TryUpgrade(new UpgradeSet { game.AssetStore.Upgrades.GetByName("Upgrade_UnlockPen") });

        Assert.True(module.IsActive);
        Assert.True(module.TryContain(victim));
        Assert.Equal(1, module.ContainedCount);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var pen = game.SpawnObject("TestPen", game.CivilianPlayer, Vector3.Zero);
        var victim = game.SpawnObject("TestVictim", game.CivilianPlayer, new Vector3(5, 0, 0));
        var live = ModuleOf(pen);
        live.TryContain(victim);

        var shadow = ModuleOf(game.SpawnObject("TestPen", game.CivilianPlayer, new Vector3(50, 0, 0)));

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
        Assert.Equal(1, shadow.ContainedCount);
    }
}
