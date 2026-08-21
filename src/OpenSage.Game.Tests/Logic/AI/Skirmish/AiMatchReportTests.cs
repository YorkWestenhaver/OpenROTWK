#nullable enable

// S9-02 (R15 L3) gate tests: the frozen AiMatchReport schema v1.
//
// Every test here runs with NO game, same as SkirmishAIBrainSpineTests: a FakeAiWorldView plus a
// RecordingAiTraceSink drives a real SkirmishAIBrain, and AiMatchReport.PlayerSnapshot.Capture
// reads it exactly as the launcher's --ai-report hook does. If a later change makes these tests
// need a Game fixture, AiMatchReport has reached around the brain's World/Trace seam.

using System.Collections.Generic;
using System.Text.Json;
using OpenSage.Logic.AI.Skirmish;
using Xunit;

namespace OpenSage.Tests.Logic.AI.Skirmish;

public class AiMatchReportTests
{
    private static SkirmishAIBrain NewBrain(int playerIndex, uint heartbeatInterval = 1) =>
        new(new FakeAiWorldView { PlayerIndex = playerIndex }, new RecordingOrderSink(),
            new AiTrace(playerIndex, new RecordingAiTraceSink()), heartbeatInterval);

    private static FakeAiWorldView WorldOf(SkirmishAIBrain brain) => (FakeAiWorldView)brain.World;

    // ---- PlayerSnapshot.Capture ----

    [Fact]
    public void Capture_ReadsWorldAndTraceAtCallTime()
    {
        var brain = NewBrain(playerIndex: 2);
        var world = WorldOf(brain);

        world.CurrentFrame = 30;
        world.Money = 1500;
        brain.Update(); // one heartbeat, TicksRun == 1
        brain.Trace.Count(AiMatchReport.FoundationConstructCounter);

        var snapshot = AiMatchReport.PlayerSnapshot.Capture(brain);

        Assert.Equal(2, snapshot.PlayerIndex);
        Assert.Equal(30u, snapshot.Frame);
        Assert.Equal(1500, snapshot.Money);
        Assert.Equal(1u, snapshot.TicksRun);
        Assert.Equal(1, snapshot.HeartbeatsEmitted);
        Assert.Equal(1, snapshot.LinesEmitted);
        Assert.Equal(1, snapshot.GetCount(AiMatchReport.FoundationConstructCounter));
        Assert.Equal(0, snapshot.GetCount("never.bumped"));
    }

    [Fact]
    public void Capture_CountersAreAnIndependentCopy_NotALiveViewOfAiTrace()
    {
        var brain = NewBrain(playerIndex: 0);
        brain.Trace.Count("base.foundation.ok");

        var snapshot = AiMatchReport.PlayerSnapshot.Capture(brain);
        Assert.Equal(1, snapshot.GetCount("base.foundation.ok"));

        // Bumping the live trace after capture must not retroactively change an already-taken
        // snapshot - that is exactly what would make "money rose"/"foundation built" undecidable.
        brain.Trace.Count("base.foundation.ok");
        brain.Trace.Count("base.foundation.ok");

        Assert.Equal(1, snapshot.GetCount("base.foundation.ok"));
        Assert.Equal(3, brain.Trace.GetCount("base.foundation.ok"));
    }

    // ---- SkirmishAiBrains / CaptureAll ----

    [Fact]
    public void SkirmishAiBrains_AndCaptureAll_ProduceOneSnapshotPerBrainInOrder()
    {
        var brain0 = NewBrain(playerIndex: 0);
        var brain1 = NewBrain(playerIndex: 1);
        WorldOf(brain0).Money = 10;
        WorldOf(brain1).Money = 20;

        var snapshots = AiMatchReport.CaptureAll(new[] { brain0, brain1 });

        Assert.Equal(2, snapshots.Count);
        Assert.Equal(0, snapshots[0].PlayerIndex);
        Assert.Equal(10, snapshots[0].Money);
        Assert.Equal(1, snapshots[1].PlayerIndex);
        Assert.Equal(20, snapshots[1].Money);
    }

    // ---- PlayerResult milestone predicates ----

    [Fact]
    public void PlayerResult_MoneyRose_IsTrueOnlyWhenEndStrictlyExceedsStart()
    {
        var start = new AiMatchReport.PlayerSnapshot(0, 0, 100, 0, 0, 0, 1, 1, new Dictionary<string, int>());
        var risen = new AiMatchReport.PlayerSnapshot(0, 10, 150, 0, 0, 1, 1, 1, new Dictionary<string, int>());
        var flat = new AiMatchReport.PlayerSnapshot(0, 10, 100, 0, 0, 1, 1, 1, new Dictionary<string, int>());
        var fell = new AiMatchReport.PlayerSnapshot(0, 10, 50, 0, 0, 1, 1, 1, new Dictionary<string, int>());

        Assert.True(new AiMatchReport.PlayerResult(start, risen).MoneyRose);
        Assert.False(new AiMatchReport.PlayerResult(start, flat).MoneyRose);
        Assert.False(new AiMatchReport.PlayerResult(start, fell).MoneyRose);
    }

