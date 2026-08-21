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
//
// Rank-1-floor caveat (why every case below asserts a DELTA, never an absolute total): the
// engine adds an ExperienceUpdate helper ("ModuleTag_ExperienceHelper") to every object on
// every non-Generals game, and on its first tick that helper raises a still-zero
// CurrentExperience to the rank-1 floor of 1. So no trainable object in a stepped world ever
// sits at an absolute 0, whether or not this module ever shares anything with it - asserting
// `== 0` or `== 100` would be asserting against that unrelated engine behavior. Each case
// therefore settles the world first (SettleFrames, enough steps for every spawned object's
// helper to have run), captures each tracker's settled value, and asserts what this module did
// or did not add on top of it.

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

    /// <summary>Steps enough frames for every already-spawned object's ExperienceUpdate helper
    /// to have run its rank-1 seeding, so a value captured afterwards is a stable baseline.</summary>
    private const int SettleFrames = 3;

    private static void StepFrames(HeadlessSimGame game, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            game.Step();
        }
    }

    private static int Experience(GameObject obj) => obj.ExperienceTracker.CurrentExperience;

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

        // Settle first: the module's baseline is live and every helper's rank-1 seeding is done.
        StepFrames(game, SettleFrames);
        var recipientBefore = Experience(recipient);
        var sharerBefore = Experience(sharer);

        sharer.ExperienceTracker.AddExperiencePoints(100);
        StepFrames(game, 2);

        Assert.Equal(100, Experience(recipient) - recipientBefore);

        // The sharer keeps its own gain - sharing is additive, not a redirect (which is what
        // distinguishes this from the ExperienceSink single-target-redirect mechanism).
        Assert.Equal(100, Experience(sharer) - sharerBefore);
    }

    [Fact]
    public void NoShare_CandidateOutsideRadius()
    {
        var game = NewGame();
        var sharer = game.SpawnObject("SharerUnit", game.CivilianPlayer, new Vector3(0, 0, 0));
        var recipient = game.SpawnObject("HeroRecipient", game.CivilianPlayer, new Vector3(200, 0, 0));

        StepFrames(game, SettleFrames);
        var recipientBefore = Experience(recipient);

        sharer.ExperienceTracker.AddExperiencePoints(100);
        StepFrames(game, 2);

        Assert.Equal(0, Experience(recipient) - recipientBefore);
    }

    [Fact]
    public void NoShare_CandidateFailsObjectFilter()
    {
        var game = NewGame();
        var sharer = game.SpawnObject("SharerUnit", game.CivilianPlayer, new Vector3(0, 0, 0));
        var recipient = game.SpawnObject("NonHeroRecipient", game.CivilianPlayer, new Vector3(50, 0, 0));

        StepFrames(game, SettleFrames);
        var recipientBefore = Experience(recipient);

        sharer.ExperienceTracker.AddExperiencePoints(100);
        StepFrames(game, 2);

        Assert.Equal(0, Experience(recipient) - recipientBefore);
    }

    [Fact]
    public void NoShare_ZeroDelta()
    {
        var game = NewGame();
        game.SpawnObject("SharerUnit", game.CivilianPlayer, new Vector3(0, 0, 0));
        var recipient = game.SpawnObject("HeroRecipient", game.CivilianPlayer, new Vector3(50, 0, 0));

        StepFrames(game, SettleFrames);
        var recipientBefore = Experience(recipient);

        // Step several more frames without ever granting XP to the sharer.
        StepFrames(game, 6);

        Assert.Equal(0, Experience(recipient) - recipientBefore);
    }

    [Fact]
    public void NoPhantomShareOfTheEngineRankOneSeed()
    {
        // Regression for the ctor baseline clamp: the engine's own ExperienceUpdate helper
        // lifts the SHARER from 0 to the rank-1 floor of 1 on its first tick. That is an
        // initialization, not a gain, and must not be observed as a +1 delta and broadcast.
        // Asserted without hard-coding what the floor is: an in-range hero must end up with
        // exactly what an identical hero parked outside Radius ends up with, since the sharer
        // never actually earns anything here.
        var game = NewGame();
        game.SpawnObject("SharerUnit", game.CivilianPlayer, new Vector3(0, 0, 0));
        var inRange = game.SpawnObject("HeroRecipient", game.CivilianPlayer, new Vector3(50, 0, 0));
        var control = game.SpawnObject("HeroRecipient", game.CivilianPlayer, new Vector3(400, 0, 0));

        StepFrames(game, 6);

        Assert.Equal(Experience(control), Experience(inRange));
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

        StepFrames(game, SettleFrames);
        sharer.ExperienceTracker.AddExperiencePoints(300);
        StepFrames(game, 2); // baseline resyncs to the new total; no recipient exists yet

        var recipient = game.SpawnObject("HeroRecipient", game.CivilianPlayer, new Vector3(50, 0, 0));
        StepFrames(game, SettleFrames);
        var recipientBefore = Experience(recipient);
        StepFrames(game, 2);

        Assert.Equal(0, Experience(recipient) - recipientBefore);
    }

    [Fact]
    public void MultipleMatchingRecipientsEachReceiveTheFullFlatShare()
    {
        var game = NewGame();
        var sharer = game.SpawnObject("SharerUnit", game.CivilianPlayer, new Vector3(0, 0, 0));
        var recipientA = game.SpawnObject("HeroRecipient", game.CivilianPlayer, new Vector3(50, 0, 0));
        var recipientB = game.SpawnObject("HeroRecipient", game.CivilianPlayer, new Vector3(-50, 0, 0));

        StepFrames(game, SettleFrames);
        var beforeA = Experience(recipientA);
        var beforeB = Experience(recipientB);

        sharer.ExperienceTracker.AddExperiencePoints(100);
        StepFrames(game, 2);

        // The flat share is not divided among recipients - each qualifying candidate
        // independently receives the full delta; there is no pool to split.
        Assert.Equal(100, Experience(recipientA) - beforeA);
        Assert.Equal(100, Experience(recipientB) - beforeB);
    }

    [Fact]
    public void RecipientNotAcceptingExperienceIsSilentlyUnaffected()
    {
        var game = NewGame();
        var sharer = game.SpawnObject("SharerUnit", game.CivilianPlayer, new Vector3(0, 0, 0));
        var recipient = game.SpawnObject("NonTrainableRecipient", game.CivilianPlayer, new Vector3(50, 0, 0));

        StepFrames(game, SettleFrames);
        sharer.ExperienceTracker.AddExperiencePoints(100);
        StepFrames(game, 2);

        // Not trainable and no sink of its own, so AddExperiencePoints no-ops internally - the
        // absolute 0 is meaningful here (the rank-1 seeding skips a non-trainable object too).
        Assert.Equal(0, Experience(recipient));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc()
    {
        var game = NewGame();
        var liveHost = game.SpawnObject("SharerUnit", game.CivilianPlayer, new Vector3(0, 0, 0));
        game.SpawnObject("HeroRecipient", game.CivilianPlayer, new Vector3(50, 0, 0));

        StepFrames(game, SettleFrames);
        liveHost.ExperienceTracker.AddExperiencePoints(100);
        StepFrames(game, 2);

        var live = ModuleOf(liveHost);
        var shadow = ModuleOf(game.SpawnObject("SharerUnit", game.CivilianPlayer, new Vector3(400, 400, 0)));

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
