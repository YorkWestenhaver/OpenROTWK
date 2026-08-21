// L1-05 (A1-G9): the periodic sim heartbeat HeadedSimSystems.OnPhase emits at EndFrame.
//
// The heartbeat has two independent outputs: an NLog debug line (not asserted here - NLog
// output isn't a stable test surface) and, whenever a GameTrace session is active, a "SimTrace
// instant event carrying the same loop-frame/logic-frame/render-frame/logic-FPS snapshot as
// text. GameTrace's own JSON file is the one observable, file-based surface both outputs share
// a cadence with, so these tests drive it directly: start a real trace session against a temp
// file, advance HeadlessSimGame's loop, stop the session, and parse back the "i" (instant)
// entries.
//
// T+1 frame convention: SimLoop.Advance() observes EndFrame for loop frame N on its (N+1)-th
// call (frame counters agree at boundaries; see HeadedSimSystemsTests). To see the heartbeat
// fire at loop frames 0, 2, and 4 (interval 2), the loop needs 5 Advance() calls.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenSage.Diagnostics;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Sim;

[Collection(GameTraceCollection.Name)]
public class SimHeartbeatTests : IDisposable
{
    private readonly string _traceFilePath =
        Path.Combine(Path.GetTempPath(), $"simheartbeat-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        // Defensive: a test that fails mid-session could leave GameTrace.Start()ed. Stop() is
        // safe to call again here (no-op risk is a leftover file, not a crash) since the
        // shared static only needs Output nulled once.
        if (GameTrace.IsTracing)
        {
            GameTrace.Stop();
        }

        if (File.Exists(_traceFilePath))
        {
            File.Delete(_traceFilePath);
        }
    }

    private static (SimLoop Loop, HeadlessSimGame Game) CreateLoop(int heartbeatIntervalInFrames)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: 0x81D6E);
        game.Configuration.SimHeartbeatIntervalInFrames = heartbeatIntervalInFrames;

        var systems = new HeadedSimSystems(game);
        var loop = new SimLoop(systems, systems)
        {
            CrcCheckpointIntervalInFrames = 0,
        };
        return (loop, game);
    }

    /// <summary>Reads back the "i" (instant) event names GameTrace wrote, in order.</summary>
    private List<string> ReadInstantEventNames()
    {
        using var stream = File.OpenRead(_traceFilePath);
        using var doc = JsonDocument.Parse(stream);

        return doc.RootElement.EnumerateArray()
            .Where(e => e.GetProperty("ph").GetString() == "i")
            .Select(e => e.GetProperty("name").GetString())
            .ToList();
    }

    [Fact]
    public void FiresAtFrameZeroAndOnEveryConfiguredIntervalWhenTracing()
    {
        var (loop, _) = CreateLoop(heartbeatIntervalInFrames: 2);

        GameTrace.Start(_traceFilePath);
        for (var i = 0; i < 5; i++)
        {
            loop.Advance();
        }
        GameTrace.Stop();

        var names = ReadInstantEventNames();

        // Loop frames 0, 1, 2, 3, 4 were observed (T+1 for 5 calls); interval 2 fires at 0, 2, 4.
        Assert.Equal(3, names.Count);
        Assert.All(names, n => Assert.StartsWith("SimHeartbeat ", n));
        Assert.Contains("loopFrame=0 ", names[0]);
        Assert.Contains("loopFrame=2 ", names[1]);
        Assert.Contains("loopFrame=4 ", names[2]);

        // GameLogic.CurrentFrame reads one ahead of the loop's at EndFrame (see
        // HeadedSimSystemsTests.FrameCountersAgreeAtBoundariesAndDifferInsideAFrame).
        Assert.Contains("logicFrame=1 ", names[0]);
        Assert.Contains("logicFrame=3 ", names[1]);
        Assert.Contains("logicFrame=5 ", names[2]);

        // HeadlessSimGame never drives Update(), so the render-frame counter stays at 0 -
        // proving the field is threaded through, not hardcoded into the message.
        Assert.All(names, n => Assert.Contains("renderFrame=0 ", n));
    }

    [Fact]
    public void IntervalOfZeroDisablesTheHeartbeatEntirely()
    {
        var (loop, _) = CreateLoop(heartbeatIntervalInFrames: 0);

        GameTrace.Start(_traceFilePath);
        for (var i = 0; i < 5; i++)
        {
            loop.Advance();
        }
        GameTrace.Stop();

        Assert.Empty(ReadInstantEventNames());
    }

    [Fact]
    public void ANegativeIntervalAlsoDisablesTheHeartbeat()
    {
        var (loop, _) = CreateLoop(heartbeatIntervalInFrames: -1);

        GameTrace.Start(_traceFilePath);
        loop.Advance();
        loop.Advance();
        GameTrace.Stop();

        Assert.Empty(ReadInstantEventNames());
    }

    [Fact]
    public void ProducesNoTraceEventsWhenNoTraceSessionIsActive()
    {
        // GameTrace.IsTracing is false here (no Start() call): the heartbeat must not throw,
        // and must not create a trace file, since nothing is tracing.
        var (loop, _) = CreateLoop(heartbeatIntervalInFrames: 1);

        loop.Advance();
        loop.Advance();
        loop.Advance();

        Assert.False(GameTrace.IsTracing);
        Assert.False(File.Exists(_traceFilePath));
    }

    [Fact]
    public void ASecondTraceSessionInTheSameProcessProducesValidJson()
    {
        // GameTrace's Output/WrittenFirstEntry are process-wide statics; a prior session that
        // didn't reset WrittenFirstEntry would make Start() begin the array with a stray
        // leading comma. Two back-to-back sessions catch that regression directly.
        var (loop, _) = CreateLoop(heartbeatIntervalInFrames: 1);

        GameTrace.Start(_traceFilePath);
        loop.Advance();
        GameTrace.Stop();
        var firstSessionCount = ReadInstantEventNames().Count;

        GameTrace.Start(_traceFilePath);
        loop.Advance();
        GameTrace.Stop();
        var secondSessionCount = ReadInstantEventNames().Count;

        Assert.Equal(1, firstSessionCount);
        Assert.Equal(1, secondSessionCount);
    }
}
