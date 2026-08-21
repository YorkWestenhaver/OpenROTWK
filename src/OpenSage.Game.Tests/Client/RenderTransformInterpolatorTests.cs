// R14 packet 6 (workbench research/design-sim-presentation-bridge.md §1.6, §2 packet 6).
//
// Render-free: RenderTransformInterpolator is pure presentation math over Transform and
// LogicFrame, both of which construct without a graphics device, a game installation or any
// asset. No MockedGameTest, no GameFixture, no [GameFact] - these run everywhere CI runs.
//
// What is being pinned:
//   * the picture lags the sim by exactly one logic frame (interpolate, never extrapolate),
//   * the pose history only advances when the LOGIC frame changes, not per render frame,
//   * discontinuities snap instead of smearing: first sight, a skipped frame, a rewound
//     logic clock (GameLogic.Reset), and a translation jump at or beyond SnapDistance,
//   * the composed matrix matches Transform's own composition order, so it is a drop-in
//     replacement on the render path.

using System;
using System.Numerics;
using OpenSage.Client;
using OpenSage.Mathematics;
using Xunit;

namespace OpenSage.Tests.Client;

public class RenderTransformInterpolatorTests
{
    private const float Tolerance = 1e-4f;

    /// <summary>
    /// Ordinary one-frame motion for the tests that are about interpolation math rather than
    /// about the teleport guard. Derived from SnapDistance instead of written as a literal:
    /// SnapDistance is 10 * HeightMap.HorizontalScale = 100 world units exactly, so a literal
    /// 100-unit step silently lands ON the >= boundary and snaps, which makes a lerp test pass
    /// or fail for a reason it never meant to exercise. The guard itself is pinned separately
    /// by TeleportBeyondSnapDistance_Snaps and MotionBelowSnapDistance_StillInterpolates.
    /// </summary>
    private const float Step = RenderTransformInterpolator.SnapDistance / 2f;

    private static Transform At(float x, float y = 0f, float z = 0f) =>
        new Transform(new Vector3(x, y, z), Quaternion.Identity);

    private static Transform Yawed(float yawRadians) =>
        new Transform(Vector3.Zero, QuaternionUtility.CreateFromYawPitchRoll_ZUp(yawRadians, 0, 0));

    private static void AssertClose(in Vector3 expected, in Vector3 actual)
    {
        Assert.True(
            Vector3.Distance(expected, actual) < Tolerance,
            $"expected {expected}, got {actual}");
    }

    [Fact]
    public void BeforeAnyObservation_HasNoSample()
    {
        var interpolator = new RenderTransformInterpolator();

        // The caller's fallback contract: an object created inside a logic frame can be
        // rendered before the presentation path has ever seen it, and must draw at its raw
        // sim transform rather than at the world origin.
        Assert.False(interpolator.HasSample);
    }

    [Fact]
    public void FirstObservation_PrimesBothPoses_SoNothingLerpsInFromTheOrigin()
    {
        var interpolator = new RenderTransformInterpolator();

        interpolator.Observe(new LogicFrame(7), At(100f, 200f, 5f), tickT: 0f);

        Assert.True(interpolator.HasSample);
        Assert.Equal(7u, interpolator.SampledFrame.Value);

        // Both ends of the fraction give the sampled pose: there is no history to lerp from.
        AssertClose(new Vector3(100f, 200f, 5f), interpolator.Translation);

        interpolator.Observe(new LogicFrame(7), At(100f, 200f, 5f), tickT: 1f);
        AssertClose(new Vector3(100f, 200f, 5f), interpolator.Translation);
    }

    [Fact]
    public void SecondFrame_InterpolatesFromPreviousTowardCurrent()
    {
        var interpolator = new RenderTransformInterpolator();

        interpolator.Observe(new LogicFrame(1), At(0f), tickT: 0f);
        interpolator.Observe(new LogicFrame(2), At(Step), tickT: 0f);

        // tickT = 0 -> the PREVIOUS frame's pose. This is the one-frame display lag, and it
        // is deliberate: extrapolating forward from a 5 Hz sample overshoots every stop.
        AssertClose(new Vector3(0f, 0f, 0f), interpolator.Translation);

        interpolator.Observe(new LogicFrame(2), At(Step), tickT: 0.25f);
        AssertClose(new Vector3(Step * 0.25f, 0f, 0f), interpolator.Translation);

        interpolator.Observe(new LogicFrame(2), At(Step), tickT: 0.5f);
        AssertClose(new Vector3(Step * 0.5f, 0f, 0f), interpolator.Translation);

        interpolator.Observe(new LogicFrame(2), At(Step), tickT: 1f);
        AssertClose(new Vector3(Step, 0f, 0f), interpolator.Translation);
    }

