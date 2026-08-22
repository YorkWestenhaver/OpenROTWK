#nullable enable

// OBS-2 (R15 R1-FIX2) gate tests.
//
// Two things are under test here, and they are one packet because they are one failure story:
// the R1 sweep's crashes were anonymous (no frame, no object, no map object) AND crashing runs
// wrote no --ai-report at all, so the gate had nothing to grade.
//
//  * CrashContextTests            - the ambient context and the CRASH-CONTEXT-V1 line format.
//  * AiMatchReportPartialFlushTests - the report is still produced on the crash/teardown path,
//                                     marked partial=true, and a clean report still is not.
//
// Scoped filter for both classes:
//   FullyQualifiedName~CrashContext|FullyQualifiedName~AiMatchReportPartialFlush

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using OpenSage.Diagnostics;
using OpenSage.Logic.AI.Skirmish;
using OpenSage.Tests.Logic.AI.Skirmish;
using Xunit;

namespace OpenSage.Tests.Diagnostics;

public class CrashContextTests : IDisposable
{
    public CrashContextTests() => CrashContext.Reset();

    public void Dispose() => CrashContext.Reset();

    [Fact]
    public void Describe_WithNothingPushed_SaysSoRatherThanLying()
    {
        Assert.Equal("(no context)", CrashContext.Describe());
        Assert.Equal(0, CrashContext.Depth);
    }

    [Fact]
    public void Describe_RendersFramesOutermostFirst()
    {
        using var frame = CrashContext.Push("frame", 127L);
        using var obj = CrashContext.Push("object", "GondorWorker", 48L);
        using var module = CrashContext.Push("module", "DozerAndWorkerState");

        Assert.Equal("frame=127 | object=#48 GondorWorker | module=DozerAndWorkerState", CrashContext.Describe());
    }

    [Fact]
    public void Scope_PopsOnDispose_AndUnwindsCorrectlyWhenAnExceptionPassesThrough()
    {
        using (CrashContext.Push("frame", 5L))
        {
            Assert.Equal(1, CrashContext.Depth);

            try
            {
                using (CrashContext.Push("object", "Thing", 1L))
                {
                    Assert.Equal(2, CrashContext.Depth);
                    throw new InvalidOperationException("boom");
                }
            }
            catch (InvalidOperationException)
            {
                // The inner scope's Dispose ran during unwind, so only the frame remains.
            }

            Assert.Equal(1, CrashContext.Depth);
            Assert.Equal("frame=5", CrashContext.Describe());
        }

        Assert.Equal(0, CrashContext.Depth);
    }

    [Fact]
    public void Push_BeyondMaxDepth_DoesNotThrowAndStillUnwindsToZero()
    {
        // A runaway recursion must not turn "we crashed" into "we crashed inside the crash
        // reporter". Depth keeps counting; only the first MaxDepth entries are recorded.
        var scopes = new List<CrashContext.Scope>();
        for (var i = 0; i < 200; i++)
        {
            scopes.Add(CrashContext.Push("deep", i));
        }

        Assert.Equal(200, CrashContext.Depth);
        Assert.Contains("deep=0", CrashContext.Describe());

        for (var i = scopes.Count - 1; i >= 0; i--)
        {
            scopes[i].Dispose();
        }

        Assert.Equal(0, CrashContext.Depth);
    }

    [Fact]
    public void FormatCrashLine_IsOneSingleLine_StartingWithTheMarker()
    {
        using var frame = CrashContext.Push("frame", 127L);
        using var obj = CrashContext.Push("object", "GondorWorker", 48L);

        Exception caught;
        try
        {
            throw new NullReferenceException("Object reference not set to an instance of an object.");
        }
        catch (NullReferenceException ex)
        {
            caught = ex;
        }

        var line = CrashContext.FormatCrashLine(caught, "game-loop");

        Assert.StartsWith("CRASH-CONTEXT-V1 {", line);
        // One record per crash: a multi-line record cannot be attributed in an interleaved log.
        Assert.DoesNotContain("\n", line);
        Assert.DoesNotContain("\r", line);
    }

