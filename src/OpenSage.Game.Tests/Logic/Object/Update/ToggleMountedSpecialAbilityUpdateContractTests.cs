// Mocked-game unit tests for the ToggleMountedSpecialAbilityUpdate port (R14, spec
// bfme2-workbench/research/modules-r13/specs/ToggleMountedSpecialAbilityUpdateModuleData.md §4):
// one test per behavior branch from the spec's own test plan, [create -> trigger/tick ->
// observable effect]. Pattern copied from ReplaceObjectUpdateContractTests.cs.
//
// The trigger is a driven input (no landed special-power/command system calls it yet - see the
// file header on ToggleMountedSpecialAbilityUpdate.cs): tests call InitiateIntentToDoSpecialPower
// / Trigger directly.
//
// Frame arithmetic caveat (spec §4, "this batch's standing failure mode"): ParseDurationLogicFrames
// reads milliseconds against the frozen 5 Hz logic rate - "1000" below is exactly 5 logic frames -
// and a freshly created module's first Update() lands on the SECOND Step() after spawn
// (UpdateSleepTime.None). Tests step until an observable, never assert an exact frame count off a
// hand-computed spawn frame.

using System.Linq;
using System.Numerics;
using OpenSage;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class ToggleMountedSpecialAbilityUpdateContractTests
{
    private const string Definitions = @"
Object FootHero
  KindOf = INFANTRY
  IsTrainable = Yes
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ToggleMountedSpecialAbilityUpdate ModuleTag_Mount
    SpecialPowerTemplate = TestToggleMounted
    MountedTemplate      = MountedHero
    UnpackTime           = 1000
    PreparationTime      = 1000
    PackTime             = 1000
    AwardXPForTriggering = 50
    OpacityTarget        = .3
    IgnoreFacingCheck    = Yes
    CancelDisguiseWhenDismounting = Yes
    SynchronizeTimerOnSpecialPower = TestOtherPowerA TestOtherPowerB
  End
End

Object MountedHero
  KindOf = CAVALRY
  IsTrainable = Yes
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 400
  End
  Behavior = ToggleMountedSpecialAbilityUpdate ModuleTag_Dismount
    SpecialPowerTemplate = TestToggleDismount
    MountedTemplate      = FootHero
    UnpackTime           = 0
    PreparationTime      = 1000
    PackTime             = 0
  End
End

Object RangedFootHero
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ToggleMountedSpecialAbilityUpdate ModuleTag_Mount
    SpecialPowerTemplate = TestToggleMounted
    MountedTemplate      = MountedHero
    StartAbilityRange    = 100
    PreparationTime      = 1000
    AwardXPForTriggering = 50
  End
End

Object NoTemplateHero
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ToggleMountedSpecialAbilityUpdate ModuleTag_Mount
    SpecialPowerTemplate = TestToggleMounted
    PreparationTime      = 1000
  End
End

Object InstantMountHero
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ToggleMountedSpecialAbilityUpdate ModuleTag_Mount
    SpecialPowerTemplate     = TestToggleMounted
    MountedTemplate          = MountedHero
    TriggerInstantlyOnCreate = Yes
    UnpackTime               = 1000
    PreparationTime          = 1000
    PackTime                 = 0
  End
End

Object PersistentPrepHero
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ToggleMountedSpecialAbilityUpdate ModuleTag_Mount
    SpecialPowerTemplate = TestToggleMounted
    MountedTemplate      = MountedHero
    PreparationTime      = 1000
    PersistentPrepTime   = 1000
    PackTime             = 0
  End
End

Object TestRider
  KindOf = INFANTRY
  IsTrainable = Yes
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x704E5)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static ToggleMountedSpecialAbilityUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<ToggleMountedSpecialAbilityUpdate>().Single();

    private static uint NextTestTeamId = 900;

    private static Team AssignSingletonTeam(HeadlessSimGame game, GameObject obj, Player owner)
    {
        var id = NextTestTeamId++;
        var template = new TeamTemplate(game.TeamFactory, id, $"ToggleMountedTestTeam{id}", owner, isSingleton: true);
        var team = new Team(template, id);
        obj.Team = team;
        return team;
    }

    private static void StepUntilDestroyed(HeadlessSimGame game, GameObject obj, int maxSteps = 50)
    {
        for (var i = 0; i < maxSteps && !obj.IsDestroyed; i++)
        {
            game.Step();
        }

        Assert.True(obj.IsDestroyed, "object was never swapped within the step budget");
    }

    // Case 1: Trigger_AfterUnpackAndPrep_ReplacesSelfWithMountedTemplate
    [Fact]
    public void Trigger_AfterUnpackAndPrep_ReplacesSelfWithMountedTemplate()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        var foot = game.SpawnObject("FootHero", owner, new Vector3(10, 20, 0));
        var team = AssignSingletonTeam(game, foot, owner);
        var module = ModuleOf(foot);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestToggleMounted", null));

        for (var i = 0; i < 50 && module.Phase != ToggleMountedSpecialAbilityUpdate.ToggleMountedPhase.Prepared && !foot.IsDestroyed; i++)
        {
            game.Step();
        }
        Assert.False(foot.IsDestroyed);
        Assert.Equal(ToggleMountedSpecialAbilityUpdate.ToggleMountedPhase.Prepared, module.Phase);

        Assert.True(module.Trigger(null));
        StepUntilDestroyed(game, foot);

        var replacement = game.GameLogic.Objects.Single(o => o.Definition.Name == "MountedHero");
        Assert.Equal(owner, replacement.Owner);
        Assert.Equal(team, replacement.Team);
        Assert.Equal(10.0f, replacement.Translation.X, 2);
        Assert.Equal(20.0f, replacement.Translation.Y, 2);
    }

    // Case 2: Trigger_CreatesFreshInstance_NoHealthOrVeterancyCarryOver
    [Fact]
    public void Trigger_CreatesFreshInstance_NoHealthOrVeterancyCarryOver()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        var foot = game.SpawnObject("FootHero", owner, Vector3.Zero);
        AssignSingletonTeam(game, foot, owner);
        var module = ModuleOf(foot);

        foot.BodyModule.InternalChangeHealth(-50f);
        Assert.Equal(50f, foot.BodyModule.Health, 1);

        foot.ExperienceTracker.AddExperiencePoints(100000);
        var footExperience = foot.ExperienceTracker.CurrentExperience;
        Assert.True(footExperience > 0, "test setup expected the donor to gain experience");

        Assert.True(module.InitiateIntentToDoSpecialPower("TestToggleMounted", null));
        for (var i = 0; i < 50 && module.Phase != ToggleMountedSpecialAbilityUpdate.ToggleMountedPhase.Prepared && !foot.IsDestroyed; i++)
        {
            game.Step();
        }
        Assert.True(module.Trigger(null));
        StepUntilDestroyed(game, foot);

        var replacement = game.GameLogic.Objects.Single(o => o.Definition.Name == "MountedHero");
        Assert.Equal(400f, replacement.BodyModule.Health, 1);
        Assert.Equal(400f, replacement.BodyModule.MaxHealth, 1);
        // Not 0: every GameObject carries an automatic ExperienceUpdate (GameObject.cs's
        // ModuleTag_ExperienceHelper, pre-existing/unrelated to this port) that raises a
        // still-zero CurrentExperience to a rank-1 floor of 1 on its own first Update() tick -
        // see ShareExperienceBehavior.cs's own citation of this same behavior. The replacement
        // ticks at least once before StepUntilDestroyed returns, so this floor is the true
        // "fresh instance" baseline, not a veterancy carry-over from the donor's 100000 XP.
        Assert.Equal(1, replacement.ExperienceTracker.CurrentExperience);
        Assert.Equal(VeterancyLevel.Regular, replacement.ExperienceTracker.VeterancyLevel);
    }

    // Case 3: WrongSpecialPowerTemplate_DoesNotTrigger
    [Fact]
    public void WrongSpecialPowerTemplate_DoesNotTrigger()
    {
        var game = NewGame();
        var foot = game.SpawnObject("FootHero", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(foot);

        Assert.False(module.InitiateIntentToDoSpecialPower("SomeOtherPower", null));

        for (var i = 0; i < 20; i++)
        {
            game.Step();
        }

        Assert.False(foot.IsDestroyed);
        Assert.DoesNotContain(game.GameLogic.Objects, o => o.Definition.Name == "MountedHero");
    }

    // Case 4: ReTrigger_WhileInProgress_IsRejected
    [Fact]
    public void ReTrigger_WhileInProgress_IsRejected()
    {
        var game = NewGame();
        var foot = game.SpawnObject("FootHero", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(foot);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestToggleMounted", null));
        Assert.False(module.InitiateIntentToDoSpecialPower("TestToggleMounted", null));
    }

    // Case 5: Trigger_OutsidePreparedWindow_IsNoOp
    [Fact]
    public void Trigger_OutsidePreparedWindow_IsNoOp()
    {
        var game = NewGame();
        var foot = game.SpawnObject("FootHero", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(foot);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestToggleMounted", null));
        // Still Unpacking - the Prepared window has not opened yet.
        Assert.False(module.Trigger(null));

        for (var i = 0; i < 20; i++)
        {
            game.Step();
        }

        Assert.DoesNotContain(game.GameLogic.Objects, o => o.Definition.Name == "MountedHero");
        Assert.False(foot.IsDestroyed);
    }

    // Case 6: PreparedWindowExpires_WithNoTrigger_PacksDownAndNoSwap
    [Fact]
    public void PreparedWindowExpires_WithNoTrigger_PacksDownAndNoSwap()
    {
        var game = NewGame();
        var foot = game.SpawnObject("FootHero", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(foot);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestToggleMounted", null));

        for (var i = 0; i < 40; i++)
        {
            game.Step();
        }

        Assert.False(foot.IsDestroyed);
        Assert.DoesNotContain(game.GameLogic.Objects, o => o.Definition.Name == "MountedHero");
        Assert.Equal(ToggleMountedSpecialAbilityUpdate.ToggleMountedPhase.Packed, module.Phase);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestToggleMounted", null));
    }

    // Case 7: PersistentPrepTime_ExtendsPreparedWindowExactlyOnce (guards F-TMS-1)
    [Fact]
    public void PersistentPrepTime_ExtendsPreparedWindowExactlyOnce()
    {
        var game = NewGame();
        var hero = game.SpawnObject("PersistentPrepHero", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(hero);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestToggleMounted", null));

        for (var i = 0; i < 50 && module.Phase != ToggleMountedSpecialAbilityUpdate.ToggleMountedPhase.Prepared; i++)
        {
            game.Step();
        }
        Assert.Equal(ToggleMountedSpecialAbilityUpdate.ToggleMountedPhase.Prepared, module.Phase);

        // PreparationTime = 1000ms = 5 frames. Step past it: the one-shot extension should
        // fire and keep the module in Prepared.
        for (var i = 0; i < 7; i++)
        {
            game.Step();
        }
        Assert.Equal(ToggleMountedSpecialAbilityUpdate.ToggleMountedPhase.Prepared, module.Phase);
        Assert.False(hero.IsDestroyed);

        // Step past the extension (another PersistentPrepTime = 5 frames) with no second
        // extension available: back to Packed, no swap (PackTime = 0).
        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }
        Assert.Equal(ToggleMountedSpecialAbilityUpdate.ToggleMountedPhase.Packed, module.Phase);
        Assert.False(hero.IsDestroyed);
    }

    // Case 8: StartAbilityRange_OutOfRangeTriggeringObject_Rejected / accepted when near
    [Fact]
    public void StartAbilityRange_OutOfRangeTriggeringObject_RejectedThenAcceptedWhenNear()
    {
        var game = NewGame();
        var foot = game.SpawnObject("RangedFootHero", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(foot);
        var farRider = game.SpawnObject("TestRider", game.CivilianPlayer, new Vector3(500, 0, 0));

        Assert.False(module.InitiateIntentToDoSpecialPower("TestToggleMounted", farRider));

        var nearRider = game.SpawnObject("TestRider", game.CivilianPlayer, new Vector3(10, 0, 0));
        Assert.True(module.InitiateIntentToDoSpecialPower("TestToggleMounted", nearRider));
    }

    // Case 9: AwardXPForTriggering_CreditedToTriggeringObject_AtSwapNotAtRequest
    [Fact]
    public void AwardXPForTriggering_CreditedToTriggeringObject_AtSwapNotAtRequest()
    {
        var game = NewGame();
        var foot = game.SpawnObject("FootHero", game.CivilianPlayer, Vector3.Zero);
        var rider = game.SpawnObject("TestRider", game.CivilianPlayer, new Vector3(1, 0, 0));
        var module = ModuleOf(foot);

        // Every GameObject carries an automatic ExperienceUpdate (GameObject.cs's
        // ModuleTag_ExperienceHelper, pre-existing/unrelated to this port) that raises a
        // still-zero CurrentExperience to a rank-1 floor of 1 on its own first Update() tick -
        // see ShareExperienceBehavior.cs's own citation of this same behavior. Step once first
        // so that floor has already settled before capturing the "before" baseline, isolating
        // this module's own AwardXPForTriggering grant in the assertions below.
        game.Step();
        var experienceBefore = rider.ExperienceTracker.CurrentExperience;

        Assert.True(module.InitiateIntentToDoSpecialPower("TestToggleMounted", rider));

        for (var i = 0; i < 50 && module.Phase != ToggleMountedSpecialAbilityUpdate.ToggleMountedPhase.Prepared; i++)
        {
            game.Step();
        }
        Assert.Equal(ToggleMountedSpecialAbilityUpdate.ToggleMountedPhase.Prepared, module.Phase);

        // Not credited at request time.
        Assert.Equal(experienceBefore, rider.ExperienceTracker.CurrentExperience);

        Assert.True(module.Trigger(rider));
        StepUntilDestroyed(game, foot);

        Assert.Equal(experienceBefore + 50, rider.ExperienceTracker.CurrentExperience);
    }

    // Case 10: NoMountedTemplate_IsSilentNoOp
    [Fact]
    public void NoMountedTemplate_IsSilentNoOp()
    {
        var game = NewGame();
        var hero = game.SpawnObject("NoTemplateHero", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(hero);
        var objectCountBefore = game.GameLogic.Objects.Count();

        Assert.True(module.InitiateIntentToDoSpecialPower("TestToggleMounted", null));

        for (var i = 0; i < 50 && module.Phase != ToggleMountedSpecialAbilityUpdate.ToggleMountedPhase.Prepared; i++)
        {
            game.Step();
        }
        Assert.Equal(ToggleMountedSpecialAbilityUpdate.ToggleMountedPhase.Prepared, module.Phase);

        Assert.True(module.Trigger(null));

        for (var i = 0; i < 20; i++)
        {
            game.Step();
        }

        Assert.False(hero.IsDestroyed);
        Assert.Equal(objectCountBefore, game.GameLogic.Objects.Count());
        Assert.Equal(ToggleMountedSpecialAbilityUpdate.ToggleMountedPhase.Swapped, module.Phase);
    }

    // Case 11: TriggerInstantlyOnCreate_MountsWithNoExternalCall (+ negative control)
    [Fact]
    public void TriggerInstantlyOnCreate_MountsWithNoExternalCall()
    {
        var game = NewGame();
        var hero = game.SpawnObject("InstantMountHero", game.CivilianPlayer, new Vector3(7, 9, 0));

        for (var i = 0; i < 50 && !hero.IsDestroyed; i++)
        {
            game.Step();
        }

        Assert.True(hero.IsDestroyed);
        var replacement = game.GameLogic.Objects.Single(o => o.Definition.Name == "MountedHero");
        Assert.Equal(7.0f, replacement.Translation.X, 2);
        Assert.Equal(9.0f, replacement.Translation.Y, 2);

        // Negative control: TriggerInstantlyOnCreate absent (defaults to No) does nothing on
        // its own.
        var passiveHero = game.SpawnObject("FootHero", game.CivilianPlayer, new Vector3(50, 50, 0));
        for (var i = 0; i < 40; i++)
        {
            game.Step();
        }
        Assert.False(passiveHero.IsDestroyed);
    }

    // Case 12: TriggerInstantlyOnCreate_ReplacementDoesNotImmediatelySwapBack
    [Fact]
    public void TriggerInstantlyOnCreate_ReplacementDoesNotImmediatelySwapBack()
    {
        var game = NewGame();
        var hero = game.SpawnObject("InstantMountHero", game.CivilianPlayer, Vector3.Zero);

        for (var i = 0; i < 50 && !hero.IsDestroyed; i++)
        {
            game.Step();
        }
        Assert.True(hero.IsDestroyed);
        var mounted = game.GameLogic.Objects.Single(o => o.Definition.Name == "MountedHero");

        for (var i = 0; i < 40; i++)
        {
            game.Step();
        }

        Assert.False(mounted.IsDestroyed);
        Assert.Single(game.GameLogic.Objects, o => o.Definition.Name == "MountedHero");
        Assert.DoesNotContain(game.GameLogic.Objects, o => o.Definition.Name == "FootHero");
    }

    // Case 13: DismountHalf_SwapsBackViaItsOwnModule
    [Fact]
    public void DismountHalf_SwapsBackViaItsOwnModule()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        var foot = game.SpawnObject("FootHero", owner, Vector3.Zero);
        AssignSingletonTeam(game, foot, owner);
        var mountModule = ModuleOf(foot);

        Assert.True(mountModule.InitiateIntentToDoSpecialPower("TestToggleMounted", null));
        for (var i = 0; i < 50 && mountModule.Phase != ToggleMountedSpecialAbilityUpdate.ToggleMountedPhase.Prepared; i++)
        {
            game.Step();
        }
        Assert.True(mountModule.Trigger(null));
        StepUntilDestroyed(game, foot);

        var mounted = game.GameLogic.Objects.Single(o => o.Definition.Name == "MountedHero");
        var dismountModule = ModuleOf(mounted);

        Assert.True(dismountModule.InitiateIntentToDoSpecialPower("TestToggleDismount", null));
        for (var i = 0; i < 50 && dismountModule.Phase != ToggleMountedSpecialAbilityUpdate.ToggleMountedPhase.Prepared && !mounted.IsDestroyed; i++)
        {
            game.Step();
        }
        Assert.True(dismountModule.Trigger(null));
        StepUntilDestroyed(game, mounted);

        var backOnFoot = game.GameLogic.Objects.Single(o => o.Definition.Name == "FootHero");
        Assert.True(mounted.IsDestroyed);
        Assert.False(backOnFoot.IsDestroyed);
    }

    // Case 14: HeldFields_ParsedButNotConsumed
    [Fact]
    public void HeldFields_ParsedButNotConsumed()
    {
        var game = NewGame();
        var footDefinition = game.AssetStore.ObjectDefinitions.GetByName("FootHero");
        var moduleData = footDefinition.Behaviors["ModuleTag_Mount"].Data as ToggleMountedSpecialAbilityUpdateModuleData;

        Assert.NotNull(moduleData);
        Assert.True(moduleData.IgnoreFacingCheck);
        Assert.True(moduleData.CancelDisguiseWhenDismounting);
        Assert.Equal(new[] { "TestOtherPowerA", "TestOtherPowerB" }, moduleData.SynchronizeTimerOnSpecialPower);

        // The held fields change nothing observable: the full sequence still runs identically.
        var owner = game.CivilianPlayer;
        var foot = game.SpawnObject("FootHero", owner, new Vector3(10, 20, 0));
        var team = AssignSingletonTeam(game, foot, owner);
        var module = ModuleOf(foot);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestToggleMounted", null));
        for (var i = 0; i < 50 && module.Phase != ToggleMountedSpecialAbilityUpdate.ToggleMountedPhase.Prepared && !foot.IsDestroyed; i++)
        {
            game.Step();
        }
        Assert.True(module.Trigger(null));
        StepUntilDestroyed(game, foot);

        var replacement = game.GameLogic.Objects.Single(o => o.Definition.Name == "MountedHero");
        Assert.Equal(owner, replacement.Owner);
        Assert.Equal(team, replacement.Team);
        Assert.Equal(10.0f, replacement.Translation.X, 2);
        Assert.Equal(20.0f, replacement.Translation.Y, 2);
    }

    // Case 15: Xfer_SaveLoadRoundTrip_MidPreparedWindow
    [Fact]
    public void Xfer_SaveLoadRoundTrip_MidPreparedWindow()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        var live = game.SpawnObject("FootHero", owner, Vector3.Zero);
        AssignSingletonTeam(game, live, owner);
        var liveModule = ModuleOf(live);

        Assert.True(liveModule.InitiateIntentToDoSpecialPower("TestToggleMounted", null));
        for (var i = 0; i < 50 && liveModule.Phase != ToggleMountedSpecialAbilityUpdate.ToggleMountedPhase.Prepared; i++)
        {
            game.Step();
        }
        Assert.Equal(ToggleMountedSpecialAbilityUpdate.ToggleMountedPhase.Prepared, liveModule.Phase);

        var shadowHost = game.SpawnObject("FootHero", owner, new Vector3(200, 0, 0));
        var shadowModule = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(liveModule, shadowModule);

        // Continuing to step the loaded (shadow) instance reaches its own swap - proving the
        // full mutable-state inventory (phase/timer/prep-extended/triggering-id/auto-armed)
        // survived the round trip, not just the CRC snapshot.
        Assert.True(shadowModule.Trigger(null));
        StepUntilDestroyed(game, shadowHost);

        var shadowReplacement = game.GameLogic.Objects.Single(o =>
            o.Definition.Name == "MountedHero" && o.Translation.X > 100f);
        Assert.Equal(200.0f, shadowReplacement.Translation.X, 2);
    }
}
