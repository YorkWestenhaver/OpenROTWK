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

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
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

        Assert.True(module.InitiateIntentToDoSpecialPower("TestAbilityPower", hero));

        // Unpacking flag is set synchronously - no Step needed to observe it.
        Assert.True(watcher.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));

        Step(game, 5); // UnpackTime = 5 frames: unpack completes.
        Assert.False(watcher.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));

        Step(game, 5); // PreparationTime = 5 frames: preparation completes, effect auto-fires.
        Assert.Equal(50, hero.ExperienceTracker.CurrentExperience);
        Assert.Contains(("Sound_Trigger", watcher.Id), recorder.AudioEvents);
        Assert.True(watcher.ModelConditionFlags.Get(ModelConditionFlag.Packing));

        Step(game, 5); // PackTime = 5 frames: pack completes, back to Packed.
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

        Assert.True(module.InitiateIntentToDoSpecialPower("TestAbilityPower", hero));

        Step(game, 5); // First 5-frame Prepared window completes: effect fires.
        Assert.Equal(10, hero.ExperienceTracker.CurrentExperience);

        // Looped back to Prepared per the GPL repeating-loop reading - did NOT proceed to
        // Packing. This is the case that discriminates the GPL repeating reading from
        // ToggleHidden's one-shot-extension reading.
        Assert.False(watcher.ModelConditionFlags.Get(ModelConditionFlag.Packing));

        Step(game, 5); // Second 5-frame Prepared window completes: effect fires again.
        Assert.Equal(20, hero.ExperienceTracker.CurrentExperience);
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

        Step(game, 2); // Still mid-Unpack: 3 of 5 UnpackTime frames remain, above the 2-frame threshold.
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

        Assert.True(module.InitiateIntentToDoSpecialPower("TestAbilityPower", null));
        Step(game, 15); // Full unpack -> prepare -> pack cycle.

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

        // None of the four gates or blocks a full trigger cycle.
        Assert.True(module.InitiateIntentToDoSpecialPower("TestAbilityPower", null));
        Step(game, 15);
        Assert.True(module.InitiateIntentToDoSpecialPower("TestAbilityPower", null));
    }

    [Fact]
    public void Xfer_SaveLoadRoundTrip_MidPreparation_PreservesPhaseAndFrame()
    {
        var game = NewLoadedGame();
        var watcher = game.SpawnObject("XferWatcher", game.CivilianPlayer, Vector3.Zero);
        var hero = game.SpawnObject("TestHero", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(watcher);

        Assert.True(live.InitiateIntentToDoSpecialPower("TestAbilityPower", hero));

        // UnpackTime = 0: Prepared starts immediately. Tick 2 of the 5-frame Prepared window.
        Step(game, 2);

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

        // Drive the loaded shadow instance directly through its remaining 3 frames (module
        // scheduling itself is engine-owned and outside this lightweight Xfer walk - see the
        // file-header note on ModelConditionSpecialAbilityUpdate.cs's own Xfer scope).
        for (var i = 0; i < 3; i++)
        {
            game.Step();
            shadow.Update();
        }

        Assert.Equal(25, hero.ExperienceTracker.CurrentExperience);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewLoadedGame();
        var watcher = game.SpawnObject("Watcher", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(watcher);
        Assert.True(live.InitiateIntentToDoSpecialPower("TestAbilityPower", null));
        game.Step();

        var shadowHost = game.SpawnObject("Watcher", game.CivilianPlayer, new Vector3(200, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
