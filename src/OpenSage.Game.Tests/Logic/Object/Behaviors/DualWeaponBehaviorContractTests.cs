// Mocked-game unit tests for the DualWeaponBehavior port (R13, spec packet:
// bfme2-workbench/research/modules-r13/specs/DualWeaponBehaviorModuleData.md): the
// always-on close-range condition toggle, one test per behavioral branch, the F-DWB-1/-3/-4/-5
// tripwire pins, the shadow-copy base test, and the mid-behavior save/load round-trip.
//
// Sleepy-update caveat: this module IS an UpdateModule, so a freshly spawned module's first
// real Update() runs on the object's SECOND HeadlessSimGame.Step() (GameLogic.CreateObject
// bumps a frame-zero spawn's NextCallFrame to >= 1, and Step() increments _currentFrame only
// at the end). Every case below calls the shared StepTwice helper before asserting post-spawn
// state. There is no ctor RNG stagger here (no ScanDelayTime-equivalent field), so two steps
// are exact and sufficient - no StepPastFirstScan-style margin loop is needed.
//
// Observables: obj.WeaponSetConditions.Get(WeaponSetConditions.CloseRange) (internal field,
// GameObject.cs, visible to the test assembly) and, for the end-to-end cases,
// obj.CurrentWeapon.Template.Name to prove the exact-match WeaponSet re-resolve actually fired.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Behaviors;

