#nullable enable

// S9-09 (R15 L3): "which enemy object should this wave hit" - the whole of it, as a pure
// function of the world snapshot.
//
// Kept in its own file, with no manager state and no seams, for one reason: target choice is
// the part of the attack lane most likely to be retuned (S9-11 tuning, S9-13 .bse personalities,
// a later oracle round), and a pure static scorer can be retuned and re-tested without touching
// the wave machine in AiAttackCoordinator.cs.
//
// THE SCORE
//
//     score = priority * ProximityBuckets - proximityBucket
//
// A priority class is worth strictly more than the entire proximity range, so proximity only
// ever breaks ties WITHIN a class: the AI does not walk past a barracks to reach a slightly
// closer farm, but among equally-interesting targets it picks the near one. Buckets rather than
// raw distance because a raw-distance term would make the choice flip whenever two candidates
// were a hair apart, and the wave would oscillate between them on consecutive re-scans.
//
// DETERMINISM
//
// Every number below is an int except the one distance computation, which is
// (dx*dx + dz*dz) followed by MathF.Sqrt: plain IEEE-754 add/multiply/sqrt, all correctly
// rounded and therefore identical on x86 and Apple Silicon (C# does not contract to FMA). The
// float result is immediately quantized into an int bucket and never compared for equality.
// Ties are broken by lower object id and then by owner player index, so a peer that saw the
// same candidate SET produces the same pick whatever order it enumerated them in.
//
// CLEAN-ROOM: the priority classes and their weights are a v1 heuristic derived from the
// snapshot facts this lane already has (KINDOF STRUCTURE, under-construction, horde membership).
// They are not recovered retail numbers and are not read out of any binary.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace OpenSage.Logic.AI.Skirmish;

/// <summary>
/// How interesting a class of enemy object is to an attack wave. Higher wins outright; the
/// numeric values are the weights the scorer multiplies, so keep them ordered and spaced.
/// </summary>
/// <remarks>
/// Ordering rationale, all v1 heuristic (TODO S9-11: make these tunable rather than constant):
/// <list type="bullet">
///   <item><b>MobileUnit</b> is highest because it is the only class that shoots back and the
///   only one that can leave: a wave that ignores the army defending a building loses the
///   building fight anyway, and a wave that chases buildings while being shot is the classic
///   "AI melts on approach" failure.</item>
///   <item><b>Structure</b> next: finished buildings are what actually ends a match.</item>
///   <item><b>UnderConstruction</b> last among real targets: it is contributing nothing yet,
///   so killing it is worth less than killing a working building of the same cost.</item>
/// </list>
/// </remarks>
public enum AiAttackPriority
{
    /// <summary>Not a legal target for a wave. Never scored.</summary>
    None = 0,

    /// <summary>A structure that is still being built.</summary>
    UnderConstruction = 1,

    /// <summary>A finished structure.</summary>
    Structure = 2,

    /// <summary>A mobile enemy - a horde object or a standalone unit.</summary>
    MobileUnit = 3,
}

/// <summary>
/// One scored candidate. Comparable, so a caller can sort candidates instead of re-implementing
/// the tie-break rules.
/// </summary>
/// <param name="Id">The candidate object.</param>
/// <param name="OwnerIndex">Owning player index; the last tie-break.</param>
/// <param name="Priority">Class the candidate scored in.</param>
/// <param name="ProximityBucket">
/// Quantized distance from the wave, 0 (on top of us) to
/// <see cref="AiTargetScoring.ProximityBuckets"/> - 1 (at or beyond the horizon).
/// </param>
/// <param name="Score">The combined score. Higher is better.</param>
public readonly record struct AiTargetScore(
    ObjectId Id,
    int OwnerIndex,
    AiAttackPriority Priority,
    int ProximityBucket,
    int Score) : IComparable<AiTargetScore>
{
    /// <summary>
    /// Best-first ordering: higher score, then LOWER object id, then LOWER owner player index.
    /// </summary>
    /// <remarks>
    /// The object-id tie-break settles every real tie on its own (ids are unique), and the
    /// player-index tie-break after it is deliberately unreachable-but-specified: it pins the
    /// ordering as a total order that does not depend on the comparison sort being stable, which
    /// <see cref="List{T}.Sort"/> is not.
    /// </remarks>
    public int CompareTo(AiTargetScore other)
    {
        if (Score != other.Score)
        {
            return other.Score.CompareTo(Score);
        }

        if (Id.Index != other.Id.Index)
        {
            return Id.Index.CompareTo(other.Id.Index);
        }

        return OwnerIndex.CompareTo(other.OwnerIndex);
    }
}

