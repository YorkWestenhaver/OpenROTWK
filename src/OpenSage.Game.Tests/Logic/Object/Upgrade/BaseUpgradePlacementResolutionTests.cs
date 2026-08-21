// Pure unit tests for BaseUpgrade.ResolvePlacementPosition (BaseUpgradeModuleData spec §5.3
// step 4): the retail candidate[PlacementIndex] branch is a direct, unadjusted array index,
// which is what makes index 0 unreachable (0 < 1 always falls back) and an out-of-range index
// silently fall back rather than fail. No game harness needed - these boundary conditions
// don't depend on a real bone hierarchy, just the index arithmetic itself.

using System.Collections.Generic;
using System.Numerics;
using OpenSage.Graphics;
using OpenSage.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Upgrade;

public class BaseUpgradePlacementResolutionTests
{
    private static readonly Vector3 Fallback = new(11, 22, 33);

    private static IReadOnlyList<(ModelBone Bone, Matrix4x4 WorldTransform)> Candidates(params Vector3[] positions)
    {
        var list = new List<(ModelBone Bone, Matrix4x4 WorldTransform)>();
        for (var i = 0; i < positions.Length; i++)
        {
            var bone = new ModelBone(i, $"upgrade{i:00}", null, Vector3.Zero, Quaternion.Identity);
            list.Add((bone, Matrix4x4.CreateTranslation(positions[i])));
        }
        return list;
    }

    // ---- index 0 is unreachable through the direct branch (0 < 1 always falls back) ----

    [Fact]
    public void IndexZero_FallsBackEvenThoughACandidateExistsAtSlotZero()
    {
        var candidates = Candidates(new Vector3(1, 1, 1), new Vector3(2, 2, 2));

        var result = BaseUpgrade.ResolvePlacementPosition(candidates, placementIndex: 0, fallbackPosition: Fallback);

        Assert.Equal(Fallback, result);
    }

    // ---- negative index falls back ----

    [Fact]
    public void NegativeIndex_FallsBack()
    {
        var candidates = Candidates(new Vector3(1, 1, 1));

        var result = BaseUpgrade.ResolvePlacementPosition(candidates, placementIndex: -1, fallbackPosition: Fallback);

        Assert.Equal(Fallback, result);
    }

    // ---- an in-range index (AotR authors 1 and 5) selects candidate[index] directly ----

    [Fact]
    public void InRangeIndex_SelectsTheDirectArraySlot()
    {
        var candidates = Candidates(
            new Vector3(1, 1, 1),   // slot 0 - unreachable, see IndexZero test
            new Vector3(2, 2, 2),   // slot 1
            new Vector3(3, 3, 3));  // slot 2

        var result = BaseUpgrade.ResolvePlacementPosition(candidates, placementIndex: 1, fallbackPosition: Fallback);

        Assert.Equal(new Vector3(2, 2, 2), result);
    }

    [Fact]
    public void InRangeIndex_MatchingAotRsSecondBaseUpgradeTag()
    {
        var candidates = Candidates(
            new Vector3(0, 0, 0),
            new Vector3(1, 0, 0),
            new Vector3(2, 0, 0),
            new Vector3(3, 0, 0),
            new Vector3(4, 0, 0),
            new Vector3(5, 0, 0));

        // AotR's MordorBase's second BaseUpgrade tag authors PlacementIndex = 5.
        var result = BaseUpgrade.ResolvePlacementPosition(candidates, placementIndex: 5, fallbackPosition: Fallback);

        Assert.Equal(new Vector3(5, 0, 0), result);
    }

    // ---- index == match count falls back (off-by-one at the top end) ----

    [Fact]
    public void IndexEqualToMatchCount_FallsBack()
    {
        var candidates = Candidates(new Vector3(1, 1, 1), new Vector3(2, 2, 2));

        var result = BaseUpgrade.ResolvePlacementPosition(candidates, placementIndex: 2, fallbackPosition: Fallback);

        Assert.Equal(Fallback, result);
    }

    // ---- index far past match count falls back (a mod authoring a bad PlacementIndex) ----

    [Fact]
    public void IndexPastMatchCount_FallsBackRatherThanThrowing()
    {
        var candidates = Candidates(new Vector3(1, 1, 1));

        var result = BaseUpgrade.ResolvePlacementPosition(candidates, placementIndex: 99, fallbackPosition: Fallback);

        Assert.Equal(Fallback, result);
    }

    // ---- no candidates at all (no matching bones, or no PlacementPrefix authored) ----

    [Fact]
    public void EmptyCandidateList_FallsBack()
    {
        var result = BaseUpgrade.ResolvePlacementPosition([], placementIndex: 1, fallbackPosition: Fallback);

        Assert.Equal(Fallback, result);
    }
}
