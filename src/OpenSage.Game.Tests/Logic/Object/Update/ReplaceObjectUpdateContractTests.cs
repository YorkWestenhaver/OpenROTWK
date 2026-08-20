// Mocked-game unit tests for the ReplaceObjectUpdate port (api-freeze-v1 §6 fitness item 4):
// one test per behavior branch, [create -> trigger/tick -> observable effect], covering the
// R12 task packet's testCases.
//
// The trigger is a driven input (see the file header on ReplaceObjectUpdate.cs, mirroring
// MissileLauncherBuildingUpdate's own InitiateIntentToDoSpecialPower seam): tests call it
// directly instead of standing up a special-power/command system.
//
// Frame arithmetic: PreparationTime/UnpackTime are milliseconds (ParseDurationLogicFrames,
// the SAGE INI convention), quantized to the frozen 5 Hz logic rate - "1000" below is exactly
// 5 logic frames.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class ReplaceObjectUpdateContractTests
{
    private const string Definitions = @"
Object Widget
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ReplaceObjectUpdate ModuleTag_Replace
    SpecialPowerTemplate = TestReplacePower
    PreparationTime = 1000
    UnpackTime = 1000
    ReplaceObject
      TargetObjectFilter = ALL
      ReplacementObjectName = ReplacedWidget
    End
  End
End

Object ScatterWidget
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ReplaceObjectUpdate ModuleTag_Replace
    SpecialPowerTemplate = TestReplacePower
    Scatter = Yes
    ReplaceRadius = 50
    ReplaceObject
      TargetObjectFilter = ALL
      ReplacementObjectName = ReplacedWidget
    End
  End
End

Object FilteredWidget
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ReplaceObjectUpdate ModuleTag_Replace
    SpecialPowerTemplate = TestReplacePower
    ReplaceObject
      TargetObjectFilter = NONE +VEHICLE
      ReplacementObjectName = ReplacedWidget
    End
  End
End

Object RangedWidget
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ReplaceObjectUpdate ModuleTag_Replace
    SpecialPowerTemplate = TestReplacePower
    StartAbilityRange = 100
    ReplaceObject
      TargetObjectFilter = ALL
      ReplacementObjectName = ReplacedWidget
    End
  End
End

Object XPWidget
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ReplaceObjectUpdate ModuleTag_Replace
    SpecialPowerTemplate = TestReplacePower
    AwardXPForTriggering = 100
    ReplaceObject
      TargetObjectFilter = ALL
      ReplacementObjectName = ReplacedWidget
    End
  End
End

Object ReplacedWidget
  KindOf = STRUCTURE
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
";

    private static HeadlessSimGame NewGame(uint seed = 0xB4E2)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static ReplaceObjectUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<ReplaceObjectUpdate>().Single();

    private static uint _nextTestTeamId = 700;

    private static Team AssignSingletonTeam(HeadlessSimGame game, GameObject obj, Player owner)
    {
        var id = _nextTestTeamId++;
        var template = new TeamTemplate(game.TeamFactory, id, $"ReplaceTestTeam{id}", owner, isSingleton: true);
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

        Assert.True(obj.IsDestroyed, "object was never replaced within the step budget");
    }

    [Fact]
    public void Trigger_ProgressesThroughPreparationAndUnpack_ThenReplacesAtOriginalLocation()
    {
        var game = NewGame();
        var owner = game.CivilianPlayer;
        var widget = game.SpawnObject("Widget", owner, new Vector3(10, 20, 0));
        var team = AssignSingletonTeam(game, widget, owner);
        var module = ModuleOf(widget);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestReplacePower", null));

        // Neither phase has had time to expire yet.
        game.Step();
        Assert.False(widget.IsDestroyed);

        StepUntilDestroyed(game, widget);

        var replacement = game.GameLogic.Objects.Single(o => o.Definition.Name == "ReplacedWidget");
        Assert.Equal(owner, replacement.Owner);
        Assert.Equal(team, replacement.Team);
        Assert.Equal(10.0f, replacement.Translation.X, 2);
        Assert.Equal(20.0f, replacement.Translation.Y, 2);
    }

    [Fact]
    public void WrongSpecialPowerTemplate_DoesNotTrigger()
    {
        var game = NewGame();
        var widget = game.SpawnObject("Widget", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(widget);

        Assert.False(module.InitiateIntentToDoSpecialPower("SomeOtherPower", null));

        for (var i = 0; i < 20; i++)
        {
            game.Step();
        }

        Assert.False(widget.IsDestroyed);
    }

    [Fact]
    public void ReTrigger_WhileInProgress_IsRejected()
    {
        var game = NewGame();
        var widget = game.SpawnObject("Widget", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(widget);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestReplacePower", null));
        Assert.False(module.InitiateIntentToDoSpecialPower("TestReplacePower", null));
    }

    [Fact]
    public void Scatter_WithReplaceRadius_PlacesReplacementWithinRadius()
    {
        var game = NewGame();
        var origin = new Vector3(100, 100, 0);
        var widget = game.SpawnObject("ScatterWidget", game.CivilianPlayer, origin);
        var module = ModuleOf(widget);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestReplacePower", null));
        StepUntilDestroyed(game, widget);

        var replacement = game.GameLogic.Objects.Single(o => o.Definition.Name == "ReplacedWidget");
        var distance = Vector3.Distance(
            new Vector3(replacement.Translation.X, replacement.Translation.Y, 0),
            new Vector3(origin.X, origin.Y, 0));

        Assert.True(distance <= 50.01f, $"scattered {distance} units away, expected <= 50");
    }

    [Fact]
    public void ScatterDisabled_ReplacementExactlyAtOriginalTransform()
    {
        var game = NewGame();
        var origin = new Vector3(5, 5, 0);
        var widget = game.SpawnObject("Widget", game.CivilianPlayer, origin);
        var module = ModuleOf(widget);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestReplacePower", null));
        StepUntilDestroyed(game, widget);

        var replacement = game.GameLogic.Objects.Single(o => o.Definition.Name == "ReplacedWidget");
        Assert.Equal(origin.X, replacement.Translation.X, 3);
        Assert.Equal(origin.Y, replacement.Translation.Y, 3);
    }

    [Fact]
    public void TargetObjectFilter_DoesNotMatchSelf_NoReplacementOccurs()
    {
        // FilteredWidget's TargetObjectFilter only accepts VEHICLE; the object itself is a
        // STRUCTURE, so the filter never matches and no replacement is configured.
        var game = NewGame();
        var widget = game.SpawnObject("FilteredWidget", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(widget);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestReplacePower", null));

        for (var i = 0; i < 20; i++)
        {
            game.Step();
        }

        Assert.False(widget.IsDestroyed);
        Assert.DoesNotContain(game.GameLogic.Objects, o => o.Definition.Name == "ReplacedWidget");
    }

    [Fact]
    public void PostReplacement_ReplacementIsPathfindGridVisibleAndQueueable()
    {
        var game = NewGame();
        var widget = game.SpawnObject("Widget", game.CivilianPlayer, new Vector3(30, 40, 0));
        var module = ModuleOf(widget);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestReplacePower", null));
        StepUntilDestroyed(game, widget);

        var replacement = game.GameLogic.Objects.Single(o => o.Definition.Name == "ReplacedWidget");

        // The grid knows about the replacement's cell (it is world-visible, not off the
        // deterministic pathfind grid), and it can be queued for a path exactly like any
        // other live object - the same observable ReplaceObjectUpdate itself drives via
        // Context.GameLogic.PathfindQueueForPath post-creation.
        Assert.True(game.GameLogic.SimPathfind.Grid.WorldToCell(
            new FixVector3(
                new Fix64((int)replacement.Translation.X),
                new Fix64((int)replacement.Translation.Y),
                Fix64.Zero),
            out _, out _));
        Assert.True(game.GameLogic.SimPathfind.QueueForPath(replacement.Id));
    }

    [Fact]
    public void AwardXPForTriggering_CreditsTriggeringObject()
    {
        var game = NewGame();
        var widget = game.SpawnObject("XPWidget", game.CivilianPlayer, Vector3.Zero);
        var hero = game.SpawnObject("TestHero", game.CivilianPlayer, new Vector3(1, 0, 0));
        var module = ModuleOf(widget);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestReplacePower", hero));
        StepUntilDestroyed(game, widget);

        Assert.Equal(100, hero.ExperienceTracker.CurrentExperience);
    }

    [Fact]
    public void StartAbilityRange_TriggeringObjectTooFar_FailsToTrigger()
    {
        var game = NewGame();
        var widget = game.SpawnObject("RangedWidget", game.CivilianPlayer, Vector3.Zero);
        // 150 units away, StartAbilityRange = 100: out of range.
        var farAway = game.SpawnObject("TestHero", game.CivilianPlayer, new Vector3(150, 0, 0));
        var module = ModuleOf(widget);

        Assert.False(module.InitiateIntentToDoSpecialPower("TestReplacePower", farAway));

        for (var i = 0; i < 20; i++)
        {
            game.Step();
        }

        Assert.False(widget.IsDestroyed);
    }

    [Fact]
    public void StartAbilityRange_TriggeringObjectInRange_Triggers()
    {
        var game = NewGame();
        var widget = game.SpawnObject("RangedWidget", game.CivilianPlayer, Vector3.Zero);
        // 50 units away, StartAbilityRange = 100: in range.
        var nearby = game.SpawnObject("TestHero", game.CivilianPlayer, new Vector3(50, 0, 0));
        var module = ModuleOf(widget);

        Assert.True(module.InitiateIntentToDoSpecialPower("TestReplacePower", nearby));
        StepUntilDestroyed(game, widget);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var widget = game.SpawnObject("Widget", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(widget);
        Assert.True(live.InitiateIntentToDoSpecialPower("TestReplacePower", null));
        game.Step();

        var shadowHost = game.SpawnObject("Widget", game.CivilianPlayer, new Vector3(200, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
