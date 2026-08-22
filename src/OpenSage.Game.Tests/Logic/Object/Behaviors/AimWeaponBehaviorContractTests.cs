// Mocked-game unit tests for the AimWeaponBehavior port (R15 packet L5-P7, spec:
// bfme2-workbench/research/modules-r13/specs/AimWeaponBehaviorModuleData.md): the AIM_NEAR
// half only - one test per behavioral branch, the F-AWB-1/-2/-3/-4 tripwire pins, the
// shadow-copy base test, and the mid-behavior save/load round-trip.
//
// Sleepy-update caveat: this module IS an UpdateModule, so a freshly spawned module's first
// real Update() runs on the object's SECOND HeadlessSimGame.Step() (GameLogic.CreateObject
// bumps a frame-zero spawn's NextCallFrame to >= 1, and Step() increments _currentFrame only
// at the end). Every case below calls the shared StepTwice helper before asserting post-spawn
// state. There is no ctor RNG stagger here (no ScanDelayTime-equivalent field), so two steps
// are exact and sufficient.
//
// Observable: obj.ModelConditionFlags.Get(ModelConditionFlag.AimNear) - HeadlessSimGame builds
// a real GameClient, so Drawable (and therefore ModelConditionFlags) is live, exactly as
// EnemyNearUpdateContractTests relies on.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Behaviors;

public class AimWeaponBehaviorContractTests
{
    // MeleeGiant mirrors mountaingiant's shape (AimNearDistance alone, 40.0 - the value 4 of
    // the 5 live authored instances use). Archer mirrors the majority 56-of-61 shape: the
    // held High/Low pair at the uniform +/-0.15, no AimNearDistance at all.
    private const string Definitions = @"
Object MeleeGiant
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
  Behavior = AimWeaponBehavior AimWeaponModuleTag
    AimNearDistance = 40.0
  End
End

Object Archer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
  Behavior = AimWeaponBehavior AimWeaponModuleTag
    AimLowThreshold = -0.15
    AimHighThreshold = 0.15
  End
End

Object GiantUpgradeFields
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
  Behavior = AimWeaponBehavior AimWeaponModuleTag
    AimNearDistance = 40.0
    TriggeredBy = Upgrade_TestThing
    StartsActive = No
  End
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

    private static HeadlessSimGame NewGame(uint seed = 0xA1EDu) // "AimEd"
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static AimWeaponBehavior ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<AimWeaponBehavior>().Single();

    private static bool AimNearFlag(GameObject obj) =>
        obj.ModelConditionFlags.Get(ModelConditionFlag.AimNear);

    /// <summary>
    /// This module is an UpdateModule: a freshly spawned module's first real Update() runs on
    /// the object's SECOND Step(). No ctor RNG stagger here, so two steps are exact.
    /// </summary>
    private static void StepTwice(HeadlessSimGame game)
    {
        game.Step();
        game.Step();
    }

    [Fact]
    public void NoVictim_AimNearNotSet()
    {
        // Proves the module keys on the victim, not on mere proximity: an EnemyNearUpdate-shaped
        // copy-paste (which fires on anything in radius) would set the flag here.
        var game = NewGame();
        var giant = game.SpawnObject("MeleeGiant", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Target", game.CivilianPlayer, new Vector3(10, 0, 0));

        StepTwice(game);

        Assert.False(AimNearFlag(giant));
    }

    [Fact]
    public void VictimInsideDistance_SetsAimNear()
    {
        var game = NewGame();
        var giant = game.SpawnObject("MeleeGiant", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(10, 0, 0));
        giant.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);

        Assert.True(AimNearFlag(giant));
    }

    [Fact]
    public void VictimOutsideDistance_DoesNotSet()
    {
        var game = NewGame();
        var giant = game.SpawnObject("MeleeGiant", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(500, 0, 0));
        giant.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);

