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

        Step(game, 5);
        Assert.Equal(1, module.TriggerCount);
        Assert.Equal(100, archer.ExperienceTracker.CurrentExperience);
        Assert.Equal(0, bystander.ExperienceTracker.CurrentExperience);

        // No further award from stepping past the trigger frame.
        Step(game, 10);
        Assert.Equal(100, archer.ExperienceTracker.CurrentExperience);
    }

    [Fact]
    public void AwardXPForTriggering_PersistentAbility_AwardsOncePerTrigger()
    {
        var game = NewGame();
        var archer = game.SpawnObject("PersistentXPArcher", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(archer);

        game.Step();
        Assert.True(module.InitiateIntentToDoSpecialPower("TestArrowStorm", null));

        while (module.TriggerCount < 3)
        {
            game.Step();
        }

        Assert.Equal(300, archer.ExperienceTracker.CurrentExperience);
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
