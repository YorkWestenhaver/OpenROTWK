// OBS-3: the Info-echo cadence for sim heartbeats.
//
// The heartbeat's Debug line is unconditional and unchanged (SimHeartbeatTests pins the
// heartbeat's firing schedule and message shape via GameTrace). What is new here is which of
// those heartbeats is ALSO loud enough to reach the console/wrapper log. NLog output is not a
// stable test surface, so the decision itself lives in HeartbeatCadence and is tested directly.

using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Sim;

public class HeartbeatCadenceTests
{
    [Fact]
    public void TheFirstHeartbeatIsAlwaysLoudSoAnEarlyDeathStillProvesTheSimRan()
    {
        Assert.True(HeartbeatCadence.ShouldEmitAtInfo(heartbeatOrdinal: 1, everyNth: 10));
    }

    [Theory]
    // everyNth = 10: loud at 1, 11, 21; quiet everywhere between.
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(9, false)]
    [InlineData(10, false)]
    [InlineData(11, true)]
    [InlineData(12, false)]
    [InlineData(20, false)]
    [InlineData(21, true)]
    public void EveryNthHeartbeatCountingFromTheFirstIsLoud(long ordinal, bool expectedLoud)
    {
        Assert.Equal(expectedLoud, HeartbeatCadence.ShouldEmitAtInfo(ordinal, everyNth: 10));
    }

    [Fact]
    public void AnEveryNthOfOneMakesEveryHeartbeatLoud()
    {
        for (long ordinal = 1; ordinal <= 5; ordinal++)
        {
            Assert.True(HeartbeatCadence.ShouldEmitAtInfo(ordinal, everyNth: 1));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void ANonPositiveEveryNthDisablesTheInfoEchoEntirely(int everyNth)
    {
        // Including ordinal 1: "disabled" must mean silent, not "silent after the first".
        Assert.False(HeartbeatCadence.ShouldEmitAtInfo(heartbeatOrdinal: 1, everyNth));
        Assert.False(HeartbeatCadence.ShouldEmitAtInfo(heartbeatOrdinal: 2, everyNth));
        Assert.False(HeartbeatCadence.ShouldEmitAtInfo(heartbeatOrdinal: 100, everyNth));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveOrdinalIsNeverLoud(long ordinal)
    {
        // The counter is incremented before the check, so this is defensive only - but a
        // negative ordinal must not fall through to a negative modulo result of 0 and go loud.
        Assert.False(HeartbeatCadence.ShouldEmitAtInfo(ordinal, everyNth: 1));
        Assert.False(HeartbeatCadence.ShouldEmitAtInfo(ordinal, everyNth: 10));
    }

    [Fact]
    public void TheDefaultConfigurationEchoesOneHeartbeatInTen()
    {
        // Pins the default so a harness reading the wrapper log knows the expected density
        // (one Info heartbeat per 10 * SimHeartbeatIntervalInFrames logic frames).
        Assert.Equal(10, new Configuration().SimHeartbeatInfoEveryNth);
        Assert.Equal(50, new Configuration().SimHeartbeatIntervalInFrames);
    }
}