    [Fact]
    public void FormatCrashLine_JsonNamesThePhaseExceptionContextAndStack()
    {
        using var frame = CrashContext.Push("frame", 127L);
        using var obj = CrashContext.Push("object", "GondorWorker", 48L);
        using var module = CrashContext.Push("module", "DozerAndWorkerState");

        Exception caught;
        try
        {
            throw new NullReferenceException("nre-marker-text");
        }
        catch (NullReferenceException ex)
        {
            caught = ex;
        }

        var line = CrashContext.FormatCrashLine(caught, "game-loop");
        var json = line.Substring(CrashContext.LineMarker.Length + 1);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("game-loop", root.GetProperty("phase").GetString());
        Assert.Equal("System.NullReferenceException", root.GetProperty("exceptionType").GetString());
        Assert.Equal("nre-marker-text", root.GetProperty("message").GetString());
        Assert.Equal(
            "frame=127 | object=#48 GondorWorker | module=DozerAndWorkerState",
            root.GetProperty("context").GetString());

        // The managed stack rides inside the same record, escaped, so the wrapper log keeps it.
        Assert.Contains("nre-marker-text", root.GetProperty("stack").GetString());

        var frames = root.GetProperty("frames");
        Assert.Equal(3, frames.GetArrayLength());
        Assert.Equal("frame", frames[0].GetProperty("key").GetString());
        Assert.Equal("127", frames[0].GetProperty("value").GetString());
        Assert.Equal("object", frames[1].GetProperty("key").GetString());
        Assert.Equal("#48 GondorWorker", frames[1].GetProperty("value").GetString());
    }

    [Fact]
    public void FormatCrashLine_EscapesQuotesAndNewlinesInValues_SoTheRecordStaysParseable()
    {
        using var scope = CrashContext.Push("mapObject", "quote\"and\nnewline");

        var line = CrashContext.FormatCrashLine(new Exception("a\"b"), "map-load");

        Assert.DoesNotContain("\n", line);

        using var document = JsonDocument.Parse(line.Substring(CrashContext.LineMarker.Length + 1));
        Assert.Contains("quote\"and\nnewline", document.RootElement.GetProperty("context").GetString());
        Assert.Equal("a\"b", document.RootElement.GetProperty("message").GetString());
    }

    // ---- throw-time snapshot ----
    //
    // The defect this closes was found in this packet's own verification run: the first
    // implementation formatted the LIVE context in the catch block, by which point every
    // `using` scope the exception unwound through had disposed, and the record read
    // "(no context)" on a real frame-127 crash. Context must be frozen at the throw site.

    [Fact]
    public void DescribeFor_AfterTheScopesUnwound_StillReportsTheContextAsOfTheThrow()
    {
        Exception caught;
        try
        {
            using (CrashContext.Push("frame", 127L))
            using (CrashContext.Push("object", "GondorWorker", 48L))
            using (CrashContext.Push("module", "DozerAndWorkerState"))
            {
                try
                {
                    throw new NullReferenceException("unwind me");
                }
                catch (Exception ex)
                {
                    // Stand in for AppDomain.FirstChanceException, which the launcher wires to
                    // this method: it runs at the throw site, before any scope disposes.
                    CrashContext.CaptureThrowSnapshot(ex);
                    throw;
                }
            }
        }
        catch (NullReferenceException ex)
        {
            caught = ex;
        }

        // Live context is empty here - that is the whole problem.
        Assert.Equal(0, CrashContext.Depth);
        Assert.Equal("(no context)", CrashContext.Describe());

        Assert.Equal(
            "frame=127 | object=#48 GondorWorker | module=DozerAndWorkerState",
            CrashContext.DescribeFor(caught));

        using var document = JsonDocument.Parse(
            CrashContext.FormatCrashLine(caught, "game-loop").Substring(CrashContext.LineMarker.Length + 1));
        Assert.Equal(3, document.RootElement.GetProperty("frames").GetArrayLength());
    }

    [Fact]
    public void DescribeFor_DoesNotLendAnEarlierHandledThrowsContextToALaterCrash()
    {
        // Handled throws are routine during asset load; their stale context must never be
        // attributed to a different exception that crashes the process later.
        using (CrashContext.Push("mapObject", "SomeAssetBeingLoaded", 3L))
        {
            CrashContext.CaptureThrowSnapshot(new InvalidOperationException("handled, recovered"));
        }

        var laterCrash = new NullReferenceException("unrelated");
        Assert.Equal("(no context)", CrashContext.DescribeFor(laterCrash));
    }