/// <summary>
/// Pure target scoring for the skirmish attack lane. No state, no seams, no orders.
/// </summary>
public static class AiTargetScoring
{
    /// <summary>
    /// Number of distance buckets. Also the multiplier applied to the priority class, which is
    /// what makes any priority difference outrank any proximity difference.
    /// </summary>
    public const int ProximityBuckets = 64;

    /// <summary>
    /// World units per proximity bucket. v1 heuristic: BFME2 world units are roughly a footman
    /// per ten, so ~40 units is a short walk and the 64 buckets cover ~2560 units - about a
    /// skirmish map's diagonal, which is what "beyond this, distance stops mattering" should
    /// mean. TODO S9-11: derive from the map extent or from SkirmishAIData rather than fixing it.
    /// </summary>
    public const float ProximityBucketSize = 40f;

    /// <summary>
    /// Scores one candidate against a wave centre. Returns false when the candidate is not a
    /// legal target at all.
    /// </summary>
    /// <remarks>
    /// The one exclusion that matters is <see cref="AiObjectView.IsHordeMember"/>. Ordering a
    /// wave onto a horde MEMBER is the mirror image of the S9-08 recruitment rule: the member is
    /// a real object with real health, so the order is accepted and looks correct, but the wave
    /// then fights a sub-object of a horde that will simply be replaced, and the horde object
    /// itself - the thing whose death removes the threat - is never targeted. Target the parent
    /// horde, which appears in the same snapshot with <see cref="AiObjectView.IsHorde"/>.
    /// </remarks>
    public static bool TryScore(in AiObjectView candidate, in Vector3 from, out AiTargetScore score)
    {
        score = default;

        var priority = Classify(candidate);

        if (priority == AiAttackPriority.None)
        {
            return false;
        }

        var bucket = Bucket(from, candidate.Position);

        score = new AiTargetScore(
            candidate.Id,
            candidate.OwnerIndex,
            priority,
            bucket,
            ((int)priority * ProximityBuckets) - bucket);

        return true;
    }

    /// <summary>
    /// The priority class of one candidate, or <see cref="AiAttackPriority.None"/> when it is
    /// not a legal target (invalid id, or a horde member - see <see cref="TryScore"/>).
    /// </summary>
    public static AiAttackPriority Classify(in AiObjectView candidate)
    {
        if (candidate.Id.IsInvalid || candidate.IsHordeMember)
        {
            return AiAttackPriority.None;
        }

        if (!candidate.IsStructure)
        {
            // A unit still "under construction" is a unit being trained inside a producer; it is
            // not on the field, so it is not a target either.
            return candidate.IsUnderConstruction ? AiAttackPriority.None : AiAttackPriority.MobileUnit;
        }

        return candidate.IsUnderConstruction ? AiAttackPriority.UnderConstruction : AiAttackPriority.Structure;
    }

    /// <summary>
    /// Picks the best target out of <paramref name="candidates"/>, or null when none is legal.
    /// </summary>
    /// <param name="candidates">Enemy snapshot. Order is irrelevant to the result.</param>
    /// <param name="from">Where the attacking wave currently is.</param>
    /// <remarks>
    /// A single best-of pass rather than a sort: the result is identical (the comparison is a
    /// total order) and it does not allocate, which matters because this runs per wave per
    /// re-scan for the whole match.
    /// </remarks>
    public static AiTargetScore? PickBest(IReadOnlyList<AiObjectView>? candidates, in Vector3 from)
    {
        if (candidates == null)
        {
            return null;
        }

        AiTargetScore? best = null;

        for (var i = 0; i < candidates.Count; i++)
        {
            if (!TryScore(candidates[i], from, out var score))
            {
                continue;
            }

            if (best is null || score.CompareTo(best.Value) < 0)
            {
                best = score;
            }
        }

        return best;
    }

