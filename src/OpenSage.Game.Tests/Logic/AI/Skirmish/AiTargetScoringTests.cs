#nullable enable

// S9-09 (R15 L3) tests: AiTargetScoring - the pure half of the attack lane.
//
// THE TEST THAT MATTERS MOST IN THIS FILE
//
// HordeMembers_AreNeverLegalTargets. It is the mirror of S9-08's recruitment rule. A ten-orc
// horde is eleven objects in the snapshot, and ten of them are members that the horde will
// simply keep replacing; a wave ordered onto a member fights a sub-object forever while the
// horde OBJECT - the thing whose death removes the threat - is never touched. The order looks
// correct in every log, so nothing else in the lane would catch it.
//
// The rest pin the score's two-level structure (priority outranks proximity outright, proximity
// only orders within a class), the tie-break total order, and that a pick does not depend on the
// order the snapshot happened to list candidates in.

using System.Collections.Generic;
using System.Numerics;
using OpenSage.Logic.AI.Skirmish;
using Xunit;

namespace OpenSage.Tests.Logic.AI.Skirmish;

public class AiTargetScoringTests
{
    private const int EnemyIndex = 1;

    private static AiObjectView Unit(uint id, float x = 0f, bool isHorde = false, bool isHordeMember = false)
        => new(new ObjectId(id), isHorde ? "MordorFighterHorde" : "MordorFighter", new Vector3(x, 0f, 0f),
            EnemyIndex, false, false, 1.0f, isHorde, isHordeMember);

    private static AiObjectView Structure(uint id, float x = 0f, bool underConstruction = false)
        => new(new ObjectId(id), "MordorOrcPit", new Vector3(x, 0f, 0f), EnemyIndex, true, underConstruction, 1.0f);

    private static Vector3 At(float x) => new(x, 0f, 0f);

    // ---- legality --------------------------------------------------------------------------

    [Fact]
    public void HordeMembers_AreNeverLegalTargets()
    {
        var member = Unit(10, isHordeMember: true);
        var horde = Unit(11, isHorde: true);

        Assert.Equal(AiAttackPriority.None, AiTargetScoring.Classify(member));
        Assert.False(AiTargetScoring.TryScore(member, Vector3.Zero, out _));

        // The parent horde in the same snapshot IS the target.
        Assert.Equal(AiAttackPriority.MobileUnit, AiTargetScoring.Classify(horde));

        var best = AiTargetScoring.PickBest(new List<AiObjectView> { member, horde }, Vector3.Zero);

        Assert.NotNull(best);
        Assert.Equal(new ObjectId(11), best!.Value.Id);
    }

    [Fact]
    public void InvalidIds_AreNotTargets()
    {
        Assert.Equal(AiAttackPriority.None, AiTargetScoring.Classify(Unit(0)));
        Assert.Null(AiTargetScoring.PickBest(new List<AiObjectView> { Unit(0) }, Vector3.Zero));
    }

    [Fact]
    public void PickBest_OverNullOrEmptyCandidates_IsNull()
    {
        Assert.Null(AiTargetScoring.PickBest(null, Vector3.Zero));
        Assert.Null(AiTargetScoring.PickBest(new List<AiObjectView>(), Vector3.Zero));
    }

    [Theory]
    [InlineData(false, false, AiAttackPriority.MobileUnit)]
    [InlineData(true, false, AiAttackPriority.Structure)]
    [InlineData(true, true, AiAttackPriority.UnderConstruction)]
    public void Classify_SortsTheThreeRealClasses(bool isStructure, bool underConstruction, AiAttackPriority expected)
    {
        var view = new AiObjectView(
            new ObjectId(7), "Thing", Vector3.Zero, EnemyIndex, isStructure, underConstruction, 1.0f);

        Assert.Equal(expected, AiTargetScoring.Classify(view));
    }

    [Fact]
    public void UnitsStillBeingTrained_AreNotOnTheFieldAndAreNotTargets()
    {
        var training = new AiObjectView(
            new ObjectId(9), "MordorFighter", Vector3.Zero, EnemyIndex, false, true, 1.0f);

        Assert.Equal(AiAttackPriority.None, AiTargetScoring.Classify(training));
    }

    // ---- the two-level score -----------------------------------------------------------------

    [Fact]
    public void PriorityOutranksProximity_EvenAtMaximumDistance()
    {
        // A unit at the far horizon still beats a structure we are standing on: the classes are
        // separated by more than the whole proximity range, by construction.
        var farUnit = Unit(20, x: AiTargetScoring.ProximityBucketSize * AiTargetScoring.ProximityBuckets * 2);
        var nearStructure = Structure(21, x: 0f);

        var best = AiTargetScoring.PickBest(new List<AiObjectView> { nearStructure, farUnit }, Vector3.Zero);

        Assert.Equal(new ObjectId(20), best!.Value.Id);
        Assert.Equal(AiAttackPriority.MobileUnit, best.Value.Priority);
    }

    [Fact]
    public void ProximityOrdersWithinAClass()
    {
        var near = Structure(31, x: 50f);
        var far = Structure(30, x: 2000f);

        var best = AiTargetScoring.PickBest(new List<AiObjectView> { far, near }, Vector3.Zero);

        // Note the ids: the nearer one has the HIGHER id, so this cannot pass by the id
        // tie-break alone.
        Assert.Equal(new ObjectId(31), best!.Value.Id);
    }

    [Fact]
    public void FinishedStructures_OutrankHalfBuiltOnes()
    {
        var halfBuilt = Structure(40, x: 0f, underConstruction: true);
        var finished = Structure(41, x: 500f);

        var best = AiTargetScoring.PickBest(new List<AiObjectView> { halfBuilt, finished }, Vector3.Zero);

        Assert.Equal(new ObjectId(41), best!.Value.Id);
    }

