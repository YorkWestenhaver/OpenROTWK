// Contract tests for the ShareExperienceBehavior port (R13). See
// bfme2-workbench/research/modules-r13/specs/ShareExperienceBehaviorModuleData.md §3 for the
// full test plan this file implements.
//
// Sleepy-update caveat (applies to every case below that asserts a shared-XP amount): a
// freshly spawned UpdateModule's wake frame is not guaranteed live in the same
// HeadlessSimGame.Step() call that spawned it - the module's first Update() call lands on the
// SECOND Step() after spawn, not the first. Every case that grants XP to the sharer after spawn
// and then expects the share to have propagated calls game.Step() an extra time beyond the
// naive frame count.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Behaviors;

public class ShareExperienceBehaviorContractTests
{
    private const string Definitions = @"
Object SharerUnit
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  IsTrainable = Yes
  ExperienceRequired = 0 100 200 300 400
  Behavior = ShareExperienceBehavior ModuleTag_Share
    ObjectFilter = ANY +HERO
    Radius = 100.0
    DropOff = 1.0
  End
End

Object HeroRecipient
  KindOf = HERO
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  IsTrainable = Yes
  ExperienceRequired = 0 100 200 300 400
End

Object NonHeroRecipient
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  IsTrainable = Yes
  ExperienceRequired = 0 100 200 300 400
End

Object NonTrainableRecipient
  KindOf = HERO
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xE5E);
        game.LoadIniText(Definitions);
        return game;
    }

    private static void StepFrames(HeadlessSimGame game, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            game.Step();
        }
    }

    private static ShareExperienceBehavior ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<ShareExperienceBehavior>().Single();

    [Fact]
    public void ParserAssignsAllFields()
    {
        var game = NewGame();
        var sharer = game.SpawnObject("SharerUnit", game.CivilianPlayer, new Vector3(0, 0, 0));

        var data = sharer.Definition.Behaviors.Values
            .Select(v => v.Data)
            .OfType<ShareExperienceBehaviorModuleData>()
            .Single();

        Assert.Equal(new Fix64(100), data.Radius);
        Assert.Equal(Fix64.One, data.DropOff);
        Assert.NotNull(data.ObjectFilter);
        Assert.True(data.ObjectFilter.Rules.Get(ObjectFilterRule.Any));
        Assert.True(data.ObjectFilter.Include.Get(ObjectKinds.Hero));
    }

    [Fact]
    public void FlatShare_MatchingCandidateInRadiusReceivesFullGain()
    {
        var game = NewGame();
        var sharer = game.SpawnObject("SharerUnit", game.CivilianPlayer, new Vector3(0, 0, 0));
        var recipient = game.SpawnObject("HeroRecipient", game.CivilianPlayer, new Vector3(50, 0, 0));

        // Reach the module's second guaranteed tick before granting XP, so the ctor-time
        // baseline seed has definitely been consumed once already.
        StepFrames(game, 2);

        sharer.ExperienceTracker.AddExperiencePoints(100);
        StepFrames(game, 2);

        Assert.Equal(100, recipient.ExperienceTracker.CurrentExperience);
        Assert.Equal(100, sharer.ExperienceTracker.CurrentExperience);
    }

    [Fact]
    public void NoShare_CandidateOutsideRadius()
    {
        var game = NewGame();
        var sharer = game.SpawnObject("SharerUnit", game.CivilianPlayer, new Vector3(0, 0, 0));
        var recipient = game.SpawnObject("HeroRecipient", game.CivilianPlayer, new Vector3(200, 0, 0));

        StepFrames(game, 2);
        sharer.ExperienceTracker.AddExperiencePoints(100);
        StepFrames(game, 2);

        Assert.Equal(0, recipient.ExperienceTracker.CurrentExperience);
    }

    [Fact]
    public void NoShare_CandidateFailsObjectFilter()
    {
        var game = NewGame();
        var sharer = game.SpawnObject("SharerUnit", game.CivilianPlayer, new Vector3(0, 0, 0));
        var recipient = game.SpawnObject("NonHeroRecipient", game.CivilianPlayer, new Vector3(50, 0, 0));

        StepFrames(game, 2);
        sharer.ExperienceTracker.AddExperiencePoints(100);
        StepFrames(game, 2);

        Assert.Equal(0, recipient.ExperienceTracker.CurrentExperience);
    }

    [Fact]
    public void NoShare_ZeroDelta()
    {
        var game = NewGame();
        game.SpawnObject("SharerUnit", game.CivilianPlayer, new Vector3(0, 0, 0));
        var recipient = game.SpawnObject("HeroRecipient", game.CivilianPlayer, new Vector3(50, 0, 0));

        // Step several frames without ever granting XP to the sharer.
        StepFrames(game, 6);

        Assert.Equal(0, recipient.ExperienceTracker.CurrentExperience);
    }

    [Fact]
    public void NoPhantomShareOfPreExistingExperienceOnSpawn()
    {
        // There is no declarative "spawn with nonzero starting XP" path in this engine today
        // (no ObjectDefinition field sets an initial VeterancyLevel/CurrentExperience, and
        // GameObject.Rank can only be set after the object - and therefore its behavior
        // modules, including this one's ctor - already exists). So this exercises the same
        // invariant the ctor-time seed protects (_lastObservedExperience already matches
        // CurrentExperience => no phantom broadcast) via the module's own natural per-tick
        // convergence instead: grant the sharer XP while no recipient yet exists in the world
        // (nothing to phantom-share to), let the module's next guaranteed tick resync its
        // baseline to the new CurrentExperience, THEN spawn a recipient in range. If the ctor
        // seed / resync were broken (e.g. baseline never updated), the recipient would
        // wrongly receive the earlier gain on the next tick.
        var game = NewGame();
        var sharer = game.SpawnObject("SharerUnit", game.CivilianPlayer, new Vector3(0, 0, 0));

        StepFrames(game, 2);
        sharer.ExperienceTracker.AddExperiencePoints(300);
        StepFrames(game, 2); // baseline resyncs to 300; no recipient exists yet to receive it

        var recipient = game.SpawnObject("HeroRecipient", game.CivilianPlayer, new Vector3(50, 0, 0));
        StepFrames(game, 2);

        Assert.Equal(0, recipient.ExperienceTracker.CurrentExperience);
    }

    [Fact]
    public void MultipleMatchingRecipientsEachReceiveTheFullFlatShare()
    {
        var game = NewGame();
        var sharer = game.SpawnObject("SharerUnit", game.CivilianPlayer, new Vector3(0, 0, 0));
        var recipientA = game.SpawnObject("HeroRecipient", game.CivilianPlayer, new Vector3(50, 0, 0));
        var recipientB = game.SpawnObject("HeroRecipient", game.CivilianPlayer, new Vector3(-50, 0, 0));

        StepFrames(game, 2);
        sharer.ExperienceTracker.AddExperiencePoints(100);
        StepFrames(game, 2);

        Assert.Equal(100, recipientA.ExperienceTracker.CurrentExperience);
        Assert.Equal(100, recipientB.ExperienceTracker.CurrentExperience);
    }

    [Fact]
    public void RecipientNotAcceptingExperienceIsSilentlyUnaffected()
    {
        var game = NewGame();
        var sharer = game.SpawnObject("SharerUnit", game.CivilianPlayer, new Vector3(0, 0, 0));
        var recipient = game.SpawnObject("NonTrainableRecipient", game.CivilianPlayer, new Vector3(50, 0, 0));

        StepFrames(game, 2);
        sharer.ExperienceTracker.AddExperiencePoints(100);
        StepFrames(game, 2);

        Assert.Equal(0, recipient.ExperienceTracker.CurrentExperience);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc()
    {
        var game = NewGame();
        var liveHost = game.SpawnObject("SharerUnit", game.CivilianPlayer, new Vector3(0, 0, 0));
        game.SpawnObject("HeroRecipient", game.CivilianPlayer, new Vector3(50, 0, 0));

        StepFrames(game, 2);
        liveHost.ExperienceTracker.AddExperiencePoints(100);
        StepFrames(game, 2);

        var live = ModuleOf(liveHost);
        var shadow = ModuleOf(game.SpawnObject("SharerUnit", game.CivilianPlayer, new Vector3(400, 400, 0)));

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