        Assert.False(AimNearFlag(giant));
    }

    [Fact]
    public void VictimExactlyAtDistance_DoesNotSet()
    {
        // Pins the partition seam's strict '<' predicate: a future inclusive-boundary change
        // must be caught here, not as a desync.
        var game = NewGame();
        var giant = game.SpawnObject("MeleeGiant", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(40, 0, 0));
        giant.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);

        Assert.False(AimNearFlag(giant));
    }

    [Fact]
    public void VictimEntersThenLeaves_RisingAndFallingEdges()
    {
        var game = NewGame();
        var giant = game.SpawnObject("MeleeGiant", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(500, 0, 0));
        giant.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);
        Assert.False(AimNearFlag(giant));

        target.UpdateTransform(new Vector3(10, 0, 0));
        target.UpdateColliders();
        game.Step();
        Assert.True(AimNearFlag(giant));

        target.UpdateTransform(new Vector3(500, 0, 0));
        target.UpdateColliders();
        game.Step();
        Assert.False(AimNearFlag(giant));
    }

    [Fact]
    public void VictimClearedToInvalid_ClearsAimNear()
    {
        var game = NewGame();
        var giant = game.SpawnObject("MeleeGiant", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(10, 0, 0));
        giant.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);
        Assert.True(AimNearFlag(giant));

        giant.AIUpdate.SetCurrentVictim(ObjectId.Invalid);
        game.Step();

        Assert.False(AimNearFlag(giant));
    }

    [Fact]
    public void VictimDies_ClearsAimNear()
    {
        // Falling edge with no GetObjectById call: a destroyed victim simply is not in the
        // partition query's result list.
        var game = NewGame();
        var giant = game.SpawnObject("MeleeGiant", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(10, 0, 0));
        giant.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);
        Assert.True(AimNearFlag(giant));

        target.Kill();
        game.Step(); // reap the destroyed object
        game.Step(); // guarantee the following Update() sees the vacated partition slot

        Assert.False(AimNearFlag(giant));
    }

    [Fact]
    public void BystanderInRange_ButVictimFar_DoesNotSet()
    {
        // Discriminates "membership of the victim" from "anything in radius".
        var game = NewGame();
        var giant = game.SpawnObject("MeleeGiant", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Bystander", game.CivilianPlayer, new Vector3(10, 0, 0));
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(500, 0, 0));
        giant.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);

        Assert.False(AimNearFlag(giant));
    }

    [Fact]
    public void ZeroAimNearDistance_NeverSetsAimNear()
    {
        // F-AWB-4 pin: the Archer shape (no AimNearDistance authored -> Fix64.Zero) is the
        // MAJORITY of the shipped corpus (56 of 61 live instances), so this case stands in for
        // most of the data, not a corner case.
        var game = NewGame();
        var archer = game.SpawnObject("Archer", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(1, 0, 0));
        archer.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);

        Assert.False(AimNearFlag(archer));
    }

    [Fact]
    public void SteadyState_HoldsAimNearAcrossMultipleFrames()
    {
        // Correctness regression only: unlike DualWeaponBehavior (whose weapon-set re-resolve
        // allocates) there is no allocation-based observable for the transition-only guard,
        // because ModelConditionFlags.Set is a plain BitArray write. Documented here so a
        // future editor does not go looking for an observable that does not exist.
        var game = NewGame();
        var giant = game.SpawnObject("MeleeGiant", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(10, 0, 0));
        giant.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);
        Assert.True(AimNearFlag(giant));

        for (var i = 0; i < 5; i++)
        {
            game.Step();
            Assert.True(AimNearFlag(giant));
        }
    }

    [Fact]
    public void HeldFields_AimHighLowThreshold_NeverSetOrClear()
    {
        // F-AWB-1/F-AWB-2 tripwire: the Archer authors the held +/-0.15 pair and its victim sits
        // directly overhead at a height far steeper than any plausible aim cone. Under ANY
        // guessed pitch reading of AimHighThreshold, AIM_HIGH would be raised here; this port is
        // silent on both flags, so they must stay false. This test is meant to fail the day
        // someone implements a guessed reading, forcing F-AWB-1/F-AWB-6's open unit question
        // back through review.
        var game = NewGame();
        var archer = game.SpawnObject("Archer", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(0, 0, 500));
        archer.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);

        for (var i = 0; i < 5; i++)
        {
            game.Step();
            Assert.False(archer.ModelConditionFlags.Get(ModelConditionFlag.AimHigh));
            Assert.False(archer.ModelConditionFlags.Get(ModelConditionFlag.AimLow));
        }
    }

    [Fact]
    public void Xfer_ShadowCopyCrcEqualsLiveCrc_AimNearActive()
    {
        var game = NewGame();
        var giant = game.SpawnObject("MeleeGiant", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(10, 0, 0));
        giant.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);
        Assert.True(AimNearFlag(giant));
        var live = ModuleOf(giant);

        // Shadow host deliberately in the OPPOSITE state (never given a victim).
        var shadowHost = game.SpawnObject("MeleeGiant", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void Xfer_ShadowCopyCrcEqualsLiveCrc_Idle()
    {
        var game = NewGame();
        var giant = game.SpawnObject("MeleeGiant", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Target", game.CivilianPlayer, new Vector3(500, 0, 0));
        // No victim ever set: the false branch of the one-field walk.

        StepTwice(game);
        Assert.False(AimNearFlag(giant));
        var live = ModuleOf(giant);

        var shadowHost = game.SpawnObject("MeleeGiant", game.CivilianPlayer, new Vector3(100, 0, 0));
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
        var giant = game.SpawnObject("MeleeGiant", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(10, 0, 0));
        giant.AIUpdate.SetCurrentVictim(target.Id);
        var module = ModuleOf(giant);

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
            trajectory[i] = AimNearFlag(giant) ? 1 : 0;
        }

        return trajectory;
    }

    [Fact]
    public void UpgradeFields_ParseButAreInert()
    {
        // F-AWB-3 pin: the definition loads (no IniParseException) despite carrying
        // TriggeredBy/StartsActive, and the module still fires unconditionally - the runtime
        // implements no IUpgradeableModule gate, per the census (0 of 61 shipped instances
        // author either field).
        var game = NewGame();
        var giant = game.SpawnObject("GiantUpgradeFields", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Target", game.CivilianPlayer, new Vector3(10, 0, 0));
        giant.AIUpdate.SetCurrentVictim(target.Id);

        StepTwice(game);

        Assert.True(AimNearFlag(giant));
    }
}