    [Fact]
    public void Bucket_IsZeroOnTopOfUs_AndClampsAtTheHorizon()
    {
        Assert.Equal(0, AiTargetScoring.Bucket(Vector3.Zero, Vector3.Zero));

        Assert.Equal(
            AiTargetScoring.ProximityBuckets - 1,
            AiTargetScoring.Bucket(Vector3.Zero, At(AiTargetScoring.ProximityBucketSize * 10_000)));
    }

    [Fact]
    public void Bucket_IgnoresHeight()
    {
        var flat = AiTargetScoring.Bucket(Vector3.Zero, new Vector3(400f, 0f, 0f));
        var onACliff = AiTargetScoring.Bucket(Vector3.Zero, new Vector3(400f, 900f, 0f));

        Assert.Equal(flat, onACliff);
    }

    [Fact]
    public void Bucket_OnANanPosition_DoesNotProduceAnArbitraryValue()
    {
        Assert.Equal(0, AiTargetScoring.Bucket(Vector3.Zero, new Vector3(float.NaN, 0f, float.NaN)));
    }

    // ---- total order -------------------------------------------------------------------------

    [Fact]
    public void Ties_AreBrokenByLowerObjectIdThenLowerOwnerIndex()
    {
        var high = Structure(60);
        var low = Structure(59);

        var best = AiTargetScoring.PickBest(new List<AiObjectView> { high, low }, Vector3.Zero);
        Assert.Equal(new ObjectId(59), best!.Value.Id);

        // Same score AND same id (only reachable by constructing the score directly): the owner
        // index decides, so the comparison is a total order and does not lean on sort stability.
        var p3 = new AiTargetScore(new ObjectId(5), 3, AiAttackPriority.Structure, 0, 100);
        var p1 = new AiTargetScore(new ObjectId(5), 1, AiAttackPriority.Structure, 0, 100);

        Assert.True(p1.CompareTo(p3) < 0);
        Assert.True(p3.CompareTo(p1) > 0);
        Assert.Equal(0, p1.CompareTo(p1));
    }

    [Fact]
    public void PickBest_IsIndependentOfSnapshotOrder()
    {
        var candidates = new List<AiObjectView>
        {
            Structure(70, x: 100f),
            Unit(71, x: 3000f),
            Structure(72, x: 10f),
            // Nearer than unit 71 AND a higher id, so only the score can pick it.
            Unit(73, x: 2000f),
            Structure(74, x: 5f),
        };

        var forwards = AiTargetScoring.PickBest(candidates, Vector3.Zero);

        candidates.Reverse();
        var backwards = AiTargetScoring.PickBest(candidates, Vector3.Zero);

        Assert.Equal(forwards, backwards);
        Assert.Equal(AiAttackPriority.MobileUnit, forwards!.Value.Priority);
        Assert.Equal(new ObjectId(73), forwards.Value.Id);
    }

    [Fact]
    public void ScoreAll_IsBestFirstAndDropsIllegalCandidates()
    {
        var candidates = new List<AiObjectView>
        {
            Structure(80, x: 1000f),
            Unit(81, isHordeMember: true),
            Unit(82, x: 1000f),
            Structure(83, x: 10f, underConstruction: true),
        };

        var scored = new List<AiTargetScore>();
        AiTargetScoring.ScoreAll(candidates, Vector3.Zero, scored);

        Assert.Equal(3, scored.Count);
        Assert.Equal(new ObjectId(82), scored[0].Id);
        Assert.Equal(new ObjectId(80), scored[1].Id);
        Assert.Equal(new ObjectId(83), scored[2].Id);

        for (var i = 1; i < scored.Count; i++)
        {
            Assert.True(scored[i - 1].Score >= scored[i].Score);
        }
    }

    [Fact]
    public void ScoreAll_ClearsItsOutputList()
    {
        var scored = new List<AiTargetScore> { new(new ObjectId(1), 0, AiAttackPriority.Structure, 0, 1) };

        AiTargetScoring.ScoreAll(new List<AiObjectView>(), Vector3.Zero, scored);

        Assert.Empty(scored);
    }

    // ---- centres -------------------------------------------------------------------------------

    [Fact]
    public void CentreOf_AveragesOnlyTheNamedIds_AndFallsBackWhenNoneMatch()
    {
        var objects = new List<AiObjectView> { Unit(90, x: 0f), Unit(91, x: 100f), Unit(92, x: 5000f) };
        var ids = new List<ObjectId> { new(90), new(91) };

        Assert.Equal(new Vector3(50f, 0f, 0f), AiTargetScoring.CentreOf(objects, ids, Vector3.Zero));

        var fallback = new Vector3(7f, 8f, 9f);
        Assert.Equal(fallback, AiTargetScoring.CentreOf(objects, new List<ObjectId> { new(999) }, fallback));
        Assert.Equal(fallback, AiTargetScoring.CentreOf(objects, new List<ObjectId>(), fallback));
        Assert.Equal(fallback, AiTargetScoring.CentreOf(null, ids, fallback));
    }

    [Fact]
    public void TryCentreOf_ReportsAbsenceRatherThanReturningTheOrigin()
    {
        var objects = new List<AiObjectView> { Unit(93, x: 10f), Structure(94, x: 30f) };

        Assert.True(AiTargetScoring.TryCentreOf(objects, static o => o.IsCompletedStructure, out var structures));
        Assert.Equal(new Vector3(30f, 0f, 0f), structures);

        Assert.False(AiTargetScoring.TryCentreOf(objects, static o => o.IsUnderConstruction, out var none));
        Assert.Equal(Vector3.Zero, none);
    }
}