    [Fact]
    public void PlayerResult_HeartbeatsPresent_ReadsTheEndSnapshotOnly()
    {
        var start = new AiMatchReport.PlayerSnapshot(0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>());
        var end = new AiMatchReport.PlayerSnapshot(0, 30, 0, 0, 0, 1, 1, 1, new Dictionary<string, int>());

        Assert.True(new AiMatchReport.PlayerResult(start, end).HeartbeatsPresent);

        var stillZero = new AiMatchReport.PlayerSnapshot(0, 30, 0, 0, 0, 1, 0, 0, new Dictionary<string, int>());
        Assert.False(new AiMatchReport.PlayerResult(start, stillZero).HeartbeatsPresent);
    }

    [Fact]
    public void PlayerResult_FoundationConstructed_ReadsTheFrozenCounterKeyOnly()
    {
        var start = new AiMatchReport.PlayerSnapshot(0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>());
        var withOtherCounter = new AiMatchReport.PlayerSnapshot(
            0, 30, 0, 0, 0, 1, 1, 1, new Dictionary<string, int> { ["base.foundation.rejected"] = 5 });
        var withFoundation = new AiMatchReport.PlayerSnapshot(
            0, 30, 0, 0, 0, 1, 1, 1, new Dictionary<string, int> { [AiMatchReport.FoundationConstructCounter] = 1 });

        Assert.False(new AiMatchReport.PlayerResult(start, withOtherCounter).FoundationConstructed);
        Assert.True(new AiMatchReport.PlayerResult(start, withFoundation).FoundationConstructed);
    }

    [Fact]
    public void PlayerResult_Constructor_RejectsMismatchedPlayerIndex()
    {
        var start = new AiMatchReport.PlayerSnapshot(0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>());
        var end = new AiMatchReport.PlayerSnapshot(1, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>());

        Assert.Throws<System.ArgumentException>(() => new AiMatchReport.PlayerResult(start, end));
    }

    // ---- AiMatchReport.Build pairing ----

    [Fact]
    public void Build_PairsSnapshotsByPlayerIndex_RegardlessOfInputOrder()
    {
        var start = new[]
        {
            new AiMatchReport.PlayerSnapshot(1, 0, 100, 0, 0, 0, 0, 0, new Dictionary<string, int>()),
            new AiMatchReport.PlayerSnapshot(0, 0, 100, 0, 0, 0, 0, 0, new Dictionary<string, int>()),
        };
        var end = new[]
        {
            new AiMatchReport.PlayerSnapshot(0, 10, 200, 0, 0, 1, 1, 1, new Dictionary<string, int> { [AiMatchReport.FoundationConstructCounter] = 1 }),
            new AiMatchReport.PlayerSnapshot(1, 10, 50, 0, 0, 1, 1, 1, new Dictionary<string, int>()),
        };

        var report = AiMatchReport.Build(start, end);

        Assert.Equal(2, report.Players.Count);
        // Ascending player index, for byte-stable serialization - not input order.
        Assert.Equal(0, report.Players[0].PlayerIndex);
        Assert.Equal(1, report.Players[1].PlayerIndex);
    }

    [Fact]
    public void Build_SkipsAPlayerPresentInOnlyOneList()
    {
        var start = new[]
        {
            new AiMatchReport.PlayerSnapshot(0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>()),
        };
        var end = new[]
        {
            new AiMatchReport.PlayerSnapshot(0, 10, 0, 0, 0, 1, 1, 1, new Dictionary<string, int>()),
            new AiMatchReport.PlayerSnapshot(1, 10, 0, 0, 0, 1, 1, 1, new Dictionary<string, int>()),
        };

        var report = AiMatchReport.Build(start, end);

        Assert.Single(report.Players);
        Assert.Equal(0, report.Players[0].PlayerIndex);
    }

    // ---- top-level milestones: "per AI player", so one straggler fails the whole run ----

    private static AiMatchReport.PlayerSnapshot Snap(int playerIndex, int money, int heartbeats, int foundationCount) =>
        new(playerIndex, 30, money, 0, 0, 1, heartbeats, heartbeats,
            foundationCount > 0
                ? new Dictionary<string, int> { [AiMatchReport.FoundationConstructCounter] = foundationCount }
                : new Dictionary<string, int>());

    [Fact]
    public void MilestoneA_RequiresEveryPlayerToHaveHeartbeatsAndRisingMoney()
    {
        var start = new[] { Snap(0, 100, 0, 0), Snap(1, 100, 0, 0) };
        var bothPass = new[] { Snap(0, 200, 1, 0), Snap(1, 200, 1, 0) };
        var oneFlat = new[] { Snap(0, 200, 1, 0), Snap(1, 100, 1, 0) };

        Assert.True(AiMatchReport.Build(start, bothPass).MilestoneA);
        Assert.False(AiMatchReport.Build(start, oneFlat).MilestoneA);
    }

