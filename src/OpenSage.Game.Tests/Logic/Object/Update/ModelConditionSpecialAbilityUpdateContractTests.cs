// Mocked-game unit tests for the ModelConditionSpecialAbilityUpdate port (R13, spec
// research/modules-r13/specs/ModelConditionSpecialAbilityUpdateModuleData.md §3): one test per
// behavior branch, [create -> trigger/tick -> observable effect], covering the spec's own
// contract-test plan cases 1-9.
//
// Frame arithmetic: all duration fields are milliseconds (ParseDurationLogicFrames, the SAGE
// INI convention), quantized to the frozen 5 Hz logic rate - "1000" below is exactly 5 logic
// frames, "400" is exactly 2 logic frames.
//
// Sleepy-update caveat (spec §3): a freshly spawned object's module sleeps forever until
// InitiateIntentToDoSpecialPower succeeds; before that, Update() never runs at all. After a
// successful Initiate call, the module wakes UpdateSleepTime.None-style (every following
// frame) for as long as any active phase persists.
//
// Frame-vs-Step arithmetic (the trap spec §3 flags, and the one these tests originally fell
// into): one game.Step() runs the update pass for the CURRENT frame and THEN increments the
// frame counter (GameLogic.Update: `var now = _currentFrame; ...; _currentFrame++`). A test
// calls Initiate outside any update pass, i.e. while frame N is still pending, and Initiate's
// SetWakeFrame(UpdateSleepTime.None) schedules the module for frame N+1 - so the first Step
// (which runs frame N) does not tick the module at all, and a D-frame phase started at frame N
// ends on the update pass for frame N+D, which is the (D+1)'th Step. Counting Steps instead of
// frames therefore under-runs every first phase by exactly one. These tests count FRAMES:
// StepThroughFrame(game, start + D) below runs the game up to and including the update pass for
// frame start+D, whatever the Step bookkeeping happens to be. The module itself is unchanged by
// this - GPL's own countdown (startUnpacking sets m_animFrames = unpackTime, each update()
// decrements, complete at zero) puts the boundary on exactly that frame.
//
// Experience baseline (R13 repair, the second trap these tests fell into): AwardXPForTriggering
// is asserted against RankOneFloor, never against literal zero. GameObject.cs adds an
// ExperienceUpdate helper ("ModuleTag_ExperienceHelper") to every object on every non-Generals
// game, and that helper's first tick raises a still-zero CurrentExperience to the rank-1 floor
// of 1 (ExperienceUpdate.Initialize: `if (CurrentExperience == 0) SetExperienceAndLevel(1)`).
// The floor lands once, on the first Step after the XP-receiving object is spawned, and is
// engine-wide landed behavior wholly outside this module - so any test that steps the game at
// all and then reads a trainable object's absolute experience is reading award + 1. The module
// itself is unaffected: it awards exactly AwardXPForTriggering per trigger through
// ExperienceTracker.AddExperiencePoints, which is what the deltas below assert.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class ModelConditionSpecialAbilityUpdateContractTests
{
    private const string Definitions = @"
Object Watcher
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ModelConditionSpecialAbilityUpdate ModuleTag_Ability
    SpecialPowerTemplate = TestAbilityPower
    UnpackTime = 1000
    PreparationTime = 1000
    PackTime = 1000
    AwardXPForTriggering = 50
    TriggerSound = Sound_Trigger
  End
End

Object PersistentWatcher
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ModelConditionSpecialAbilityUpdate ModuleTag_Ability
    SpecialPowerTemplate = TestAbilityPower
    UnpackTime = 0
    PreparationTime = 1000
    PersistentPrepTime = 1000
    PackTime = 1000
    AwardXPForTriggering = 10
  End
End

Object ZeroWatcher
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ModelConditionSpecialAbilityUpdate ModuleTag_Ability
    SpecialPowerTemplate = TestAbilityPower
    UnpackTime = 0
    PreparationTime = 0
    PersistentPrepTime = 0
    PackTime = 0
    AwardXPForTriggering = 5
  End
End

Object StealthyWatcher
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ModelConditionSpecialAbilityUpdate ModuleTag_Ability
    SpecialPowerTemplate = TestAbilityPower
    UnpackTime = 1000
    LoseStealthOnTrigger = Yes
    PreTriggerUnstealthTime = 400
  End
  Behavior = StealthUpdate ModuleTag_Stealth
    InnateStealth = Yes
    StealthDelay = 400
  End
End

Object StealthyWatcherNoLose
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ModelConditionSpecialAbilityUpdate ModuleTag_Ability
    SpecialPowerTemplate = TestAbilityPower
    UnpackTime = 1000
    LoseStealthOnTrigger = No
    PreTriggerUnstealthTime = 400
  End
  Behavior = StealthUpdate ModuleTag_Stealth
    InnateStealth = Yes
  End
End

Object TerrorWatcher
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ModelConditionSpecialAbilityUpdate ModuleTag_Ability
    SpecialPowerTemplate = TestAbilityPower
    UnpackTime = 1000
    PreparationTime = 1000
    PackTime = 1000
    GenerateTerror = Yes
    EmotionPulseRadius = 200
    GenerateUncontrollableFear = Yes
    ObjectFilter = ANY +INFANTRY
  End
End

Object UnmodeledFieldsWatcher
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ModelConditionSpecialAbilityUpdate ModuleTag_Ability
    SpecialPowerTemplate = TestAbilityPower
    UnpackTime = 1000
    PreparationTime = 1000
    PackTime = 1000
    WhichSpecialPower = 2
    DisableWhenWearingTheRing = Yes
    UnpackingVariation = 3
    MustFinishAbility = Yes
  End
End

Object XferWatcher
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ModelConditionSpecialAbilityUpdate ModuleTag_Ability
    SpecialPowerTemplate = TestAbilityPower
    UnpackTime = 0
    PreparationTime = 1000
    AwardXPForTriggering = 25
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

    private static HeadlessSimGame NewGame(uint seed = 0x5AB111) => new(SageGame.Bfme2Rotwk, seed);

    private static HeadlessSimGame NewLoadedGame(uint seed = 0x5AB111)
    {
        var game = NewGame(seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static ModelConditionSpecialAbilityUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<ModelConditionSpecialAbilityUpdate>().Single();

    private static void Step(HeadlessSimGame game, int count)
    {
        for (var i = 0; i < count; i++)
        {
            game.Step();
        }
    }

    /// <summary>
    /// Runs the game up to and including the update pass for <paramref name="frame"/> - the
    /// frame-counted alternative to counting Steps (see the file-header note).
    /// </summary>
    private static void StepThroughFrame(HeadlessSimGame game, LogicFrame frame)
    {
        while (game.GameLogic.CurrentFrame <= frame)
        {
            game.Step();
        }
    }

    private static LogicFrameSpan Frames(uint count) => new(count);

    /// <summary>
    /// Experience a trainable object carries once the engine's own ExperienceUpdate helper has
    /// ticked, before this module has awarded anything - see the file-header note.
    /// </summary>
    private const int RankOneFloor = 1;

    [Fact]
    public void InitiateIntentToDoSpecialPower_WrongTemplateName_NoOp()
    {
        var game = NewLoadedGame();
        var watcher = game.SpawnObject("Watcher", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(watcher);

        // Sleepy-update: nothing has triggered yet, so this tick is a no-op for the module.
        game.Step();

        Assert.False(module.InitiateIntentToDoSpecialPower("WrongName", null));
        Assert.False(watcher.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));

        // No phase corruption: the correctly-named power still initiates afterwards.
        Assert.True(module.InitiateIntentToDoSpecialPower("TestAbilityPower", null));
    }

    [Fact]
    public void UnpackThenPrepareThenAutoTrigger_ZeroPersistentPrepTime_PacksAndReturnsToPacked()
    {
        var game = NewLoadedGame();
        var watcher = game.SpawnObject("Watcher", game.CivilianPlayer, Vector3.Zero);
        var hero = game.SpawnObject("TestHero", game.CivilianPlayer, new Vector3(1, 0, 0));
        var module = ModuleOf(watcher);
        var recorder = RecordingSimEvents.InstallOn(game);

        var start = game.GameLogic.CurrentFrame;
        Assert.True(module.InitiateIntentToDoSpecialPower("TestAbilityPower", hero));

        // Unpacking flag is set synchronously - no Step needed to observe it.
        Assert.True(watcher.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));

        // UnpackTime = 5 frames: still Unpacking on the last frame of the window...
        StepThroughFrame(game, start + Frames(4));
        Assert.True(watcher.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));

        // ...and Prepared on the frame the countdown reaches zero.
        StepThroughFrame(game, start + Frames(5));
        Assert.False(watcher.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));
        Assert.Equal(RankOneFloor, hero.ExperienceTracker.CurrentExperience);

        // PreparationTime = 5 more frames: preparation completes, effect auto-fires.
        StepThroughFrame(game, start + Frames(10));
        Assert.Equal(RankOneFloor + 50, hero.ExperienceTracker.CurrentExperience);
        Assert.Contains(("Sound_Trigger", watcher.Id), recorder.AudioEvents);
        Assert.True(watcher.ModelConditionFlags.Get(ModelConditionFlag.Packing));

        // PackTime = 5 more frames: pack completes, back to Packed.
        StepThroughFrame(game, start + Frames(15));
        Assert.False(watcher.ModelConditionFlags.Get(ModelConditionFlag.Packing));

        // Fully cycled: a fresh Initiate call succeeds again.
        Assert.True(module.InitiateIntentToDoSpecialPower("TestAbilityPower", null));
    }

    [Fact]
    public void PersistentPrepTime_NonZero_RepeatsTriggerWithoutPacking()
    {
        var game = NewLoadedGame();
        var watcher = game.SpawnObject("PersistentWatcher", game.CivilianPlayer, Vector3.Zero);
        var hero = game.SpawnObject("TestHero", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(watcher);

        var start = game.GameLogic.CurrentFrame;
        Assert.True(module.InitiateIntentToDoSpecialPower("TestAbilityPower", hero));

        // UnpackTime = 0, so Prepared starts on the Initiate call itself; the first 5-frame
        // Prepared window completes on frame start+5 and the effect fires.
        StepThroughFrame(game, start + Frames(5));
        Assert.Equal(RankOneFloor + 10, hero.ExperienceTracker.CurrentExperience);

        // Looped back to Prepared per the GPL repeating-loop reading - did NOT proceed to
        // Packing. This is the case that discriminates the GPL repeating reading from
        // ToggleHidden's one-shot-extension reading.
        Assert.False(watcher.ModelConditionFlags.Get(ModelConditionFlag.Packing));

        // Second PersistentPrepTime window: fires again, still without packing.
        StepThroughFrame(game, start + Frames(10));
        Assert.Equal(RankOneFloor + 20, hero.ExperienceTracker.CurrentExperience);
        Assert.False(watcher.ModelConditionFlags.Get(ModelConditionFlag.Packing));

        // Third window: "repeat forever" is a loop, not a single extra pass - a one-shot or
        // twice-only reading of PersistentPrepTime dies here.
        StepThroughFrame(game, start + Frames(15));
        Assert.Equal(RankOneFloor + 30, hero.ExperienceTracker.CurrentExperience);
        Assert.False(watcher.ModelConditionFlags.Get(ModelConditionFlag.Packing));

        // ...and the cycle is genuinely never returning to Packed: a fresh Initiate is
        // rejected because the module is still mid-cycle (spec §1.8's Packed-only gate).
        Assert.False(module.InitiateIntentToDoSpecialPower("TestAbilityPower", hero));
    }

    [Fact]
    public void ZeroDurationFields_SkipPhasesImmediately()
    {
        var game = NewLoadedGame();
        var watcher = game.SpawnObject("ZeroWatcher", game.CivilianPlayer, Vector3.Zero);
        var hero = game.SpawnObject("TestHero", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(watcher);

        // No Step needed anywhere below - every phase is zero-duration, so the whole cycle
        // collapses to one synchronous call.
        Assert.True(module.InitiateIntentToDoSpecialPower("TestAbilityPower", hero));

        // No RankOneFloor term here (unlike every other XP assertion in this file): the game is
        // never stepped in this test, so the engine's ExperienceUpdate helper has not ticked and
        // the hero's experience is still at its literal spawn value of zero.
        Assert.Equal(5, hero.ExperienceTracker.CurrentExperience);
        Assert.False(watcher.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));
        Assert.False(watcher.ModelConditionFlags.Get(ModelConditionFlag.Packing));

        // Phase == Packed: a fresh Initiate call succeeds immediately.
        Assert.True(module.InitiateIntentToDoSpecialPower("TestAbilityPower", null));
    }

    [Fact]
    public void LoseStealthOnTrigger_PreTriggerUnstealthTime_MarksDetectedDuringUnpacking()
    {
        var game = NewLoadedGame();
        var watcher = game.SpawnObject("StealthyWatcher", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(watcher);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestAbilityPower", null));

        // Still mid-Unpack: the module's own first tick lands one frame after the Initiate call
        // (file-header note), so 4 of the 5 UnpackTime frames remain here - above the 2-frame
        // threshold.
        Step(game, 2);
        Assert.False(watcher.TestStatus(ObjectStatus.Detected));

        // 1 (then 0) frames remain, below the 2-frame threshold: MarkAsDetected fires. One
        // extra frame of margin here is deliberate - it removes any dependence on the relative
        // same-frame ordering between this module and the StealthUpdate sibling that actually
        // flips ObjectStatus.Detected off the timer MarkAsDetected arms.
        Step(game, 3);
        Assert.True(watcher.TestStatus(ObjectStatus.Detected));
    }

    [Fact]
    public void LoseStealthOnTrigger_False_NeverMarksDetected()
    {
        var game = NewLoadedGame();
        var watcher = game.SpawnObject("StealthyWatcherNoLose", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(watcher);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestAbilityPower", null));

        Step(game, 5); // Full unpack.
        Assert.False(watcher.TestStatus(ObjectStatus.Detected));
    }

    [Fact]
    public void TerrorFields_ParseCorrectlyAndAreHeldNotConsumed()
    {
        var game = NewLoadedGame();
        var watcher = game.SpawnObject("TerrorWatcher", game.CivilianPlayer, Vector3.Zero);
        var bystander = game.SpawnObject("TestHero", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = ModuleOf(watcher);

        Step(game, 5);

        var start = game.GameLogic.CurrentFrame;
        Assert.True(module.InitiateIntentToDoSpecialPower("TestAbilityPower", null));
        StepThroughFrame(game, start + Frames(15)); // Full unpack -> prepare -> pack cycle.

        Assert.True(module.GenerateTerror);
        Assert.True(module.GenerateUncontrollableFear);
        Assert.NotNull(module.ObjectFilter);

        // Negative control: §1.3's "parsed, not modeled" posture holds - nothing accidentally
        // fires the terror model conditions on any nearby object.
        Assert.False(watcher.ModelConditionFlags.Get(ModelConditionFlag.EmotionTerror));
        Assert.False(watcher.ModelConditionFlags.Get(ModelConditionFlag.EmotionUncontrollablyAfraid));
        Assert.False(bystander.ModelConditionFlags.Get(ModelConditionFlag.EmotionTerror));
        Assert.False(bystander.ModelConditionFlags.Get(ModelConditionFlag.EmotionUncontrollablyAfraid));
    }

    [Fact]
    public void WhichSpecialPower_DisableWhenWearingTheRing_UnpackingVariation_MustFinishAbility_ParseOnly()
    {
        var game = NewLoadedGame();
        var watcher = game.SpawnObject("UnmodeledFieldsWatcher", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(watcher);

        Assert.Equal(2, module.WhichSpecialPower);
        Assert.True(module.DisableWhenWearingTheRing);
        Assert.Equal(3, module.UnpackingVariation);
        Assert.True(module.MustFinishAbility);

        // None of the four gates or blocks a full trigger cycle: unpack (5) + prepare (5) +
        // pack (5) frames, and the module is back at Packed on frame start+15.
        var start = game.GameLogic.CurrentFrame;
        Assert.True(module.InitiateIntentToDoSpecialPower("TestAbilityPower", null));
        StepThroughFrame(game, start + Frames(15));
        Assert.True(module.InitiateIntentToDoSpecialPower("TestAbilityPower", null));
    }

    [Fact]
    public void Xfer_SaveLoadRoundTrip_MidPreparation_PreservesPhaseAndFrame()
    {
        var game = NewLoadedGame();
        var watcher = game.SpawnObject("XferWatcher", game.CivilianPlayer, Vector3.Zero);
        var hero = game.SpawnObject("TestHero", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(watcher);

        var start = game.GameLogic.CurrentFrame;
        Assert.True(live.InitiateIntentToDoSpecialPower("TestAbilityPower", hero));

        // UnpackTime = 0: Prepared starts on the Initiate call, and the effect is due on frame
        // start+5 (PreparationTime = 5 frames). Save mid-window, with frames left to run.
        var triggerFrame = start + Frames(5);
        StepThroughFrame(game, start + Frames(1));
        Assert.Equal(RankOneFloor, hero.ExperienceTracker.CurrentExperience);

        var saved = PortedModuleTestKit.Save(live);
        var liveCrc = PortedModuleTestKit.LiveCrc(live);

        var shadowHost = game.SpawnObject("XferWatcher", game.CivilianPlayer, new Vector3(200, 0, 0));
        var shadow = ModuleOf(shadowHost);
        PortedModuleTestKit.Load(shadow, saved);

        Assert.Equal(liveCrc, PortedModuleTestKit.LiveCrc(shadow));

        // Retire the live instance's own host now that its mid-cycle state has been captured:
        // both `live` and `shadow` share the same triggering hero and the same game clock, so
        // leaving `live` on the sleepy-update queue would let it complete its own Prepared
        // window in parallel and double-award the hero's XP, confounding the very thing this
        // test is trying to isolate (that the SHADOW instance, and only the shadow instance,
        // reproduces the effect from its loaded state).
        game.GameLogic.DestroyObject(watcher);

        // Drive the loaded shadow instance directly through the frames remaining in the window
        // it was saved mid-way through (module scheduling itself is engine-owned and outside
        // this lightweight Xfer walk - see the file-header note on
        // ModelConditionSpecialAbilityUpdate.cs's own Xfer scope). The loaded _phaseEndFrame is
        // an absolute frame, so the shadow must fire on exactly triggerFrame: neither early
        // (the in-loop assertion below) nor late (the assertion after it).
        while (game.GameLogic.CurrentFrame < triggerFrame)
        {
            Assert.Equal(RankOneFloor, hero.ExperienceTracker.CurrentExperience);
            game.Step();
            shadow.Update();
        }

        Assert.Equal(RankOneFloor + 25, hero.ExperienceTracker.CurrentExperience);

        // PackTime = 0 on XferWatcher, so the loaded instance also collapsed straight back to
        // Packed: it accepts a fresh Initiate, proving the whole phase machine (not just the
        // one pending timer) survived the round trip.
        Assert.True(shadow.InitiateIntentToDoSpecialPower("TestAbilityPower", null));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewLoadedGame();
        var watcher = game.SpawnObject("Watcher", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(watcher);
        var start = game.GameLogic.CurrentFrame;
        Assert.True(live.InitiateIntentToDoSpecialPower("TestAbilityPower", null));

        // Genuinely mid-behavior: the module's own Update() has run at least once (the first
        // Step only runs the frame the Initiate call pre-dated - file-header note).
        StepThroughFrame(game, start + Frames(1));

        var shadowHost = game.SpawnObject("Watcher", game.CivilianPlayer, new Vector3(200, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
