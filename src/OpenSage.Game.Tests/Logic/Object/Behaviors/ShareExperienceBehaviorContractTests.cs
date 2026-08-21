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
//
// Call-occurrence caveat (R14 mutation-pilot follow-up, dr-0031 finding #1): a mutation-lite
// pilot flipped the module's `delta > 0` gate (Update()) to `delta >= 0` and every test above
// still passed, 0/10 red. The reason is structural, not a weak assertion someone forgot to
// write: ShareGain(0) calls ExperienceTracker.AddExperiencePoints(0, canScaleForBonus: true) on
// every candidate, and adding zero XP is a byte-for-byte numeric no-op under the current
// AddExperiencePoints implementation for every recipient shape this file exercises (trainable,
// non-trainable, sink-redirected, at a level boundary) - x + 0 == x, so it can never cross a
// veterancy threshold or otherwise change any GameObject-observable state. No amount of net-XP
// or before/after-delta assertion can ever distinguish "ShareGain ran and shared zero" from
// "ShareGain never ran" through GameObject/ExperienceTracker's public surface; that gap is what
// let the mutant through. Killing it needs a signal that is not the shared AMOUNT but the fact
// of the call. ShareGain's only observable action besides AddExperiencePoints is the radius scan
// itself (Context.Partition.QueryObjectsInRadius) - it runs exactly once per ShareGain
// invocation and only from inside the `delta > 0` branch, so counting THAT call, independent of
// what it shares, is what makes the gate itself testable. QueryOccurrenceCountingPartition below
// installs a counting decorator over the game's single shared SimContext.Partition (reflection
// injection into its auto-property backing field - the same test-only injection idiom
// BunkerBusterBehaviorContractTests.InjectContain already uses for GameObject.Contain; no
// production file is touched). In this test file's minimal object set nothing else queries the
// partition, so the counter's value is exactly "how many times did ShareGain run this test".

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
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

    /// <summary>
    /// Test-only call-occurrence instrumentation (see the header caveat above): wraps the live
    /// IPartitionQuery so a test can assert exactly how many times ShareGain's radius scan ran,
    /// independent of the net XP delta it produced. GetVisionRange is forwarded untouched -
    /// nothing in this file's object set uses it, but a faithful decorator does not silently
    /// drop interface members it happens not to need today.
    /// </summary>
    private sealed class QueryOccurrenceCountingPartition : IPartitionQuery
    {
        private readonly IPartitionQuery _inner;

        public QueryOccurrenceCountingPartition(IPartitionQuery inner) => _inner = inner;

        public int QueryCount { get; private set; }

        public IEnumerable<GameObject> QueryObjectsInRadius(GameObject center, Fix64 radius)
        {
            QueryCount++;
            return _inner.QueryObjectsInRadius(center, radius);
        }

        public Fix64 GetVisionRange(GameObject gameObject) => _inner.GetVisionRange(gameObject);
    }

    /// <summary>
    /// Installs <see cref="QueryOccurrenceCountingPartition"/> over the game's single shared
    /// SimContext.Partition. SimContext.Partition has no public setter by design (the frozen
    /// ISimContext contract, api-freeze-v1 §3) - a test-only reflection write into its
    /// compiler-generated backing field is the only door in, same shape as
    /// BunkerBusterBehaviorContractTests.InjectContain reaching GameObject's Contain backing
    /// field. Call this AFTER any settling steps whose radius scans should not be counted.
    /// </summary>
    private static QueryOccurrenceCountingPartition InstallShareGainOccurrenceCounter(HeadlessSimGame game)
    {
        var simContext = (SimContext)game.GameEngine.SimContext;
        var field = typeof(SimContext).GetField("<Partition>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);

        var counter = new QueryOccurrenceCountingPartition((IPartitionQuery)field!.GetValue(simContext)!);
        field.SetValue(simContext, counter);
        return counter;
    }

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

    // ---- R14 mutation-pilot follow-up (dr-0031 finding #1): call-occurrence / count / gate-
    // boundary cases, re-derived per the pilot's diagnosis. Every case above this line stays -
    // it is still correctly asserting what it claims to assert - but none of them can see the
    // `delta > 0` gate itself, only its numeric aftermath. These can.

    [Fact]
    public void ZeroDeltaTicksNeverInvokeShareGain()
    {
        // The exact mutation the pilot flipped (delta > 0 -> delta >= 0) only changes behavior
        // on a tick where delta computes to precisely zero - and once the world is settled,
        // EVERY tick with no grant is such a tick (Update() resyncs the baseline to
        // currentExperience unconditionally each frame, so a steady-state object sits at
        // delta == 0 forever, not merely on one boundary frame). This asserts ShareGain's
        // radius scan runs zero times across many such ticks - the numeric caveat above is why
        // this needs the occurrence counter rather than an XP-delta assertion.
        var game = NewGame();
        game.SpawnObject("SharerUnit", game.CivilianPlayer, new Vector3(0, 0, 0));
        game.SpawnObject("HeroRecipient", game.CivilianPlayer, new Vector3(50, 0, 0));

        StepFrames(game, SettleFrames);
        var counter = InstallShareGainOccurrenceCounter(game);

        // No XP ever granted after this point - every one of these ticks has delta == 0.
        StepFrames(game, 8);

        Assert.Equal(0, counter.QueryCount);
    }

    [Fact]
    public void ShareGainFiresExactlyOnceForEachDistinctGainTick_ThenStopsAtSteadyState()
    {
        // Per-tick share counting (spec §1.2 step 4): a single real gain must produce exactly
        // one ShareGain invocation, not zero (missed), not two-plus (double-fired or re-fired on
        // a later settle tick), and a later zero-delta tick must add no further invocations -
        // the same gate-boundary property as ZeroDeltaTicksNeverInvokeShareGain, exercised
        // immediately after a real share rather than from a never-granted baseline.
        var game = NewGame();
        var sharer = game.SpawnObject("SharerUnit", game.CivilianPlayer, new Vector3(0, 0, 0));
        var recipient = game.SpawnObject("HeroRecipient", game.CivilianPlayer, new Vector3(50, 0, 0));

        StepFrames(game, SettleFrames);
        var counter = InstallShareGainOccurrenceCounter(game);
        var recipientBefore = Experience(recipient);

        sharer.ExperienceTracker.AddExperiencePoints(100);
        StepFrames(game, 2);

        Assert.Equal(1, counter.QueryCount);
        Assert.Equal(100, Experience(recipient) - recipientBefore);

        // Settle further with no additional grant: the count must not keep climbing.
        StepFrames(game, 6);

        Assert.Equal(1, counter.QueryCount);
        Assert.Equal(100, Experience(recipient) - recipientBefore);
    }

    [Fact]
    public void ShareGain_FiresOnceForEachOfSeveralDistinctGainTicks_AndRecipientAccumulatesEachShare()
    {
        // Extends the single-grant case above across several separate gain ticks, checked after
        // EACH one rather than only at the end - catches a bug class the original 10-test suite
        // could not (it only ever compared one before/after span): a stale or re-shared delta, a
        // dropped tick, or a share that fires more than once per real gain would all show up as
        // a call count or a per-step recipient total that stops matching the running grant sum.
        var game = NewGame();
        var sharer = game.SpawnObject("SharerUnit", game.CivilianPlayer, new Vector3(0, 0, 0));
        var recipient = game.SpawnObject("HeroRecipient", game.CivilianPlayer, new Vector3(50, 0, 0));

        StepFrames(game, SettleFrames);
        var counter = InstallShareGainOccurrenceCounter(game);
        var recipientBaseline = Experience(recipient);

        var grants = new[] { 10, 25, 40 };
        var runningTotal = 0;
        for (var i = 0; i < grants.Length; i++)
        {
            sharer.ExperienceTracker.AddExperiencePoints(grants[i]);
            StepFrames(game, 2);
            runningTotal += grants[i];

            Assert.Equal(i + 1, counter.QueryCount);
            Assert.Equal(runningTotal, Experience(recipient) - recipientBaseline);
        }

        // Settle further with no additional grant: neither the call count nor the recipient's
        // total may drift past the last real gain.
        StepFrames(game, 5);

        Assert.Equal(grants.Length, counter.QueryCount);
        Assert.Equal(runningTotal, Experience(recipient) - recipientBaseline);
    }

    [Fact]
    public void NoShare_NegativeDeltaFromExperienceLoss()
    {
        // Spec §1.2 step 3: a negative delta - "the loss/reset case from SetExperienceAndLevel" -
        // is explicitly named as sharing nothing, distinct from the zero-delta case above, and
        // was previously untested (every other case only ever grants, never lowers, XP).
        // SetExperienceAndLevel is a direct rewrite, not an AddExperiencePoints-shaped gain, so
        // per §1.0 it must never be mirror-broadcast even though CurrentExperience visibly
        // drops.
        var game = NewGame();
        var sharer = game.SpawnObject("SharerUnit", game.CivilianPlayer, new Vector3(0, 0, 0));
        var recipient = game.SpawnObject("HeroRecipient", game.CivilianPlayer, new Vector3(50, 0, 0));

        StepFrames(game, SettleFrames);
        sharer.ExperienceTracker.AddExperiencePoints(300);
        StepFrames(game, 2);
        var recipientAfterGain = Experience(recipient);

        var counter = InstallShareGainOccurrenceCounter(game);

        sharer.ExperienceTracker.SetExperienceAndLevel(50);
        StepFrames(game, 2);

        Assert.Equal(0, counter.QueryCount);
        Assert.Equal(recipientAfterGain, Experience(recipient));

        // The baseline resync after a loss (Update()'s BaselineFor clamp) must not manufacture
        // a phantom future gain either: further zero-delta ticks still fire nothing.
        StepFrames(game, 4);

        Assert.Equal(0, counter.QueryCount);
        Assert.Equal(recipientAfterGain, Experience(recipient));
    }
}
