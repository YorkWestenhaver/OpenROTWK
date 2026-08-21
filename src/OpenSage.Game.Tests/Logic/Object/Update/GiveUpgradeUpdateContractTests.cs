// Mocked-game unit tests for the GiveUpgradeUpdate port (spec: bfme2-workbench/research/
// modules-r13/specs/GiveUpgradeUpdateModuleData.md §5), modeled line-for-line on
// ToggleHiddenSpecialAbilityUpdateContractTests.cs.
//
// Frame-arithmetic note: duration fields are milliseconds quantized by ceil(ms * 5 / 1000) at
// the frozen 5 Hz logic rate - "1000" below is exactly 5 logic frames, "200" is exactly 1
// logic frame. All INI values in this file are round multiples of 200ms, so no test depends
// on the ceil-vs-floor edge.
//
// Sleepy-update caveat: InitiateIntentToDoSpecialPower is a directly-invoked driven seam -
// tests call it on the module instance directly, never expect a Step() to produce it.
// Conversely, phase advancement only happens inside Update(), so every phase assertion
// follows the right number of Step() calls; the module's first Update() lands on the first
// Step() after spawn given the constructor's UpdateSleepTime.None.
//
// Frame-numbering convention: GameLogic.Update() increments its frame counter at the END of the
// tick, so the Nth game.Step() runs update modules at frame N-1. A driven seam invoked before
// the step loop therefore opens its window at frame 0, and a T-frame window (phase-end frame T)
// lapses on step T+1, not step T. Same convention the sibling this file is modeled on encodes -
// ToggleHiddenSpecialAbilityUpdateContractTests steps 6 times to lapse a PreparationTime of 5.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class GiveUpgradeUpdateContractTests
{
    private const string Definitions = @"
Object Porter
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = GiveUpgradeUpdate ModuleTag_Give
    SpecialPowerTemplate = TestGivePower
    UnpackTime      = 1000
    PreparationTime = 1000
    PackTime        = 1000
  End
End

Object PersistentPorter
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = GiveUpgradeUpdate ModuleTag_Give
    SpecialPowerTemplate = TestGivePower
    UnpackTime         = 0
    PreparationTime    = 1000
    PersistentPrepTime = 1000
    PackTime           = 0
  End
End

Object InstantPorter
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = GiveUpgradeUpdate ModuleTag_Give
    SpecialPowerTemplate = TestGivePower
    UnpackTime      = 0
    PreparationTime = 0
    PackTime        = 0
  End
End

Object RangedPorter
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = GiveUpgradeUpdate ModuleTag_Give
    SpecialPowerTemplate = TestGivePower
    StartAbilityRange = 100
    UnpackTime        = 0
    PreparationTime   = 1000
    PackTime          = 0
  End
End

Object DeliveringPorter
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = GiveUpgradeUpdate ModuleTag_Give
    SpecialPowerTemplate = TestGivePower
    UnpackTime      = 1000
    PreparationTime = 1000
    PackTime        = 1000
    ApproachRequiresLOS = No
    DeliverUpgrade  = Yes
    FadeOutSpeed    = 0.1
    SpawnOutFX      = FX_TestDeliver
  End
End

Object TestTarget
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x715A9E)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static GiveUpgradeUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<GiveUpgradeUpdate>().Single();

    private static void Step(HeadlessSimGame game, int count)
    {
        for (var i = 0; i < count; i++)
        {
            game.Step();
        }
    }

    /// <summary>
    /// Runs the case-1 schedule (initiate, then the frame-exact Unpacking/Packing boundaries
    /// for a 5/5/5-frame Porter-shaped object) and asserts flags at each boundary. Reused by
    /// case 11 to prove a held field is behaviorally inert.
    /// </summary>
    private static void AssertFullCycleSchedule(HeadlessSimGame game, GameObject obj)
    {
        var module = ModuleOf(obj);

        // Initiate runs before the step loop, i.e. at frame 0, so the UnpackTime=5 window ends
        // at frame 5 - the frame the 6th Step executes.
        Assert.True(module.InitiateIntentToDoSpecialPower("TestGivePower", null));

        // Step 1 (frame 0): Unpacking, the flag having been set by the initiate itself.
        game.Step();
        Assert.True(obj.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));
        Assert.False(obj.ModelConditionFlags.Get(ModelConditionFlag.Packing));

        // Steps 2-5 (frames 1-4): still Unpacking, one step before the boundary.
        Step(game, 4);
        Assert.True(obj.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));

        // Step 6 (frame 5): UnpackTime elapses -> Prepared (no model-condition flag of its
        // own); the PreparationTime=5 window opened here ends at frame 10.
        game.Step();
        Assert.False(obj.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));
        Assert.False(obj.ModelConditionFlags.Get(ModelConditionFlag.Packing));

        // Steps 7-10 (frames 6-9): still Prepared, one step before the boundary.
        Step(game, 4);
        Assert.False(obj.ModelConditionFlags.Get(ModelConditionFlag.Packing));

        // Step 11 (frame 10): PreparationTime elapses -> Packing, ending at frame 15.
        game.Step();
        Assert.True(obj.ModelConditionFlags.Get(ModelConditionFlag.Packing));

        // Steps 12-15 (frames 11-14): still Packing, one step before the boundary.
        Step(game, 4);
        Assert.True(obj.ModelConditionFlags.Get(ModelConditionFlag.Packing));

        // Step 16 (frame 15): PackTime elapses -> back to Packed.
        game.Step();
        Assert.False(obj.ModelConditionFlags.Get(ModelConditionFlag.Packing));
    }

    [Fact]
    public void FullCycle_UnpacksPreparesThenPacksAtExactFrames()
    {
        var game = NewGame();
        var porter = game.SpawnObject("Porter", game.CivilianPlayer, Vector3.Zero);
        AssertFullCycleSchedule(game, porter);
    }

    [Fact]
    public void Initiate_WrongTemplateName_IsRejected()
    {
        var game = NewGame();
        var porter = game.SpawnObject("Porter", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(porter);

        Assert.False(module.InitiateIntentToDoSpecialPower("WrongPower", null));

        Step(game, 20);

        Assert.False(porter.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));
        Assert.False(porter.ModelConditionFlags.Get(ModelConditionFlag.Packing));
    }

    [Fact]
    public void Initiate_WhileAlreadyCycling_IsRejected()
    {
        var game = NewGame();
        var porter = game.SpawnObject("Porter", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(porter);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestGivePower", null));
        game.Step();
        Assert.True(porter.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));

        Assert.False(module.InitiateIntentToDoSpecialPower("TestGivePower", null));

        // The in-flight cycle is unperturbed: still Unpacking here, and it completes on the
        // ordinary case-1 schedule from this point - 4 more steps (frames 1-4) still Unpacking,
        // then step 6 runs frame 5 and the UnpackTime=5 window opened at frame 0 lapses.
        Assert.True(porter.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));
        Step(game, 4);
        Assert.True(porter.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));
        game.Step();
        Assert.False(porter.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));
    }

    [Fact]
    public void StartAbilityRange_TriggeringObjectTooFar_FailsToInitiate()
    {
        var game = NewGame();
        var porter = game.SpawnObject("RangedPorter", game.CivilianPlayer, Vector3.Zero);
        var farAway = game.SpawnObject("TestTarget", game.CivilianPlayer, new Vector3(150, 0, 0));
        var module = ModuleOf(porter);

        Assert.False(module.InitiateIntentToDoSpecialPower("TestGivePower", farAway));

        Step(game, 20);

        Assert.False(porter.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));
    }

    [Fact]
    public void StartAbilityRange_TriggeringObjectInRange_Initiates()
    {
        var game = NewGame();
        var porter = game.SpawnObject("RangedPorter", game.CivilianPlayer, Vector3.Zero);
        var nearby = game.SpawnObject("TestTarget", game.CivilianPlayer, new Vector3(50, 0, 0));
        var module = ModuleOf(porter);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestGivePower", nearby));
    }

    [Fact]
    public void StartAbilityRange_NullTriggeringObject_SkipsRangeGate()
    {
        var game = NewGame();
        var porter = game.SpawnObject("RangedPorter", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(porter);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestGivePower", null));
    }

    [Fact]
    public void ZeroDurations_CollapseTheWholeCycleWithoutOccupyingFrames()
    {
        var game = NewGame();
        var porter = game.SpawnObject("InstantPorter", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(porter);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestGivePower", null));
        game.Step();

        Assert.False(porter.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));
        Assert.False(porter.ModelConditionFlags.Get(ModelConditionFlag.Packing));

        // Back at Packed, all three stages skipped in the same call: a fresh initiate succeeds.
        Assert.True(module.InitiateIntentToDoSpecialPower("TestGivePower", null));
    }

    [Fact]
    public void ZeroUnpackTime_LandsInPreparedImmediately()
    {
        var game = NewGame();
        var porter = game.SpawnObject("PersistentPorter", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(porter);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestGivePower", null));
        game.Step();

        Assert.False(porter.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));

        // Still cycling (Prepared, not back at Packed): a second initiate is rejected.
        Assert.False(module.InitiateIntentToDoSpecialPower("TestGivePower", null));
    }

    [Fact]
    public void PersistentPrepTime_ExtendsPreparedWindowExactlyOnce()
    {
        var game = NewGame();
        var porter = game.SpawnObject("PersistentPorter", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(porter);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestGivePower", null));

        // 6 steps: past the first 5-frame PreparationTime window - still Prepared thanks to
        // the one-shot PersistentPrepTime extension.
        Step(game, 6);
        Assert.False(module.InitiateIntentToDoSpecialPower("TestGivePower", null));

        // 5 more steps (11 total > 5+5): the extension fully lapses too, and the cycle
        // completes (auto-packs, zero PackTime -> back to Packed).
        Step(game, 5);
        Assert.True(module.InitiateIntentToDoSpecialPower("TestGivePower", null));
    }

    [Fact]
    public void PersistentPrepTime_Unset_PacksAtFirstWindowEnd()
    {
        var game = NewGame();
        var porter = game.SpawnObject("Porter", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(porter);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestGivePower", null));

        // No PersistentPrepTime: packing begins at frame 10 (5 unpack + 5 prep), not 15.
        Step(game, 11);
        Assert.True(porter.ModelConditionFlags.Get(ModelConditionFlag.Packing));
    }

    [Fact]
    public void HeldFields_ParsedAndExposedReadOnly()
    {
        var game = NewGame();
        var porter = game.SpawnObject("Porter", game.CivilianPlayer, Vector3.Zero);
        var delivering = game.SpawnObject("DeliveringPorter", game.CivilianPlayer, new Vector3(200, 0, 0));

        Assert.False(ModuleOf(porter).DeliversUpgrade);
        Assert.True(ModuleOf(delivering).DeliversUpgrade);

        // Behaviorally inert: the DeliverUpgrade=Yes object follows the identical frame
        // schedule as the plain Porter.
        AssertFullCycleSchedule(game, delivering);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var porter = game.SpawnObject("Porter", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(porter);
        Assert.True(live.InitiateIntentToDoSpecialPower("TestGivePower", null));
        game.Step();

        var shadowHost = game.SpawnObject("Porter", game.CivilianPlayer, new Vector3(200, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void Xfer_RoundTrip_PreservesPhaseAndTriggeringObject()
    {
        var game = NewGame();
        var porter = game.SpawnObject("RangedPorter", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("TestTarget", game.CivilianPlayer, new Vector3(50, 0, 0));
        var live = ModuleOf(porter);

        Assert.True(live.InitiateIntentToDoSpecialPower("TestGivePower", target));
        game.Step();

        var liveCrc = PortedModuleTestKit.LiveCrc(live);
        var saved = PortedModuleTestKit.Save(live);

        var freshHost = game.SpawnObject("RangedPorter", game.CivilianPlayer, new Vector3(300, 0, 0));
        var fresh = ModuleOf(freshHost);
        PortedModuleTestKit.Load(fresh, saved);

        Assert.Equal(liveCrc, PortedModuleTestKit.LiveCrc(fresh));

        // Mid-cycle (Prepared, not Packed): a fresh initiate on the loaded instance is
        // rejected, proving _phase (and by extension the whole walk) round-tripped correctly.
        Assert.False(fresh.InitiateIntentToDoSpecialPower("TestGivePower", null));
    }
}
