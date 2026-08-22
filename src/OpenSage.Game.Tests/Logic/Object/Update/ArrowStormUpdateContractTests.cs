// Mocked-game unit tests for the ArrowStormUpdate port (api-freeze-v1 §6 fitness item 4),
// following the ToggleHiddenSpecialAbilityUpdateContractTests idiom: one test per behavior
// branch, [create -> trigger/tick -> observable effect]. Both InitiateIntentToDoSpecialPower
// and Abort() are driven inputs (no landed special-power/command system exists yet), so tests
// call them directly.
//
// Frame arithmetic: all duration fields are milliseconds (ParseDurationLogicFrames), quantized
// to the frozen 5 Hz logic rate - "1000" is exactly 5 logic frames, "200" is 1 logic frame,
// "600" is 3 logic frames, "1200" is 6 logic frames.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class ArrowStormUpdateContractTests
{
    private const string Definitions = @"
Object ArrowStormArcher
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ArrowStormUpdate ModuleTag_Storm
    SpecialPowerTemplate = TestArrowStorm
    UnpackTime      = 1000
    PreparationTime = 1000
    PackTime        = 1000
  End
End

Object PersistentArcher
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ArrowStormUpdate ModuleTag_Storm
    SpecialPowerTemplate = TestArrowStorm
    UnpackTime         = 1000
    PreparationTime    = 200
    PersistentPrepTime = 600
    PackTime           = 1200
  End
End

Object RangedArcher
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ArrowStormUpdate ModuleTag_Storm
    SpecialPowerTemplate = TestArrowStorm
    StartAbilityRange = 100
    UnpackTime = 0
    PreparationTime = 1000
    PackTime = 0
  End
End

Object GatedArcher
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ArrowStormUpdate ModuleTag_Storm
    SpecialPowerTemplate = TestArrowStorm
    RequiredConditions = MOVING
    UnpackTime = 0
    PreparationTime = 1000
    PackTime = 0
  End
End

Object XPArcher
  KindOf = INFANTRY
  IsTrainable = Yes
  ; R15 L5-P10: an XP fixture MUST declare ExperienceRequired (round test convention).
  ; ExperienceTracker.AddExperiencePoints/SetExperienceAndLevel walk
  ; `_currentExperience >= (Definition.ExperienceRequired?[level] ?? 0)` over all four
  ; VeterancyLevels, so with the table absent every threshold reads 0 and the very first
  ; award - including ExperienceUpdate.Initialize's floor-of-1 - promotes the object
  ; straight to Heroic, firing OnVeterancyLevelChanged and the ActiveBody health-bonus
  ; path. That cascade has nothing to do with this module and (post R15 L1-11, which put
  ; the veterancy clamp and the ActiveBody switch on that same path) is live state churn
  ; inside an XP contract test. Thresholds are set far above the 100/300 this fixture can
  ; award, so the object stays Regular and the delta assertions measure the award alone.
  ExperienceRequired = 0 1000 2000 3000
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ArrowStormUpdate ModuleTag_Storm
    SpecialPowerTemplate = TestArrowStorm
    UnpackTime = 0
    PreparationTime = 1000
    PackTime = 0
    AwardXPForTriggering = 100
  End
End

Object PersistentXPArcher
  KindOf = INFANTRY
  IsTrainable = Yes
  ; See XPArcher: declared so the floor-of-1 baseline and the per-trigger awards cannot
  ; promote the object and pull unrelated veterancy machinery into the measurement.
  ExperienceRequired = 0 1000 2000 3000
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ArrowStormUpdate ModuleTag_Storm
    SpecialPowerTemplate = TestArrowStorm
    UnpackTime         = 0
    PreparationTime    = 200
    PersistentPrepTime = 200
    PackTime           = 0
    AwardXPForTriggering = 100
  End
End

Object HeldFieldArcher
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ArrowStormUpdate ModuleTag_Storm
    SpecialPowerTemplate = TestArrowStorm
    StartAbilityRange = 320.0
    UnpackTime = 1000
    PreparationTime = 200
    PersistentPrepTime = 600
    PackTime = 1200
    UnpackingVariation = 1
    ParalyzeDurationWhenCompleted = 600
    ParalyzeDurationWhenAborted = 800
    ApproachRequiresLOS = Yes
    AwardXPForTriggering = 0
    WeaponTemplate = TestBowArrowStorm
    TargetRadius = 200
    ShotsPerTarget = 1
    ShotsPerBurst = 7
    MaxShots = 70
    CanShootEmptyGround = Yes
  End
End

Object TestHero
  KindOf = INFANTRY
  IsTrainable = Yes
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x415257) // 'ARW' as hex bytes, arbitrary
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static ArrowStormUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<ArrowStormUpdate>().Single();

    private static void Step(HeadlessSimGame game, int count)
    {
        for (var i = 0; i < count; i++)
        {
            game.Step();
        }
    }

    // 1. Wrong-template-name is a no-op.
    [Fact]
    public void Initiate_WrongTemplateName_IsNoOp()
    {
        var game = NewGame();
        var archer = game.SpawnObject("ArrowStormArcher", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(archer);

        game.Step();

        Assert.False(module.InitiateIntentToDoSpecialPower("NotMyPower", null));
        Assert.True(module.IsPacked);
        Assert.False(archer.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));
        Assert.Equal(0, module.TriggerCount);
    }

    // 2. Full phase spine, exact frame boundaries pinned from both sides. "start" below is the
    // frame on which InitiateIntentToDoSpecialPower was accepted (established by stepping once
    // first, per the sleepy-update caveat - never counted from construction).
    [Fact]
    public void PhaseOrder_UnpackPrepareTriggerPack_ExactFrameBoundaries()
    {
        var game = NewGame();
        var archer = game.SpawnObject("ArrowStormArcher", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(archer);

        game.Step();
        Assert.True(module.InitiateIntentToDoSpecialPower("TestArrowStorm", null));

        // start+1 .. start+4: Unpacking.
        for (var i = 1; i <= 4; i++)
        {
            game.Step();
            Assert.True(archer.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));
            Assert.Equal(0, module.TriggerCount);
        }

        // start+5: boundary - Unpacking clears, Preparing begins.
        game.Step();
        Assert.False(archer.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));

        // start+6 .. start+9: Preparing, no trigger yet.
        for (var i = 6; i <= 9; i++)
        {
            game.Step();
            Assert.Equal(0, module.TriggerCount);
            Assert.False(archer.ModelConditionFlags.Get(ModelConditionFlag.Packing));
        }

        // start+10: the trigger and the Preparing->Packing transition happen in the same pass.
        game.Step();
        Assert.Equal(1, module.TriggerCount);
        Assert.True(archer.ModelConditionFlags.Get(ModelConditionFlag.Packing));

        // start+11 .. start+14: still Packing.
        for (var i = 11; i <= 14; i++)
        {
            game.Step();
            Assert.True(archer.ModelConditionFlags.Get(ModelConditionFlag.Packing));
        }

        // start+15: Packing clears, back to Packed.
        game.Step();
        Assert.False(archer.ModelConditionFlags.Get(ModelConditionFlag.Packing));
        Assert.True(module.IsPacked);
    }

    // 3. Zero UnpackTime skips Unpacking entirely.
    [Fact]
    public void ZeroUnpackTime_SkipsUnpackingEntirely()
    {
        var game = NewGame();
        var archer = game.SpawnObject("RangedArcher", game.CivilianPlayer, Vector3.Zero);
        var near = game.SpawnObject("TestHero", game.CivilianPlayer, new Vector3(50, 0, 0));
        var module = ModuleOf(archer);

        game.Step();
        Assert.True(module.InitiateIntentToDoSpecialPower("TestArrowStorm", near));

        for (var i = 1; i <= 4; i++)
        {
            game.Step();
            Assert.False(archer.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));
            Assert.Equal(0, module.TriggerCount);
        }

        // Trigger lands exactly PreparationTime (5) frames after initiation.
        game.Step();
        Assert.Equal(1, module.TriggerCount);
        Assert.False(archer.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));
    }

    // 4. Zero PackTime returns to Packed on the trigger frame.
    [Fact]
    public void ZeroPackTime_ReturnsToPackedOnTheTriggerFrame()
    {
        var game = NewGame();
        var archer = game.SpawnObject("RangedArcher", game.CivilianPlayer, Vector3.Zero);
        var near = game.SpawnObject("TestHero", game.CivilianPlayer, new Vector3(50, 0, 0));
        var module = ModuleOf(archer);

        game.Step();
        Assert.True(module.InitiateIntentToDoSpecialPower("TestArrowStorm", near));

        Step(game, 5);
        Assert.Equal(1, module.TriggerCount);
        Assert.True(module.IsPacked);
        Assert.False(archer.ModelConditionFlags.Get(ModelConditionFlag.Packing));

        // A fresh activation is accepted on the very same frame.
        Assert.True(module.InitiateIntentToDoSpecialPower("TestArrowStorm", near));
    }

    // 5. PersistentPrepTime re-trigger cadence: first-prep span vs persistent span.
    [Fact]
    public void PersistentPrepTime_RetriggerCadence_FirstPrepThenPersistentSpans()
    {
        var game = NewGame();
        var archer = game.SpawnObject("PersistentArcher", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(archer);

        game.Step();
        Assert.True(module.InitiateIntentToDoSpecialPower("TestArrowStorm", null));

        // start+6 (5 unpack + 1 first-prep): the FIRST trigger uses PreparationTime.
        Step(game, 6);
        Assert.Equal(1, module.TriggerCount);

        // Every trigger after that uses PersistentPrepTime (3 frames), not PreparationTime -
        // this is the boundary that distinguishes the two spans.
        Step(game, 3);
        Assert.Equal(2, module.TriggerCount);

        Step(game, 3);
        Assert.Equal(3, module.TriggerCount);

        Step(game, 3);
        Assert.Equal(4, module.TriggerCount);

        // One frame further: the loop keeps going, never enters Packing.
        game.Step();
        Assert.False(module.IsPacked);
        Assert.False(archer.ModelConditionFlags.Get(ModelConditionFlag.Packing));
    }

    // 6. Abort() mid-persistent-loop returns to Packed without Packing.
    [Fact]
    public void Abort_DuringPersistentLoop_ReturnsToPackedWithoutPacking()
    {
        var game = NewGame();
        var archer = game.SpawnObject("PersistentArcher", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(archer);

        game.Step();
        Assert.True(module.InitiateIntentToDoSpecialPower("TestArrowStorm", null));

        while (module.TriggerCount < 2)
        {
            game.Step();
        }

        Assert.True(module.Abort());
        Assert.True(module.IsPacked);
        Assert.False(archer.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));
        Assert.False(archer.ModelConditionFlags.Get(ModelConditionFlag.Packing));

        Step(game, 10);
        Assert.Equal(2, module.TriggerCount);
        Assert.True(module.IsPacked);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestArrowStorm", null));
        Assert.Equal(0, module.TriggerCount);
    }

    // 7. StartAbilityRange out-of-range refusal + in-range accept + null-target gate skip.
    [Fact]
    public void StartAbilityRange_OutOfRange_RefusesActivation()
    {
        var game = NewGame();
        var archer = game.SpawnObject("RangedArcher", game.CivilianPlayer, Vector3.Zero);
        var far = game.SpawnObject("TestHero", game.CivilianPlayer, new Vector3(150, 0, 0));
        var module = ModuleOf(archer);

        Assert.False(module.InitiateIntentToDoSpecialPower("TestArrowStorm", far));
        Assert.True(module.IsPacked);
        Assert.False(archer.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));
    }

    [Fact]
    public void StartAbilityRange_InRange_Accepts()
    {
        var game = NewGame();
        var archer = game.SpawnObject("RangedArcher", game.CivilianPlayer, Vector3.Zero);
        var near = game.SpawnObject("TestHero", game.CivilianPlayer, new Vector3(50, 0, 0));
        var module = ModuleOf(archer);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestArrowStorm", near));
        Assert.False(module.IsPacked);
    }

    [Fact]
    public void StartAbilityRange_NullTriggeringObject_GateSkipped()
    {
        var game = NewGame();
        var archer = game.SpawnObject("RangedArcher", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(archer);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestArrowStorm", null));
        Assert.False(module.IsPacked);
    }

    // 8. AwardXPForTriggering fires exactly once per trigger, on the module's own GameObject.
    [Fact]
    public void AwardXPForTriggering_FiresExactlyOncePerTrigger()
    {
        var game = NewGame();
        var archer = game.SpawnObject("XPArcher", game.CivilianPlayer, Vector3.Zero);
        var bystander = game.SpawnObject("TestHero", game.CivilianPlayer, new Vector3(1, 0, 0));
        var module = ModuleOf(archer);

        Assert.Equal(0, archer.ExperienceTracker.CurrentExperience);

        game.Step();
        Assert.True(module.InitiateIntentToDoSpecialPower("TestArrowStorm", null));

        // Both fixtures are IsTrainable, so the automatic ModuleTag_ExperienceHelper
        // (ExperienceUpdate.Initialize) floors their XP at 1 on its first tick - a baseline
        // this module neither controls nor should be asserted against. Sample the baseline
        // after the helper has settled but before the trigger frame, and assert the award
        // as a delta.
        //
        // R15 L5-P10: the helper's Initialize now lands on the SECOND Step, not the first
        // (measured by the INT-R2A gate on the L1-11 control shape), so the baseline must be
        // sampled at least two Steps in. This four-Step window already satisfies that; the
        // sample point is pinned here so a future retiming of the helper trips this comment
        // rather than the assertion.
        Step(game, 4);
        Assert.Equal(0, module.TriggerCount);
        var archerBaseline = archer.ExperienceTracker.CurrentExperience;
        var bystanderBaseline = bystander.ExperienceTracker.CurrentExperience;

        Step(game, 1);
        Assert.Equal(1, module.TriggerCount);
        Assert.Equal(archerBaseline + 100, archer.ExperienceTracker.CurrentExperience);
        Assert.Equal(bystanderBaseline, bystander.ExperienceTracker.CurrentExperience);

        // No further award from stepping past the trigger frame.
        Step(game, 10);
        Assert.Equal(archerBaseline + 100, archer.ExperienceTracker.CurrentExperience);
        Assert.Equal(bystanderBaseline, bystander.ExperienceTracker.CurrentExperience);

        // The award must stay an award: with ExperienceRequired declared, 100 XP is far below
        // the rank-1 threshold, so no promotion may have happened. If this ever fails the
        // fixture has lost its ExperienceRequired table and the deltas above are measuring a
        // veterancy cascade as well as the award.
        Assert.Equal(VeterancyLevel.Regular, archer.ExperienceTracker.VeterancyLevel);
    }

    [Fact]
    public void AwardXPForTriggering_PersistentAbility_AwardsOncePerTrigger()
    {
        var game = NewGame();
        var archer = game.SpawnObject("PersistentXPArcher", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(archer);

        game.Step();
        Assert.True(module.InitiateIntentToDoSpecialPower("TestArrowStorm", null));

        // PreparationTime is 1 frame here, so the first trigger lands on the same frame the
        // ExperienceHelper would apply its floor-of-1 baseline; which of the two runs first is
        // module-order-dependent and must not decide the assertion. Measure trigger 2 and 3 as
        // a delta from the post-first-trigger total, which is baseline-independent.
        while (module.TriggerCount < 1)
        {
            game.Step();
        }

        var afterFirstTrigger = archer.ExperienceTracker.CurrentExperience;

        while (module.TriggerCount < 3)
        {
            game.Step();
        }

        Assert.Equal(afterFirstTrigger + 200, archer.ExperienceTracker.CurrentExperience);
        Assert.Equal(VeterancyLevel.Regular, archer.ExperienceTracker.VeterancyLevel);
    }

    // 9. RequiredConditions ModelConditionFlag gate.
    [Fact]
    public void RequiredConditions_UnmetAtInitiation_Refuses()
    {
        var game = NewGame();
        var archer = game.SpawnObject("GatedArcher", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(archer);

        Assert.False(module.InitiateIntentToDoSpecialPower("TestArrowStorm", null));
        Assert.True(module.IsPacked);

        archer.SetModelConditionState(ModelConditionFlag.Moving);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestArrowStorm", null));
        Assert.False(module.IsPacked);
    }

    // 10. Xfer round-trip mid-Preparing/Unpacking/Packing resumes with identical remaining frames.
    [Fact]
    public void Xfer_RoundTripMidPreparing_ResumesWithIdenticalRemainingFrames()
    {
        var game = NewGame();
        var archer = game.SpawnObject("ArrowStormArcher", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(archer);

        game.Step();
        Assert.True(live.InitiateIntentToDoSpecialPower("TestArrowStorm", null));

        // 5 frames of Unpacking, then 2 frames into Preparing (3 remaining of 5).
        Step(game, 7);
        Assert.False(archer.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));
        Assert.Equal(0, live.TriggerCount);

        var shadowHost = game.SpawnObject("ArrowStormArcher", game.CivilianPlayer, new Vector3(200, 0, 0));
        var shadow = ModuleOf(shadowHost);
        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);

        var liveState = PortedModuleTestKit.Save(live);
        PortedModuleTestKit.Load(shadow, liveState);

        Step(game, 3);
        Assert.Equal(1, live.TriggerCount);
    }

    [Fact]
    public void Xfer_RoundTripMidUnpacking_ResumesWithIdenticalRemainingFrames()
    {
        var game = NewGame();
        var archer = game.SpawnObject("ArrowStormArcher", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(archer);

        game.Step();
        Assert.True(live.InitiateIntentToDoSpecialPower("TestArrowStorm", null));
        Step(game, 2);
        Assert.True(archer.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));

        var shadowHost = game.SpawnObject("ArrowStormArcher", game.CivilianPlayer, new Vector3(200, 0, 0));
        var shadow = ModuleOf(shadowHost);
        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void Xfer_RoundTripMidPacking_ResumesWithIdenticalRemainingFrames()
    {
        var game = NewGame();
        var archer = game.SpawnObject("ArrowStormArcher", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(archer);

        game.Step();
        Assert.True(live.InitiateIntentToDoSpecialPower("TestArrowStorm", null));
        Step(game, 10);
        Assert.True(archer.ModelConditionFlags.Get(ModelConditionFlag.Packing));

        var shadowHost = game.SpawnObject("ArrowStormArcher", game.CivilianPlayer, new Vector3(200, 0, 0));
        var shadow = ModuleOf(shadowHost);
        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    // 11. Held fields parse and are exposed read-only, with provably zero behavior.
    [Fact]
    public void HeldFields_ParseAndAreExposedReadOnly_WithNoBehavior()
    {
        var game = NewGame();
        var archer = game.SpawnObject("HeldFieldArcher", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(archer);
        var body = archer.BodyModule;
        var healthBefore = body.Health;

        Assert.Equal("TestBowArrowStorm", module.WeaponTemplate);
        Assert.Equal(200, module.TargetRadius);
        Assert.Equal(1, module.ShotsPerTarget);
        Assert.Equal(7, module.ShotsPerBurst);
        Assert.Equal(70, module.MaxShots);
        Assert.True(module.CanShootEmptyGround);
        Assert.Equal(1, module.UnpackingVariation);
        Assert.True(module.ApproachRequiresLos);
        Assert.Equal(800, module.ParalyzeDurationWhenAborted);
        Assert.Equal(600, module.ParalyzeDurationWhenCompleted);

        game.Step();
        Assert.True(module.InitiateIntentToDoSpecialPower("TestArrowStorm", null));

        while (module.TriggerCount < 2)
        {
            game.Step();
        }

        Assert.Equal(healthBefore, body.Health);
        Assert.False(archer.IsDisabledByType(DisabledType.Paralyzed));
    }

    // 11b. R15 L5-P10 regression fence for the held paralyze tail, re-graded against the
    // post-L5-P6 world. When ArrowStormUpdate landed (a35f4fa5), GameObject.Update() had zero
    // callers, so DisabledType windows never expired and "not paralyzed" was unfalsifiable:
    // an accidental Disable(Paralyzed) would have stuck forever and been just as visible, but
    // so would a disable applied and never cleared by anything. L5-P6/A0-prime then wired the
    // per-object sweep into GameLogic.Update() after the frame-counter increment, so a T-frame
    // window now reads clear on the T+1'th Update(). This test walks BOTH exit paths -
    // persistent triggering, and Abort() - past the T+1 boundary of both declared durations
    // (ParalyzeDurationWhenCompleted = 600ms = 3 frames, ParalyzeDurationWhenAborted = 800ms =
    // 4 frames), so a disable applied at either exit would now be observable while it lasted
    // instead of being indistinguishable from the permanent case.
    //
    // This must stay RED-on-behavior until the shot loop is specced: the paralyze split has no
    // source (see the module's file header), so the correct behavior today is no disable at
    // all. When it is specced it composes GameObject.Disable(DisabledType.Paralyzed, ...) -
    // not ParalyzeNugget.cs - and this test is where the window assertions go.
    [Fact]
    public void ParalyzeTail_HeldNotModeled_NoDisableOnEitherExitPath_AcrossTPlusOne()
    {
        var game = NewGame();
        var archer = game.SpawnObject("HeldFieldArcher", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(archer);

        game.Step();
        Assert.True(module.InitiateIntentToDoSpecialPower("TestArrowStorm", null));

        // Persistent-trigger path: step well past the T+1 boundary of the longer (aborted)
        // duration, checking every frame rather than only at the end - a 3- or 4-frame window
        // opened at any trigger would otherwise be stepped straight over.
        while (module.TriggerCount < 2)
        {
            game.Step();
            Assert.False(archer.IsDisabledByType(DisabledType.Paralyzed));
        }

        for (var i = 0; i < 6; i++)
        {
            game.Step();
            Assert.False(archer.IsDisabledByType(DisabledType.Paralyzed));
        }

        // Abort path (the exit GPL's onExit(false) models, and the one a future split would
        // most plausibly call "aborted").
        Assert.True(module.Abort());
        Assert.True(module.IsPacked);
        Assert.False(archer.IsDisabledByType(DisabledType.Paralyzed));

        for (var i = 0; i < 6; i++)
        {
            game.Step();
            Assert.False(archer.IsDisabledByType(DisabledType.Paralyzed));
        }

        // And the durations are still merely parsed and held - no exit path consumed them.
        Assert.Equal(600, module.ParalyzeDurationWhenCompleted);
        Assert.Equal(800, module.ParalyzeDurationWhenAborted);
    }

    // 12. Negative control: never self-starts while Packed.
    [Fact]
    public void Update_WhilePacked_NeverSelfStarts()
    {
        var game = NewGame();
        var archer = game.SpawnObject("ArrowStormArcher", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(archer);

        Step(game, 10);

        Assert.True(module.IsPacked);
        Assert.Equal(0, module.TriggerCount);
        Assert.False(archer.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));
        Assert.False(archer.ModelConditionFlags.Get(ModelConditionFlag.Packing));
        Assert.Equal(0, archer.ExperienceTracker.CurrentExperience);
    }
}