    [Fact]
    public void SteadyMotion_IsContinuousAcrossTheFrameBoundary()
    {
        // The seam is the interesting part: tickT ~= 1 on the last render frame before a
        // logic tick must draw (very nearly) what tickT = 0 draws on the first render frame
        // after it. If it did not, packet 6 would trade a 5 Hz snap for a 5 Hz stutter.
        var interpolator = new RenderTransformInterpolator();

        interpolator.Observe(new LogicFrame(1), At(0f), tickT: 0f);
        interpolator.Observe(new LogicFrame(2), At(10f), tickT: 1f);
        var beforeTick = interpolator.Translation;

        interpolator.Observe(new LogicFrame(3), At(20f), tickT: 0f);
        var afterTick = interpolator.Translation;

        AssertClose(new Vector3(10f, 0f, 0f), beforeTick);
        AssertClose(beforeTick, afterTick);
    }

    [Fact]
    public void RepeatedObservationInsideOneLogicFrame_AdvancesTickTButNotTheHistory()
    {
        // Observe() is called once per RENDER frame - roughly twelve times per logic frame at
        // 60 fps. Only the logic-frame edge may roll current into previous.
        var interpolator = new RenderTransformInterpolator();

        interpolator.Observe(new LogicFrame(1), At(0f), tickT: 0f);
        interpolator.Observe(new LogicFrame(2), At(Step), tickT: 0f);

        for (var i = 0; i <= 10; i++)
        {
            var t = i / 10f;
            interpolator.Observe(new LogicFrame(2), At(Step), t);
            Assert.Equal(t, interpolator.TickT, 4);
            AssertClose(new Vector3(Step * t, 0f, 0f), interpolator.Translation);
        }

        Assert.Equal(2u, interpolator.SampledFrame.Value);
    }

    [Fact]
    public void SkippedLogicFrame_Snaps()
    {
        // A gap means the object was not observed last frame, so the retained "previous" pose
        // is stale and lerping from it would smear.
        var interpolator = new RenderTransformInterpolator();

        interpolator.Observe(new LogicFrame(1), At(0f), tickT: 0f);
        interpolator.Observe(new LogicFrame(5), At(50f), tickT: 0f);

        AssertClose(new Vector3(50f, 0f, 0f), interpolator.Translation);
        Assert.Equal(5u, interpolator.SampledFrame.Value);
    }

    [Fact]
    public void RewoundLogicClock_Snaps()
    {
        // GameLogic.Reset() re-zeros the logic clock (design §4.2: scene construction wipes
        // the object list). A backwards step must not be mistaken for "no new frame".
        var interpolator = new RenderTransformInterpolator();

        interpolator.Observe(new LogicFrame(40), At(0f), tickT: 0f);
        interpolator.Observe(new LogicFrame(41), At(10f), tickT: 0f);
        interpolator.Observe(new LogicFrame(0), At(900f), tickT: 0f);

        AssertClose(new Vector3(900f, 0f, 0f), interpolator.Translation);
        Assert.Equal(0u, interpolator.SampledFrame.Value);
    }

    [Fact]
    public void TeleportBeyondSnapDistance_Snaps()
    {
        var interpolator = new RenderTransformInterpolator();

        interpolator.Observe(new LogicFrame(1), At(0f), tickT: 0f);
        interpolator.Observe(new LogicFrame(2), At(RenderTransformInterpolator.SnapDistance + 1f), tickT: 0f);

        // No smear across half the map: at tickT = 0 it is already at the destination.
        AssertClose(new Vector3(RenderTransformInterpolator.SnapDistance + 1f, 0f, 0f), interpolator.Translation);
    }

    [Fact]
    public void MotionBelowSnapDistance_StillInterpolates()
    {
        // The guard must not swallow ordinary fast movement. Half the snap distance in one
        // frame is 250 world units/second, well above any locomotor.
        var interpolator = new RenderTransformInterpolator();
        var step = RenderTransformInterpolator.SnapDistance / 2f;

        interpolator.Observe(new LogicFrame(1), At(0f), tickT: 0f);
        interpolator.Observe(new LogicFrame(2), At(step), tickT: 0.5f);

        AssertClose(new Vector3(step / 2f, 0f, 0f), interpolator.Translation);
    }

    [Fact]
    public void Rotation_IsInterpolated()
    {
        var interpolator = new RenderTransformInterpolator();

        interpolator.Observe(new LogicFrame(1), Yawed(0f), tickT: 0f);
        interpolator.Observe(new LogicFrame(2), Yawed(MathF.PI / 2f), tickT: 0.5f);

        var expected = QuaternionUtility.CreateFromYawPitchRoll_ZUp(MathF.PI / 4f, 0, 0);
        var actual = interpolator.Rotation;

        // Compare on the shorter arc: q and -q are the same orientation.
        Assert.True(MathF.Abs(Quaternion.Dot(expected, actual)) > 1f - Tolerance);
    }

    [Fact]
    public void Scale_IsInterpolated()
    {
        var interpolator = new RenderTransformInterpolator();

        interpolator.Observe(new LogicFrame(1), new Transform(Vector3.Zero, Quaternion.Identity, 1f), tickT: 0f);
        interpolator.Observe(new LogicFrame(2), new Transform(Vector3.Zero, Quaternion.Identity, 3f), tickT: 0.5f);

        Assert.Equal(2f, interpolator.Scale, 4);
    }

