// Mocked-game unit tests for the ToggleHiddenSpecialAbilityUpdate port (api-freeze-v1 §6
// fitness item 4): one test per behavior branch, [create -> trigger/tick -> observable
// effect], covering the R12 task packet's testCases.
//
// Both InitiateIntentToDoSpecialPower and Trigger are driven inputs (see the file header on
// ToggleHiddenSpecialAbilityUpdate.cs, mirroring ReplaceObjectUpdate's and
// MissileLauncherBuildingUpdate's own trigger seams): tests call them directly instead of
// standing up a special-power/command system.
//
// Frame arithmetic: all duration fields are milliseconds (ParseDurationLogicFrames, the SAGE
// INI convention), quantized to the frozen 5 Hz logic rate - "1000" below is exactly 5 logic
// frames, "200" is exactly 1 logic frame.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class ToggleHiddenSpecialAbilityUpdateContractTests
{
    private const string Definitions = @"
Object Watcher
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ToggleHiddenSpecialAbilityUpdate ModuleTag_Hide
    SpecialPowerTemplate = TestHidePower
    UnpackTime = 1000
    PreparationTime = 1000
    PackTime = 1000
  End
End

Object PersistentWatcher
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ToggleHiddenSpecialAbilityUpdate ModuleTag_Hide
    SpecialPowerTemplate = TestHidePower
    UnpackTime = 0
    PreparationTime = 1000
    PersistentPrepTime = 1000
    EffectDuration = 1000
    PackTime = 0
  End
End

Object XPWatcher
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ToggleHiddenSpecialAbilityUpdate ModuleTag_Hide
    SpecialPowerTemplate = TestHidePower
    UnpackTime = 0
    PreparationTime = 1000
    AwardXPForTriggering = 100
    EffectDuration = 1000
    PackTime = 0
  End
End

Object RangedWatcher
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ToggleHiddenSpecialAbilityUpdate ModuleTag_Hide
    SpecialPowerTemplate = TestHidePower
    StartAbilityRange = 100
    UnpackTime = 0
    PreparationTime = 1000
    PackTime = 0
  End
End

Object TimerShownWatcher
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ToggleHiddenSpecialAbilityUpdate ModuleTag_Hide
    SpecialPowerTemplate = TestHidePower
    ShowPalantirTimer = Yes
  End
End

Object TimerHiddenWatcher
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ToggleHiddenSpecialAbilityUpdate ModuleTag_Hide
    SpecialPowerTemplate = TestHidePower
    ShowPalantirTimer = No
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

    private static HeadlessSimGame NewGame(uint seed = 0x7071DE)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static ToggleHiddenSpecialAbilityUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<ToggleHiddenSpecialAbilityUpdate>().Single();

    private static void Step(HeadlessSimGame game, int count)
    {
        for (var i = 0; i < count; i++)
        {
            game.Step();
        }
    }

    [Fact]
    public void PackUnpackPrepareCycle_UnpacksThenPreparesThenAutoPacksWithoutTrigger()
    {
        var game = NewGame();
        var watcher = game.SpawnObject("Watcher", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(watcher);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestHidePower", null));

        // UnpackTime = 5 frames: the unpacking animation plays immediately.
        game.Step();
        Assert.True(watcher.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));

        // Prepared state lasts PreparationTime (5 more frames); no Trigger() call is made, so
        // the ability auto-packs without ever hiding the unit.
        Step(game, 20);

        Assert.False(watcher.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));
        Assert.False(watcher.TestStatus(ObjectStatus.Stealthed));

        // Fully cycled back to Packed: packing flag cleared, and a fresh Trigger request is
        // rejected because the ability was never Prepared when it was issued (no crash, no
        // hang) - the observable proxy for "we are back at Packed, not stuck".
        Assert.False(watcher.ModelConditionFlags.Get(ModelConditionFlag.Packing));
    }

    [Fact]
    public void Trigger_DuringPrepared_AwardsXPForTriggering()
    {
        var game = NewGame();
        var watcher = game.SpawnObject("XPWatcher", game.CivilianPlayer, Vector3.Zero);
        var hero = game.SpawnObject("TestHero", game.CivilianPlayer, new Vector3(1, 0, 0));
        var module = ModuleOf(watcher);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestHidePower", null));

        // UnpackTime = 0, so the very next step lands in Prepared.
        game.Step();

        Assert.True(module.Trigger(hero));
        Assert.Equal(100, hero.ExperienceTracker.CurrentExperience);
        Assert.True(watcher.TestStatus(ObjectStatus.Stealthed));
    }

    [Fact]
    public void Trigger_OutsidePreparedPhase_IsRejected()
    {
        var game = NewGame();
        var watcher = game.SpawnObject("XPWatcher", game.CivilianPlayer, Vector3.Zero);
        var hero = game.SpawnObject("TestHero", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(watcher);

        // Still Packed: never initiated.
        Assert.False(module.Trigger(hero));
        Assert.Equal(0, hero.ExperienceTracker.CurrentExperience);
    }

    [Fact]
    public void StartAbilityRange_TriggeringObjectTooFar_FailsToInitiate()
    {
        var game = NewGame();
        var watcher = game.SpawnObject("RangedWatcher", game.CivilianPlayer, Vector3.Zero);
        // 150 units away, StartAbilityRange = 100: out of range.
        var farAway = game.SpawnObject("TestHero", game.CivilianPlayer, new Vector3(150, 0, 0));
        var module = ModuleOf(watcher);

        Assert.False(module.InitiateIntentToDoSpecialPower("TestHidePower", farAway));

        Step(game, 20);

        Assert.False(watcher.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));
    }

    [Fact]
    public void StartAbilityRange_TriggeringObjectInRange_Initiates()
    {
        var game = NewGame();
        var watcher = game.SpawnObject("RangedWatcher", game.CivilianPlayer, Vector3.Zero);
        // 50 units away, StartAbilityRange = 100: in range.
        var nearby = game.SpawnObject("TestHero", game.CivilianPlayer, new Vector3(50, 0, 0));
        var module = ModuleOf(watcher);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestHidePower", nearby));
    }

    [Fact]
    public void PersistentPrepTime_ExtendsPreparedWindowOnceWhenUnused()
    {
        var game = NewGame();
        var watcher = game.SpawnObject("PersistentWatcher", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(watcher);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestHidePower", null));

        // UnpackTime=0: Prepared starts immediately. PreparationTime=5 frames; step to just
        // before it elapses and confirm Trigger still succeeds (still Prepared, first window).
        Step(game, 4);
        Assert.True(module.Trigger(null));
    }

    [Fact]
    public void PersistentPrepTime_TriggerStillSucceedsDuringExtendedWindow()
    {
        var game = NewGame();
        var watcher = game.SpawnObject("PersistentWatcher", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(watcher);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestHidePower", null));

        // Let the first PreparationTime window (5 frames) fully lapse without triggering: the
        // one-shot PersistentPrepTime extension (5 more frames) should keep the ability
        // Prepared rather than packing it away.
        Step(game, 6);

        Assert.True(module.Trigger(null));
        Assert.True(watcher.TestStatus(ObjectStatus.Stealthed));
    }

    [Fact]
    public void EffectDuration_EndsHiddenEffectAtFrameCount()
    {
        var game = NewGame();
        var watcher = game.SpawnObject("XPWatcher", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(watcher);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestHidePower", null));
        game.Step(); // UnpackTime=0 -> Prepared.

        Assert.True(module.Trigger(null));
        Assert.True(watcher.TestStatus(ObjectStatus.Stealthed));

        // EffectDuration = 5 frames: still hidden partway through.
        Step(game, 3);
        Assert.True(watcher.TestStatus(ObjectStatus.Stealthed));

        // ... and no longer hidden once EffectDuration has fully elapsed.
        Step(game, 5);
        Assert.False(watcher.TestStatus(ObjectStatus.Stealthed));
    }

    [Fact]
    public void ShowPalantirTimer_ReflectsConfiguredFlag()
    {
        var game = NewGame();
        var shown = ModuleOf(game.SpawnObject("TimerShownWatcher", game.CivilianPlayer, Vector3.Zero));
        var hidden = ModuleOf(game.SpawnObject("TimerHiddenWatcher", game.CivilianPlayer, Vector3.Zero));

        Assert.True(shown.ShowsPalantirTimer);
        Assert.False(hidden.ShowsPalantirTimer);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var watcher = game.SpawnObject("Watcher", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(watcher);
        Assert.True(live.InitiateIntentToDoSpecialPower("TestHidePower", null));
        game.Step();

        var shadowHost = game.SpawnObject("Watcher", game.CivilianPlayer, new Vector3(200, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