    /// <summary>
    /// Scores every legal candidate into <paramref name="into"/>, best first. For traces, tests
    /// and any later manager that wants a second choice.
    /// </summary>
    public static void ScoreAll(
        IReadOnlyList<AiObjectView>? candidates,
        in Vector3 from,
        List<AiTargetScore> into)
    {
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        if (candidates == null)
        {
            return;
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            if (TryScore(candidates[i], from, out var score))
            {
                into.Add(score);
            }
        }

        into.Sort();
    }

    /// <summary>
    /// The centre of a set of objects, restricted to the ids in <paramref name="ids"/>. Used as
    /// "where the wave is" and, over the AI's own buildings, as the muster point.
    /// </summary>
    /// <remarks>
    /// Summed in the caller's list order and divided once, so it is a fixed float expression
    /// over a fixed set; the AI's id lists are already ascending (AiTeam keeps members sorted),
    /// so the summation order is a function of the SET, which is what determinism needs from a
    /// float sum. Returns <paramref name="fallback"/> when nothing matched.
    /// </remarks>
    public static Vector3 CentreOf(
        IReadOnlyList<AiObjectView>? objects,
        IReadOnlyList<ObjectId>? ids,
        in Vector3 fallback)
    {
        if (objects == null || ids == null || ids.Count == 0)
        {
            return fallback;
        }

        var sum = Vector3.Zero;
        var count = 0;

        for (var i = 0; i < ids.Count; i++)
        {
            for (var o = 0; o < objects.Count; o++)
            {
                if (objects[o].Id == ids[i])
                {
                    sum += objects[o].Position;
                    count++;
                    break;
                }
            }
        }

        return count == 0 ? fallback : sum / count;
    }

    /// <summary>
    /// The centre of every object in <paramref name="objects"/> that passes
    /// <paramref name="predicate"/>. Returns false when nothing matched.
    /// </summary>
    /// <remarks>
    /// Returns a bool rather than a fallback position because callers need to distinguish "the
    /// centre happens to be the origin" from "there was nothing to average", and a sentinel
    /// position cannot express that: a map's playable area can legitimately include (0,0,0).
    /// </remarks>
    public static bool TryCentreOf(
        IReadOnlyList<AiObjectView>? objects,
        Func<AiObjectView, bool> predicate,
        out Vector3 centre)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        centre = Vector3.Zero;

        if (objects == null)
        {
            return false;
        }

        var sum = Vector3.Zero;
        var count = 0;

        for (var i = 0; i < objects.Count; i++)
        {
            if (!predicate(objects[i]))
            {
                continue;
            }

            sum += objects[i].Position;
            count++;
        }

        if (count == 0)
        {
            return false;
        }

        centre = sum / count;
        return true;
    }

    /// <summary>
    /// Quantized planar distance between two points, clamped to
    /// <see cref="ProximityBuckets"/> - 1.
    /// </summary>
    /// <remarks>
    /// Planar (X/Z) on purpose: SAGE height varies across a map for reasons that have nothing to
    /// do with how far an army has to walk, and folding Y in would make a target on a cliff score
    /// as further away than the same target on the flat.
    /// </remarks>
    public static int Bucket(in Vector3 from, in Vector3 to)
    {
        var dx = to.X - from.X;
        var dz = to.Z - from.Z;
        var distance = MathF.Sqrt((dx * dx) + (dz * dz));

        if (!(distance > 0f))
        {
            // Covers both 0 and NaN, deliberately in one test. A NaN coordinate means the
            // snapshot is already broken; what matters here is that the cast below never runs on
            // it, because (int)float.NaN is undefined-shaped rather than merely wrong and would
            // put an arbitrary bucket into a score that is supposed to be reproducible.
            return 0;
        }

        var bucket = (int)(distance / ProximityBucketSize);

        return bucket >= ProximityBuckets ? ProximityBuckets - 1 : bucket;
    }
}