    [Fact]
    public void DescribeFor_MatchesTheSnapshotThroughAnInnerExceptionWrapper()
    {
        var inner = new NullReferenceException("inner");
        using (CrashContext.Push("frame", 9L))
        {
            CrashContext.CaptureThrowSnapshot(inner);
        }

        var wrapper = new InvalidOperationException("wrapped", inner);
        Assert.Equal("frame=9", CrashContext.DescribeFor(wrapper));
    }

    [Fact]
    public void FormatCrashLine_WithNoExceptionAndNoContext_StillEmitsAValidRecord()
    {
        var line = CrashContext.FormatCrashLine(null, null);

        using var document = JsonDocument.Parse(line.Substring(CrashContext.LineMarker.Length + 1));
        Assert.Equal("unknown", document.RootElement.GetProperty("phase").GetString());
        Assert.Equal("(none)", document.RootElement.GetProperty("exceptionType").GetString());
        Assert.Equal("(no context)", document.RootElement.GetProperty("context").GetString());
    }
}

/// <summary>
/// OBS-2's instrument fix: --ai-report must also be written when the run dies, marked partial.
/// </summary>
public class AiMatchReportPartialFlushTests
{
    private static SkirmishAIBrain NewBrain(int playerIndex) =>
        new(new FakeAiWorldView { PlayerIndex = playerIndex }, new RecordingOrderSink(),
            new AiTrace(playerIndex, new RecordingAiTraceSink()), heartbeatInterval: 1);

    private static FakeAiWorldView WorldOf(SkirmishAIBrain brain) => (FakeAiWorldView)brain.World;

    [Fact]
    public void CleanReport_IsNotMarkedPartial()
    {
        var brain = NewBrain(0);
        var start = AiMatchReport.CaptureAll(new[] { brain });
        var end = AiMatchReport.CaptureAll(new[] { brain });

        var report = AiMatchReport.Build(start, end);

        Assert.False(report.Partial);
        Assert.Contains("\"partial\":false", report.ToJson());
    }

    [Fact]
    public void BuildPartial_MarksTheReportPartial_AndStillGradesWhatTheAiDidAchieve()
    {
        var brain = NewBrain(1);
        var world = WorldOf(brain);
        world.Money = 800;

        var start = AiMatchReport.CaptureAll(new[] { brain });

        // The run got as far as one heartbeat, some income and a placed foundation, then died.
        brain.Update();
        world.Money = 900;
        brain.Trace.Count(AiMatchReport.FoundationConstructCounter);

        var report = AiMatchReport.BuildPartial(start, () => AiMatchReport.CaptureAll(new[] { brain }));

        Assert.True(report.Partial);
        Assert.True(report.MilestoneA);
        Assert.True(report.MilestoneB);
        Assert.Contains("\"partial\":true", report.ToJson());
    }

    [Fact]
    public void BuildPartial_WhenTheEndCaptureItselfThrows_DegradesToStartSnapshotsInsteadOfLosingTheReport()
    {
        var brain = NewBrain(3);
        WorldOf(brain).Money = 700;
        brain.Trace.Count(AiMatchReport.FoundationConstructCounter);

        var start = AiMatchReport.CaptureAll(new[] { brain });

        Exception? reported = null;
        var report = AiMatchReport.BuildPartial(
            start,
            () => throw new InvalidOperationException("world is gone"),
            ex => reported = ex);

        Assert.IsType<InvalidOperationException>(reported);
        Assert.True(report.Partial);
        Assert.Single(report.Players);
        // start-vs-start: no delta can be claimed, but the absolute counters survive.
        Assert.False(report.Players[0].MoneyRose);
        Assert.True(report.Players[0].FoundationConstructed);
    }

    [Fact]
    public void PartialReport_WrittenOnTeardown_LandsOnDiskAndParsesWithPartialTrue()
    {
        var brain = NewBrain(2);
        var start = AiMatchReport.CaptureAll(new[] { brain });
        brain.Update();

        var report = AiMatchReport.BuildPartial(start, () => AiMatchReport.CaptureAll(new[] { brain }));

        var path = Path.Combine(Path.GetTempPath(), $"obs2-partial-{Guid.NewGuid():N}", "ai-report.json");
        try
        {
            report.WriteToFile(path);

            Assert.True(File.Exists(path));
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(AiMatchReport.SchemaId, document.RootElement.GetProperty("schema").GetString());
            Assert.True(document.RootElement.GetProperty("partial").GetBoolean());
        }
        finally
        {
            var directory = Path.GetDirectoryName(path);
            if (directory != null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