    [Fact]
    public void TickT_IsClampedToUnitRange()
    {
        // Game.LocalLogicTick clamps the overdue side but not a paused or rewound clock, so
        // the interpolator owns the clamp.
        var interpolator = new RenderTransformInterpolator();

        interpolator.Observe(new LogicFrame(1), At(0f), tickT: 0f);

        interpolator.Observe(new LogicFrame(2), At(Step), tickT: -3f);
        Assert.Equal(0f, interpolator.TickT);
        AssertClose(new Vector3(0f, 0f, 0f), interpolator.Translation);

        interpolator.Observe(new LogicFrame(2), At(Step), tickT: 7f);
        Assert.Equal(1f, interpolator.TickT);
        AssertClose(new Vector3(Step, 0f, 0f), interpolator.Translation);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonFiniteTickT_DegradesToTheNewestPose_NotToNaN(float tickT)
    {
        // NaN would propagate through the lerp into the world matrix and the object would stop
        // being drawn at all. Falling back to 1 reproduces the old 5 Hz snap instead - bad, but
        // visible.
        var interpolator = new RenderTransformInterpolator();

        // Step, not a literal 100: at exactly SnapDistance the teleport guard collapses
        // previous onto current, and then EVERY tickT yields the newest pose - so the
        // assertion below would hold even if the non-finite fallback were broken.
        interpolator.Observe(new LogicFrame(1), At(0f), tickT: 0f);
        interpolator.Observe(new LogicFrame(2), At(Step), tickT);

        Assert.Equal(1f, interpolator.TickT);
        AssertClose(new Vector3(Step, 0f, 0f), interpolator.Translation);
        Assert.False(float.IsNaN(interpolator.Matrix.M41));
    }

    [Fact]
    public void Matrix_MatchesTransformsOwnComposition()
    {
        // Drop-in replacement contract for GameObject.BuildRenderList, which used to multiply
        // by Transform.Matrix directly.
        var interpolator = new RenderTransformInterpolator();
        var pose = new Transform(
            new Vector3(12f, -34f, 56f),
            QuaternionUtility.CreateFromYawPitchRoll_ZUp(0.75f, 0, 0),
            2.5f);

        interpolator.Observe(new LogicFrame(1), pose, tickT: 0f);

        var expected = pose.Matrix;
        var actual = interpolator.Matrix;

        Assert.True(MatricesClose(expected, actual), $"expected {expected}, got {actual}");
    }

    [Fact]
    public void Matrix_IsRecomputedWhenTickTMoves()
    {
        // The matrix is lazily cached behind a dirty flag (same pattern as Transform). A stale
        // cache would pin every object to the previous frame's pose - the very bug packet 6
        // exists to remove.
        var interpolator = new RenderTransformInterpolator();

        interpolator.Observe(new LogicFrame(1), At(0f), tickT: 0f);
        interpolator.Observe(new LogicFrame(2), At(Step), tickT: 0f);
        var atStart = interpolator.Matrix;

        interpolator.Observe(new LogicFrame(2), At(Step), tickT: 1f);
        var atEnd = interpolator.Matrix;

        AssertClose(new Vector3(0f, 0f, 0f), atStart.Translation);
        AssertClose(new Vector3(Step, 0f, 0f), atEnd.Translation);
    }

    [Fact]
    public void Observe_RejectsANullTransform()
    {
        var interpolator = new RenderTransformInterpolator();

        Assert.Throws<ArgumentNullException>(
            () => interpolator.Observe(LogicFrame.Zero, null, tickT: 0f));
    }

    private static bool MatricesClose(in Matrix4x4 a, in Matrix4x4 b)
    {
        return MathF.Abs(a.M11 - b.M11) < Tolerance && MathF.Abs(a.M12 - b.M12) < Tolerance
            && MathF.Abs(a.M13 - b.M13) < Tolerance && MathF.Abs(a.M14 - b.M14) < Tolerance
            && MathF.Abs(a.M21 - b.M21) < Tolerance && MathF.Abs(a.M22 - b.M22) < Tolerance
            && MathF.Abs(a.M23 - b.M23) < Tolerance && MathF.Abs(a.M24 - b.M24) < Tolerance
            && MathF.Abs(a.M31 - b.M31) < Tolerance && MathF.Abs(a.M32 - b.M32) < Tolerance
            && MathF.Abs(a.M33 - b.M33) < Tolerance && MathF.Abs(a.M34 - b.M34) < Tolerance
            && MathF.Abs(a.M41 - b.M41) < Tolerance && MathF.Abs(a.M42 - b.M42) < Tolerance
            && MathF.Abs(a.M43 - b.M43) < Tolerance && MathF.Abs(a.M44 - b.M44) < Tolerance;
    }
}