    [Fact]
    public void MilestoneB_RequiresEveryPlayerToHaveConstructedAFoundation()
    {
        var start = new[] { Snap(0, 0, 0, 0), Snap(1, 0, 0, 0) };
        var bothPass = new[] { Snap(0, 0, 1, 1), Snap(1, 0, 1, 2) };
        var oneMissing = new[] { Snap(0, 0, 1, 1), Snap(1, 0, 1, 0) };

        Assert.True(AiMatchReport.Build(start, bothPass).MilestoneB);
        Assert.False(AiMatchReport.Build(start, oneMissing).MilestoneB);
    }

    [Fact]
    public void Milestones_AreFalseWithNoPlayers_RatherThanVacuouslyTrue()
    {
        var report = AiMatchReport.Build(System.Array.Empty<AiMatchReport.PlayerSnapshot>(), System.Array.Empty<AiMatchReport.PlayerSnapshot>());

        Assert.Empty(report.Players);
        Assert.False(report.MilestoneA);
        Assert.False(report.MilestoneB);
        Assert.False(report.Pass);
    }

    [Fact]
    public void Pass_RequiresBothMilestones()
    {
        var start = new[] { Snap(0, 100, 0, 0) };
        var onlyA = new[] { Snap(0, 200, 1, 0) };
        var onlyB = new[] { Snap(0, 100, 1, 1) };
        var both = new[] { Snap(0, 200, 1, 1) };

        Assert.False(AiMatchReport.Build(start, onlyA).Pass);
        Assert.False(AiMatchReport.Build(start, onlyB).Pass);
        Assert.True(AiMatchReport.Build(start, both).Pass);
    }

    // ---- JSON serialization ----

    [Fact]
    public void ToJson_IsValidAndCarriesTheFrozenTopLevelShape()
    {
        var start = new[] { Snap(0, 100, 0, 0) };
        var end = new[] { Snap(0, 200, 1, 1) };
        var report = AiMatchReport.Build(start, end, generatedAtUtc: "2026-08-21T00:00:00.000Z");

        using var doc = JsonDocument.Parse(report.ToJson());
        var root = doc.RootElement;

        Assert.Equal(AiMatchReport.SchemaId, root.GetProperty("schema").GetString());
        Assert.Equal("2026-08-21T00:00:00.000Z", root.GetProperty("generatedAtUtc").GetString());
        Assert.True(root.GetProperty("milestoneA").GetBoolean());
        Assert.True(root.GetProperty("milestoneB").GetBoolean());
        Assert.True(root.GetProperty("pass").GetBoolean());

        var players = root.GetProperty("players");
        Assert.Equal(1, players.GetArrayLength());

        var player0 = players[0];
        Assert.Equal(0, player0.GetProperty("playerIndex").GetInt32());
        Assert.True(player0.GetProperty("passesMilestoneA").GetBoolean());
        Assert.True(player0.GetProperty("passesMilestoneB").GetBoolean());

        var endObj = player0.GetProperty("end");
        Assert.Equal(200, endObj.GetProperty("money").GetInt32());
        Assert.Equal(1, endObj.GetProperty("counters").GetProperty(AiMatchReport.FoundationConstructCounter).GetInt32());

        var startObj = player0.GetProperty("start");
        Assert.Equal(100, startObj.GetProperty("money").GetInt32());
    }

    [Fact]
    public void ToJson_EscapesCounterNamesThatContainJsonSpecialCharacters()
    {
        var start = new AiMatchReport.PlayerSnapshot(0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>());
        var end = new AiMatchReport.PlayerSnapshot(
            0, 10, 0, 0, 0, 1, 1, 1, new Dictionary<string, int> { ["weird\"name\\with\ttab"] = 7 });
        var report = new AiMatchReport(new[] { new AiMatchReport.PlayerResult(start, end) }, "2026-08-21T00:00:00.000Z");

        using var doc = JsonDocument.Parse(report.ToJson());
        var counters = doc.RootElement.GetProperty("players")[0].GetProperty("end").GetProperty("counters");

        Assert.Equal(7, counters.GetProperty("weird\"name\\with\ttab").GetInt32());
    }

    [Fact]
    public void WriteToFile_WritesParsableJsonAndCreatesParentDirectories()
    {
        var start = new[] { Snap(0, 100, 0, 0) };
        var end = new[] { Snap(0, 200, 1, 1) };
        var report = AiMatchReport.Build(start, end);

        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ai-match-report-tests-" + System.Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(dir, "nested", "report.json");
        try
        {
            report.WriteToFile(path);

            Assert.True(System.IO.File.Exists(path));
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path));
            Assert.Equal(AiMatchReport.SchemaId, doc.RootElement.GetProperty("schema").GetString());
        }
        finally
        {
            if (System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.Delete(dir, recursive: true);
            }
        }
    }
}