public class DualWeaponBehaviorContractTests
{
    // Swordsman mirrors eredluintrollslayerhorde's full condition table (None + CLOSE_RANGE).
    // SwordsmanNoCloseSet mirrors ithilienpathfinder (F-DWB-5: no CLOSE_RANGE weapon set at
    // all). SwordsmanZeroDistance mirrors gondorarcher (F-DWB-4: distance field absent -> 0).
    // SwordsmanRealRange + BigTarget pin F-DWB-1 (UseRealVictimRange held/inert).
    private const string Definitions = @"
Weapon TestBow
  ClipSize = 1
  DelayBetweenShots = 1000
  ClipReloadTime = 1000
End

Weapon TestSword
  ClipSize = 1
  DelayBetweenShots = 1000
  ClipReloadTime = 1000
End

Object Swordsman
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  WeaponSet
    Conditions = None
    Weapon = PRIMARY TestBow
  End
  WeaponSet
    Conditions = CLOSE_RANGE
    Weapon = PRIMARY TestSword
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
  Behavior = DualWeaponBehavior ModuleTag_Dual
    SwitchWeaponOnCloseRangeDistance = 50
  End
End

Object SwordsmanNoCloseSet
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  WeaponSet
    Conditions = None
    Weapon = PRIMARY TestBow
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
  Behavior = DualWeaponBehavior ModuleTag_Dual
    SwitchWeaponOnCloseRangeDistance = 50
  End
End

Object SwordsmanZeroDistance
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  WeaponSet
    Conditions = None
    Weapon = PRIMARY TestBow
  End
  WeaponSet
    Conditions = CLOSE_RANGE
    Weapon = PRIMARY TestSword
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
  Behavior = DualWeaponBehavior ModuleTag_Dual
  End
End

Object SwordsmanRealRange
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  WeaponSet
    Conditions = None
    Weapon = PRIMARY TestBow
  End
  WeaponSet
    Conditions = CLOSE_RANGE
    Weapon = PRIMARY TestSword
  End
  Geometry = CYLINDER
  GeometryMajorRadius = 30
  GeometryHeight = 12
  Behavior = AIUpdateInterface ModuleTag_AI
  End
  Behavior = DualWeaponBehavior ModuleTag_Dual
    SwitchWeaponOnCloseRangeDistance = 50
    UseRealVictimRange = Yes
  End
End

Object SwordsmanUpgradeFields
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  WeaponSet
    Conditions = None
    Weapon = PRIMARY TestBow
  End
  WeaponSet
    Conditions = CLOSE_RANGE
    Weapon = PRIMARY TestSword
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
  Behavior = DualWeaponBehavior ModuleTag_Dual
    SwitchWeaponOnCloseRangeDistance = 50
    TriggeredBy = Upgrade_TestThing
    StartsActive = No
  End
End

Object BigTarget
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Geometry = CYLINDER
  GeometryMajorRadius = 30
  GeometryHeight = 12
End

Object Target
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object Bystander
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xD0A1u) // "DualWeapon"
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static DualWeaponBehavior ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<DualWeaponBehavior>().Single();

    private static bool CloseRangeFlag(GameObject obj) =>
        obj.WeaponSetConditions.Get(WeaponSetConditions.CloseRange);

    /// <summary>
    /// This module is an UpdateModule: a freshly spawned module's first real Update() runs on
    /// the object's SECOND Step() (GameLogic.CreateObject bumps a frame-zero spawn's
    /// NextCallFrame to >= 1). No ctor RNG stagger here, so two steps are exact.
    /// </summary>
    private static void StepTwice(HeadlessSimGame game)
    {
        game.Step();
        game.Step();
    }

    [Fact]
    public void NoVictim_CloseRangeNotSet()
    {
        var game = NewGame();
        var swordsman = game.SpawnObject("Swordsman", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Target", game.CivilianPlayer, new Vector3(10, 0, 0));
        // Deliberately no SetCurrentVictim call: proves the module keys on the victim, not on
        // mere proximity (the wrong-implementation trap: an EnemyNearUpdate copy-paste would
        // fire here).

        StepTwice(game);

        Assert.False(CloseRangeFlag(swordsman));
        Assert.Equal("TestBow", swordsman.CurrentWeapon.Template.Name);
    }

    [Fact]
    public void VictimInsideDistance_SetsCloseRange_AndSwitchesWeapon()
    {
        var game = NewGame();
        var swordsman = game.SpawnObject("Swordsman", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(10, 0, 0));
        swordsman.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);

        Assert.True(CloseRangeFlag(swordsman));
        Assert.Equal("TestSword", swordsman.CurrentWeapon.Template.Name);
    }

    [Fact]
    public void VictimOutsideDistance_DoesNotSet()
    {
        var game = NewGame();
        var swordsman = game.SpawnObject("Swordsman", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(500, 0, 0));
        swordsman.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);

        Assert.False(CloseRangeFlag(swordsman));
        Assert.Equal("TestBow", swordsman.CurrentWeapon.Template.Name);
    }

    [Fact]
    public void VictimExactlyAtDistance_DoesNotSet()
    {
        // Pins the partition seam's strict '<' predicate: a future inclusive-boundary change
        // must be caught here, not as a desync.
        var game = NewGame();
        var swordsman = game.SpawnObject("Swordsman", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(50, 0, 0));
        swordsman.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);

        Assert.False(CloseRangeFlag(swordsman));
    }

    [Fact]
    public void VictimEntersThenLeaves_RisingAndFallingEdges()
    {
        var game = NewGame();
        var swordsman = game.SpawnObject("Swordsman", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(500, 0, 0));
        swordsman.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);
        Assert.False(CloseRangeFlag(swordsman));

        target.UpdateTransform(new Vector3(10, 0, 0));
        target.UpdateColliders();
        game.Step();
        Assert.True(CloseRangeFlag(swordsman));
        Assert.Equal("TestSword", swordsman.CurrentWeapon.Template.Name);

        target.UpdateTransform(new Vector3(500, 0, 0));
        target.UpdateColliders();
        game.Step();
        Assert.False(CloseRangeFlag(swordsman));
        Assert.Equal("TestBow", swordsman.CurrentWeapon.Template.Name);
    }

    [Fact]
    public void VictimClearedToInvalid_ClearsCloseRange()
    {
        var game = NewGame();
        var swordsman = game.SpawnObject("Swordsman", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(10, 0, 0));
        swordsman.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);
        Assert.True(CloseRangeFlag(swordsman));

        swordsman.AIUpdate.SetCurrentVictim(ObjectId.Invalid);
        game.Step();

        Assert.False(CloseRangeFlag(swordsman));
    }

    [Fact]
    public void VictimDies_ClearsCloseRange()
    {
        // Falling edge with no GetObjectById call: a destroyed victim simply is not in the
        // partition query's result list.
        var game = NewGame();
        var swordsman = game.SpawnObject("Swordsman", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(10, 0, 0));
        swordsman.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);
        Assert.True(CloseRangeFlag(swordsman));

        target.Kill();
        game.Step(); // reap the destroyed object
        game.Step(); // guarantee the following Update() sees the vacated partition slot

        Assert.False(CloseRangeFlag(swordsman));
    }

    [Fact]
    public void BystanderInRange_ButVictimFar_DoesNotSet()
    {
        // Discriminates "membership of the victim" from "anything in radius": a
        // foreach { found = true; break; } copy-paste from EnemyNearUpdate fails this case.
        var game = NewGame();
        var swordsman = game.SpawnObject("Swordsman", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Bystander", game.CivilianPlayer, new Vector3(10, 0, 0));
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(500, 0, 0));
        swordsman.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);

        Assert.False(CloseRangeFlag(swordsman));
    }

    [Fact]
    public void NoCloseRangeWeaponSet_BitSetButWeaponUnchanged()
    {
        // F-DWB-5 pin (ithilienpathfinder shape): the bit is set (the module did its job) but
        // the exact-match WeaponSet lookup finds no CLOSE_RANGE key and WeaponSet.Update
        // returns early - no throw, no null weapon, previous set retained.
        var game = NewGame();
        var swordsman = game.SpawnObject("SwordsmanNoCloseSet", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(10, 0, 0));
        swordsman.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);

        Assert.True(CloseRangeFlag(swordsman));
        Assert.Equal("TestBow", swordsman.CurrentWeapon.Template.Name);
    }

    [Fact]
    public void SteadyState_DoesNotRewriteTheConditionEveryFrame()
    {
        // WeaponSet allocates a fresh Weapon instance whenever the resolved template set
        // changes; instance identity is a clean observable for "no redundant transition write".
        var game = NewGame();
        var swordsman = game.SpawnObject("Swordsman", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(10, 0, 0));
        swordsman.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);
        Assert.True(CloseRangeFlag(swordsman));
        var weaponBefore = swordsman.CurrentWeapon;

        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }

        Assert.Same(weaponBefore, swordsman.CurrentWeapon);
    }

    [Fact]
    public void ZeroDistance_NeverSetsCloseRange()
    {
        // F-DWB-4 pin (gondorarcher shape): distance field absent -> Fix64.Zero -> the
        // degenerate guard, a documented no-op rather than an accidental always-on.
        var game = NewGame();
        var swordsman = game.SpawnObject("SwordsmanZeroDistance", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(1, 0, 0));
        swordsman.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);

        Assert.False(CloseRangeFlag(swordsman));
    }

    [Fact]
    public void UseRealVictimRange_IsParsedAndInert()
    {
        // F-DWB-1 tripwire: under a real bounding-circle-adjusted range, BigTarget's
        // surface-to-centre distance (70 - 30 = 40) would be < 50 and the bit WOULD be set;
        // under the ported centre-to-centre reading it is 70 >= 50 and it is not. This test
        // deliberately fails the day someone implements the held field, which is exactly what
        // we want - it forces the held-field decision back through review.
        var game = NewGame();
        var swordsman = game.SpawnObject("SwordsmanRealRange", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("BigTarget", game.CivilianPlayer, new Vector3(70, 0, 0));
        swordsman.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);

        Assert.False(CloseRangeFlag(swordsman));
    }

    [Fact]
    public void Xfer_ShadowCopyCrcEqualsLiveCrc_CloseRangeActive()
    {
        var game = NewGame();
        var swordsman = game.SpawnObject("Swordsman", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(10, 0, 0));
        swordsman.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);
        Assert.True(CloseRangeFlag(swordsman));
        var live = ModuleOf(swordsman);

        // Shadow host deliberately in the OPPOSITE state (never set a victim).
        var shadowHost = game.SpawnObject("Swordsman", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void Xfer_ShadowCopyCrcEqualsLiveCrc_Idle()
    {
        var game = NewGame();
        var swordsman = game.SpawnObject("Swordsman", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Target", game.CivilianPlayer, new Vector3(500, 0, 0));
        // No victim ever set: the false branch of the one-field walk.

        StepTwice(game);
        Assert.False(CloseRangeFlag(swordsman));
        var live = ModuleOf(swordsman);

        var shadowHost = game.SpawnObject("Swordsman", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 4);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static int[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xF00D);
        var swordsman = game.SpawnObject("Swordsman", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(10, 0, 0));
        swordsman.AIUpdate.SetCurrentVictim(target.Id);
        var module = ModuleOf(swordsman);

        var trajectory = new int[14];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk; // engine-owned, walk-carried (S6)
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            // Victim leaves range at frame 6, returns at frame 9: exercises both edges through
            // the round-trip.
            var outOfRange = i >= 6 && i < 9;
            target.UpdateTransform(outOfRange ? new Vector3(500, 0, 0) : new Vector3(10, 0, 0));
            target.UpdateColliders();

            game.Step();
            trajectory[i] = CloseRangeFlag(swordsman) ? 1 : 0;
        }

        return trajectory;
    }

    [Fact]
    public void UpgradeFields_ParseButAreInert()
    {
        // F-DWB-3 pin: the definition loads (no IniParseException) despite carrying
        // TriggeredBy/StartsActive, and the module still fires unconditionally - the runtime
        // implements no IUpgradeableModule gate, per the §0.4 census (0 of 150 shipped
        // instances author either field).
        var game = NewGame();
        var swordsman = game.SpawnObject("SwordsmanUpgradeFields", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(10, 0, 0));
        swordsman.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);

        Assert.True(CloseRangeFlag(swordsman));
        Assert.Equal("TestSword", swordsman.CurrentWeapon.Template.Name);
    }
}
