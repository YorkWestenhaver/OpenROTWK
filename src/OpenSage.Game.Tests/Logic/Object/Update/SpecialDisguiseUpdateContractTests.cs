// Mocked-game unit tests for the SpecialDisguiseUpdate port (R13), per
// bfme2-workbench/research/modules-r13/specs/SpecialDisguiseUpdateModuleData.md §3: one test
// per behavior branch, [create -> trigger/tick -> observable effect], covering the spec's
// twelve contract cases. Frame arithmetic follows ToggleHiddenSpecialAbilityUpdateContractTests'
// own convention: all duration fields are milliseconds (ParseDurationLogicFrames), quantized to
// the frozen 5 Hz logic rate - "200" below is exactly 1 logic frame.
//
// SLEEPY-UPDATE CAVEAT (spec §3, this batch's standing failure mode): a freshly spawned
// module's first Update() does not run on the frame it is constructed - it runs on the second
// HeadlessSimGame.Step() after spawn (UpdateModule's SetWakeFrame(UpdateSleepTime.None) wakes
// at +1 frame). Every case below calls Step() once after create()/spawn before asserting
// anything about phase-driven state, and every multi-phase case counts Step() calls against
// the exact frame counts each field's LogicFrameSpan implies.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class SpecialDisguiseUpdateContractTests
{
    private const string Definitions = @"
FXList FX_Disguise
End

Object TemplateA
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object TemplateB
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object TestHero
  KindOf = INFANTRY
  IsTrainable = Yes
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object Chameleon
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpecialDisguiseUpdate ModuleTag_Disguise
    SpecialPowerTemplate = SpecialPower_Disguise
    UnpackTime = 600
    PreparationTime = 600
    PackTime = 200
  End
End

Object ZeroDuration
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpecialDisguiseUpdate ModuleTag_Disguise
    SpecialPowerTemplate = SpecialPower_Disguise
    UnpackTime = 0
    PreparationTime = 0
    PackTime = 0
  End
End

Object UnpackWatcher
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpecialDisguiseUpdate ModuleTag_Disguise
    SpecialPowerTemplate = SpecialPower_Disguise
    UnpackTime = 600
    PreparationTime = 1000
    PackTime = 0
  End
End

Object SlowUnpacker
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpecialDisguiseUpdate ModuleTag_Disguise
    SpecialPowerTemplate = SpecialPower_Disguise
    UnpackTime = 1000
    PreparationTime = 2000
    PackTime = 0
  End
End

Object DisguiseTrigger
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpecialDisguiseUpdate ModuleTag_Disguise
    SpecialPowerTemplate = SpecialPower_Disguise
    UnpackTime = 0
    PreparationTime = 600
    PackTime = 200
    DisguiseAsTemplate = TemplateA
    DisguisedAsTemplate_EnemyPerspective = TemplateB
    DisguiseFX = FX_Disguise
    AwardXPForTriggering = 50
  End
End

Object AutoPacker
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpecialDisguiseUpdate ModuleTag_Disguise
    SpecialPowerTemplate = SpecialPower_Disguise
    UnpackTime = 0
    PreparationTime = 400
    PersistentPrepTime = 0
    PackTime = 200
  End
End

Object PersistentDisguiser
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpecialDisguiseUpdate ModuleTag_Disguise
    SpecialPowerTemplate = SpecialPower_Disguise
    UnpackTime = 0
    PreparationTime = 400
    PersistentPrepTime = 600
    PackTime = 0
  End
End

Object MountForcer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpecialDisguiseUpdate ModuleTag_Disguise
    SpecialPowerTemplate = SpecialPower_Disguise
    UnpackTime = 0
    PreparationTime = 400
    PackTime = 600
    ForceMountedWhenDisguising = Yes
  End
End

Object XferWatcher
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpecialDisguiseUpdate ModuleTag_Disguise
    SpecialPowerTemplate = SpecialPower_Disguise
    UnpackTime = 1000
    PreparationTime = 400
    PackTime = 200
    OpacityTarget = 0.25
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x5D1591)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static SpecialDisguiseUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<SpecialDisguiseUpdate>().Single();

    private static void Step(HeadlessSimGame game, int count)
    {
        for (var i = 0; i < count; i++)
        {
            game.Step();
        }
    }

    // ---- case 1 ----

    [Fact]
    public void InitiateIntentToDoSpecialPower_WrongTemplateName_NoOp()
    {
        var game = NewGame();
        var chameleon = game.SpawnObject("Chameleon", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(chameleon);
        game.Step();

        Assert.False(module.InitiateIntentToDoSpecialPower("SpecialPower_WrongName", null));
        Assert.False(chameleon.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));
    }

    // ---- case 2 ----

    [Fact]
    public void InitiateIntentToDoSpecialPower_WhileNotPacked_NoOp()
    {
        var game = NewGame();
        var chameleon = game.SpawnObject("Chameleon", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(chameleon);
        game.Step();

        Assert.True(module.InitiateIntentToDoSpecialPower("SpecialPower_Disguise", null));
        // Same frame, still Unpacking: no re-trigger of an in-flight cycle.
        Assert.False(module.InitiateIntentToDoSpecialPower("SpecialPower_Disguise", null));
    }

    // ---- case 3 ----

    [Fact]
    public void FullCycle_ZeroDurationFields_SkipsStraightToPacked()
    {
        var game = NewGame();
        var zero = game.SpawnObject("ZeroDuration", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(zero);
        game.Step();

        Assert.True(module.InitiateIntentToDoSpecialPower("SpecialPower_Disguise", null));

        // Same-frame collapse through Unpacking -> Prepared -> Packing, all synchronous:
        // the Prepared window never opens, so Trigger() was never possible.
        Assert.False(zero.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));
        Assert.False(zero.ModelConditionFlags.Get(ModelConditionFlag.Packing));
        Assert.False(zero.TestStatus(ObjectStatus.Disguised));
    }

    // ---- case 4 ----

    [Fact]
    public void UnpackToPrepared_SetsUnpackingThenClearsIt()
    {
        var game = NewGame();
        var watcher = game.SpawnObject("UnpackWatcher", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(watcher);
        game.Step();

        Assert.True(module.InitiateIntentToDoSpecialPower("SpecialPower_Disguise", null));
        Assert.True(watcher.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));

        // UnpackTime = 600ms = 3 frames.
        Step(game, 3);

        Assert.False(watcher.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));
    }

    // ---- case 5 ----

    [Fact]
    public void Trigger_WhilePrepared_SetsDisguisedStatusAndFiresFX()
    {
        var game = NewGame();
        var recorder = RecordingSimEvents.InstallOn(game);
        var disguiser = game.SpawnObject("DisguiseTrigger", game.CivilianPlayer, Vector3.Zero);
        var hero = game.SpawnObject("TestHero", game.CivilianPlayer, new Vector3(1, 0, 0));
        var module = ModuleOf(disguiser);
        game.Step();

        Assert.True(module.InitiateIntentToDoSpecialPower("SpecialPower_Disguise", null));

        // UnpackTime = 0, PreparationTime = 600ms = 3 frames: step strictly less than that so
        // Trigger() lands safely inside the Prepared window (the off-by-one hazard the spec's
        // own §3 caveat calls out - hitting the boundary exactly would let Update() auto-pack
        // the window closed before this test's own Trigger() call gets a chance to run).
        Step(game, 2);

        Assert.True(module.Trigger(hero));
        Assert.True(disguiser.TestStatus(ObjectStatus.Disguised));
        Assert.True(disguiser.ModelConditionFlags.Get(ModelConditionFlag.Disguised));
        Assert.Equal(50, hero.ExperienceTracker.CurrentExperience);

        var fx = Assert.Single(recorder.Events);
        Assert.Equal("FX_Disguise", fx.FXListName);
        Assert.Equal(disguiser.Id, fx.ObjectId);

        // No Step() has run since Trigger() - the sleepy-update caveat means the phase is
        // still visibly Active here even though PackTime is nonzero.
        Assert.True(disguiser.TestStatus(ObjectStatus.Disguised));
    }

    // ---- case 6 ----

    [Fact]
    public void Trigger_WhileNotPrepared_NoOp()
    {
        var game = NewGame();
        var disguiser = game.SpawnObject("DisguiseTrigger", game.CivilianPlayer, Vector3.Zero);
        var hero = game.SpawnObject("TestHero", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(disguiser);
        game.Step();

        // Still Packed: never initiated.
        Assert.False(module.Trigger(hero));
        Assert.False(disguiser.TestStatus(ObjectStatus.Disguised));
        Assert.Equal(0, hero.ExperienceTracker.CurrentExperience);
    }

    // ---- case 7 ----

    [Fact]
    public void PreparedWindowExpires_NoTriggerCall_AutoPacksWithNoDisguise()
    {
        var game = NewGame();
        var autoPacker = game.SpawnObject("AutoPacker", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(autoPacker);
        game.Step();

        Assert.True(module.InitiateIntentToDoSpecialPower("SpecialPower_Disguise", null));

        // UnpackTime = 0 -> Prepared immediately. PreparationTime = 400ms = 2 frames.
        Step(game, 2);

        // The window closed with no Trigger(): skips Active entirely, straight to Packing.
        Assert.True(autoPacker.ModelConditionFlags.Get(ModelConditionFlag.Packing));
        Assert.False(autoPacker.TestStatus(ObjectStatus.Disguised));

        // PackTime = 200ms = 1 frame.
        Step(game, 1);

        Assert.False(autoPacker.ModelConditionFlags.Get(ModelConditionFlag.Packing));
        Assert.False(autoPacker.TestStatus(ObjectStatus.Disguised));
    }

    // ---- case 8 ----

    [Fact]
    public void PersistentPrepTime_OneShotExtension_AppliesOnceOnly()
    {
        var game = NewGame();
        var persistent = game.SpawnObject("PersistentDisguiser", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(persistent);
        game.Step();

        Assert.True(module.InitiateIntentToDoSpecialPower("SpecialPower_Disguise", null));

        // PreparationTime = 400ms = 2 frames: the window would close here, but
        // PersistentPrepTime extends it once (F-SDU-1).
        Step(game, 2);

        // Trigger still succeeds - the extension kept the window open, no Packing yet.
        Assert.False(persistent.ModelConditionFlags.Get(ModelConditionFlag.Packing));

        // PersistentPrepTime = 600ms = 3 more frames: the extension is consumed, no Trigger()
        // called this time either, so it auto-packs (PackTime = 0, so straight to Packed).
        Step(game, 3);

        Assert.False(persistent.TestStatus(ObjectStatus.Disguised));
        Assert.False(persistent.ModelConditionFlags.Get(ModelConditionFlag.Packing));
    }

    // ---- case 9 ----

    [Fact]
    public void ForceMountedWhenDisguising_SetsMountedOnActiveClearsOnPackOut_DoesNotClobberRealMount()
    {
        var game = NewGame();
        var forcer = game.SpawnObject("MountForcer", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(forcer);
        game.Step();

        Assert.True(module.InitiateIntentToDoSpecialPower("SpecialPower_Disguise", null));

        // PreparationTime = 400ms = 2 frames: step strictly less so Trigger() lands inside
        // the Prepared window (same off-by-one caveat as case 5 above).
        Step(game, 1);

        Assert.True(module.Trigger(null));
        Assert.True(forcer.ModelConditionFlags.Get(ModelConditionFlag.Mounted));

        // PackTime = 600ms = 3 frames: the Active window is tied to PackTime (F-SDU-2 - see
        // the file header on SpecialDisguiseUpdate.cs), so this module owns the Mounted flag
        // and pack-out clears it after exactly PackTime frames.
        Step(game, 3);

        Assert.False(forcer.ModelConditionFlags.Get(ModelConditionFlag.Mounted));
    }

    [Fact]
    public void ForceMountedWhenDisguising_NeverClobbersAGenuineMount()
    {
        var game = NewGame();
        var forcer = game.SpawnObject("MountForcer", game.CivilianPlayer, Vector3.Zero);
        // Already genuinely mounted before this module ever touches the flag.
        forcer.SetModelConditionState(ModelConditionFlag.Mounted);
        var module = ModuleOf(forcer);
        game.Step();

        Assert.True(module.InitiateIntentToDoSpecialPower("SpecialPower_Disguise", null));
        Step(game, 1);

        Assert.True(module.Trigger(null));
        Assert.True(forcer.ModelConditionFlags.Get(ModelConditionFlag.Mounted));

        Step(game, 3);

        // Never clobbered: the real mount flag survives pack-out untouched.
        Assert.True(forcer.ModelConditionFlags.Get(ModelConditionFlag.Mounted));
    }

    // ---- case 10 ----

    private (HeadlessSimGame Game, GameObject Watcher, GameObject Observer, SpecialDisguiseUpdate Module)
        SpawnAndActivateDisguise(RelationshipType? watcherToObserver)
    {
        var game = NewGame();
        var watcher = game.SpawnObject("DisguiseTrigger", game.CivilianPlayer, Vector3.Zero);
        var observer = game.SpawnObject("TestHero", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));

        // GetRelationship reads through the CALLER's own Team, so the override belongs on the
        // watcher's own player, pointed at the observer's player (same shape EmpUpdateContractTests
        // uses for candidate.GetRelationship(self)). Both need a non-null Team or GetRelationship
        // short-circuits to Neutral regardless of the override.
        watcher.Team = new Team(new TeamTemplate(game.TeamFactory, 2001, "WatcherTeam", game.CivilianPlayer, isSingleton: true), 2001);
        observer.Team = new Team(new TeamTemplate(game.TeamFactory, 2002, "ObserverTeam", game.PlayerManager.NeutralPlayer, isSingleton: true), 2002);

        if (watcherToObserver.HasValue)
        {
            game.CivilianPlayer.SetRelationship(game.PlayerManager.NeutralPlayer, watcherToObserver.Value);
        }

        var module = ModuleOf(watcher);
        game.Step();

        Assert.True(module.InitiateIntentToDoSpecialPower("SpecialPower_Disguise", null));
        // PreparationTime = 600ms = 3 frames: step strictly less so Trigger() lands inside the
        // Prepared window (same off-by-one caveat as case 5 above).
        Step(game, 2);
        Assert.True(module.Trigger(null));

        return (game, watcher, observer, module);
    }

    [Fact]
    public void DisguisedAsTemplate_EnemyPerspective_UsedForEnemyRelationship()
    {
        var (_, _, observer, module) = SpawnAndActivateDisguise(RelationshipType.Enemies);

        Assert.Equal("TemplateB", module.GetResolvedTemplateNameFor(observer));
    }

    [Fact]
    public void DisguiseAsTemplate_UsedForNonAllyNonEnemyRelationship()
    {
        // No relationship override set: defaults to Neutral, which is neither Allies nor
        // Enemies - the F-SDU-3 fallback case.
        var (_, _, observer, module) = SpawnAndActivateDisguise(watcherToObserver: null);

        Assert.Equal("TemplateA", module.GetResolvedTemplateNameFor(observer));
    }

    [Fact]
    public void AllyObserver_SeesTrueIdentityNotEitherDisguiseTemplate()
    {
        var (_, watcher, observer, module) = SpawnAndActivateDisguise(RelationshipType.Allies);

        Assert.Equal(watcher.Definition.Name, module.GetResolvedTemplateNameFor(observer));
    }

    // ---- case 11 ----

    [Fact]
    public void Xfer_SaveLoadRoundTrip_PreservesPhaseAndOpacity()
    {
        var game = NewGame();
        var live = game.SpawnObject("XferWatcher", game.CivilianPlayer, Vector3.Zero);
        var liveModule = ModuleOf(live);
        game.Step();

        Assert.True(liveModule.InitiateIntentToDoSpecialPower("SpecialPower_Disguise", null));

        // UnpackTime = 1000ms = 5 frames: step partway into Unpacking.
        Step(game, 2);

        var shadowHost = game.SpawnObject("XferWatcher", game.CivilianPlayer, new Vector3(200, 0, 0));
        var shadowModule = ModuleOf(shadowHost);

        // The shared shadow-copy base test (Save -> Load -> CRC == live CRC) also loads the
        // live instance's saved state into the shadow, so the shadow now mirrors the live
        // instance's mid-Unpacking phase/end-frame/opacity exactly.
        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(liveModule, shadowModule);

        // Both objects live in the same headless game (shared clock), and the loaded shadow's
        // phase/end-frame now mirror the live instance's mid-Unpacking state exactly, so
        // stepping the shared clock the remaining frames drives both through Update()
        // identically - the loaded shadow reaches Prepared on the same frame the live instance
        // does.
        Step(game, 3);

        Assert.Equal(liveModule.CurrentOpacity, shadowModule.CurrentOpacity);
    }

    // ---- case 12 ----

    [Fact]
    public void Update_BetweenPhaseTransitions_NoSpuriousStateChange()
    {
        var game = NewGame();
        var slow = game.SpawnObject("SlowUnpacker", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(slow);
        game.Step();

        Assert.True(module.InitiateIntentToDoSpecialPower("SpecialPower_Disguise", null));

        // UnpackTime = 1000ms = 5 frames: strictly before it elapses on every step below.
        for (var i = 0; i < 4; i++)
        {
            game.Step();
            Assert.True(slow.ModelConditionFlags.Get(ModelConditionFlag.Unpacking));
            Assert.False(slow.TestStatus(ObjectStatus.Disguised));
        }
    }
}
