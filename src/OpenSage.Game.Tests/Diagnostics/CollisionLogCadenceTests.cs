// R15 PROD-FIX, second half of the packet: the collision log throttle.
//
// GameObject.OnCollide wrote one Info line per colliding object PAIR per FRAME. A 3600-frame
// Age of the Ring demo-map gate run produced 5-6 GB of wrapper log in its first ten minutes
// ([HUD-WIRE #5]) - about 40 GB across a three-map gate, i.e. a disk-exhaustion hazard for every
// headed sweep, and enough noise to bury the lines the harness grades from.
//
// Detail is now Debug; Info keeps a periodic summary. NLog is deliberately not a test surface
// here (same reasoning as SimHeartbeatTests), so the cadence is asserted as the pure decision it
// is, exactly like OBS-3's HeartbeatCadence.

using OpenSage.Diagnostics;
using Xunit;

namespace OpenSage.Tests.Diagnostics;

[Collection("CollisionLogCadence")]
public class CollisionLogCadenceTests
{
    public CollisionLogCadenceTests()
    {
        // The counters are process-wide; keep cases independent.
        CollisionLogCadence.ResetForTests();
    }

    [Fact]
    public void FirstCollisionOfARunIsAlwaysLoud()
    {
        // A run that dies early should still leave one "collision handling ran at all" line.
        var summary = CollisionLogCadence.Record(0, everyNFrames: 300);

        Assert.NotNull(summary);
        Assert.Equal(0, summary.Value.Frame);
        Assert.Equal(1, summary.Value.Since);
        Assert.Equal(1, summary.Value.Total);
    }

    [Fact]
    public void CollisionsInsideTheQuietWindowProduceNoInfoLine()
    {
        CollisionLogCadence.Record(0, everyNFrames: 300);

        // This is the whole point: the pathological case is thousands of pairs per frame.
        for (var i = 0; i < 5000; i++)
        {
            Assert.Null(CollisionLogCadence.Record(1, everyNFrames: 300));
        }

        Assert.Null(CollisionLogCadence.Record(299, everyNFrames: 300));
    }

    [Fact]
    public void TheWindowReopensExactlyNFramesAfterTheLastInfoLine()
    {
        CollisionLogCadence.Record(0, everyNFrames: 300);
        CollisionLogCadence.Record(150, everyNFrames: 300);

        // 299 is still inside the window; 300 is the first frame allowed to speak again.
        Assert.Null(CollisionLogCadence.Record(299, everyNFrames: 300));

        var summary = CollisionLogCadence.Record(300, everyNFrames: 300);
        Assert.NotNull(summary);
        Assert.Equal(300, summary.Value.Frame);
    }

    [Fact]
    public void TheSummaryCountsEverythingItCoalesced()
    {
        CollisionLogCadence.Record(0, everyNFrames: 300); // loud: since=1, total=1

        for (var i = 0; i < 9; i++)
        {
            CollisionLogCadence.Record(10, everyNFrames: 300);
        }

        var summary = CollisionLogCadence.Record(300, everyNFrames: 300);

        Assert.NotNull(summary);
        Assert.Equal(10, summary.Value.Since); // the 9 quiet ones plus this one
        Assert.Equal(11, summary.Value.Total); // ...plus the first, loud one
    }

    [Fact]
    public void ACadenceOfOneMakesEveryCollidingFrameLoud()
    {
        Assert.NotNull(CollisionLogCadence.Record(0, everyNFrames: 1));
        Assert.NotNull(CollisionLogCadence.Record(1, everyNFrames: 1));
        Assert.NotNull(CollisionLogCadence.Record(2, everyNFrames: 1));
    }

    [Fact]
    public void ANonPositiveCadenceSilencesTheInfoSummaryEntirely()
    {
        Assert.Null(CollisionLogCadence.Record(0, everyNFrames: 0));
        Assert.Null(CollisionLogCadence.Record(5000, everyNFrames: 0));
        Assert.Null(CollisionLogCadence.Record(5000, everyNFrames: -1));
    }

    [Theory]
    // The first line is always allowed through, whatever the frame number.
    [InlineData(0L, 0L, false, 300, true)]
    [InlineData(9999L, 0L, false, 300, true)]
    // ...after that, strictly the frame gap.
    [InlineData(299L, 0L, true, 300, false)]
    [InlineData(300L, 0L, true, 300, true)]
    [InlineData(301L, 0L, true, 300, true)]
    [InlineData(600L, 300L, true, 300, true)]
    // Disabled.
    [InlineData(9999L, 0L, true, 0, false)]
    public void ShouldEmitAtInfo_IsTheFrameGapRule(
        long frame, long lastInfoFrame, bool hasEmitted, int everyNFrames, bool expected)
    {
        Assert.Equal(expected, CollisionLogCadence.ShouldEmitAtInfo(frame, lastInfoFrame, hasEmitted, everyNFrames));
    }

    [Fact]
    public void TheDefaultCadenceBoundsAGateRunToATractableNumberOfLines()
    {
        // 3600 logic frames at one line per 300 frames is ~12 lines, not ~5 GB. Pinned so a
        // later tune cannot quietly walk this back to per-frame.
        Assert.Equal(300, CollisionLogCadence.DefaultInfoEveryNFrames);
        Assert.True(3600 / CollisionLogCadence.DefaultInfoEveryNFrames <= 20);
    }
}
