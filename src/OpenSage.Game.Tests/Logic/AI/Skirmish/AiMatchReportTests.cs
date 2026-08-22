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

    // ---- S9-10: schema v2 (milestones block, mC/mD, deltas, --seed's sibling artifacts) ----
    //
    // v2 is an EXTENSION: every assertion above still holds unchanged, and the tests here only
    // add to the shape. A v2 change that breaks one of the v1 tests above is a rewrite, not an
    // extension, and should be treated as a defect.

    /// <summary>A snapshot carrying an explicit counter bag, for the delta-graded v2 milestones.</summary>
    private static AiMatchReport.PlayerSnapshot SnapWith(int playerIndex, uint frame, Dictionary<string, int> counters) =>
        new(playerIndex, frame, 0, 0, 0, 1, 1, 1, counters);

    private static Dictionary<string, int> CombatCounters(
        int unitsQueued = 0, int teamsReady = 0, int wavesLaunched = 0, int engagements = 0,
        int unitsLost = 0, int teamMembersLost = 0) =>
        new()
        {
            [AiMatchReport.UnitsQueuedCounter] = unitsQueued,
            [AiMatchReport.TeamsReadyCounter] = teamsReady,
            [AiMatchReport.WaveLaunchedCounter] = wavesLaunched,
            [AiMatchReport.WaveEngagedCounter] = engagements,
            [AiMatchReport.UnitsLostCounter] = unitsLost,
            [AiMatchReport.TeamMembersLostCounter] = teamMembersLost,
        };

    [Fact]
    public void SchemaId_IsV2_AndTheV1IdIsStillNamed()
    {
        Assert.Equal("bfme2-ai-match/report/v2", AiMatchReport.SchemaId);
        Assert.Equal("bfme2-ai-match/report/v1", AiMatchReport.SchemaIdV1);
    }

    /// <summary>
    /// Drift guard for the by-value duplication in AiMatchReport's v2 counter-key constants: the
    /// report deliberately does not take a compile-time dependency on the managers, so a manager
    /// renaming its key would otherwise silently stop feeding a milestone. This test is the
    /// tripwire. (No wave-manager constants exist yet - those two keys are frozen by the report.)
    /// </summary>
    [Fact]
    public void CounterKeys_MatchTheManagerConstants()
    {
        Assert.Equal(AiProductionManager.UnitQueuedCounter, AiMatchReport.UnitsQueuedCounter);
        Assert.Equal(AiProductionManager.UnitConfirmedCounter, AiMatchReport.UnitsConfirmedCounter);
        Assert.Equal(AiProductionManager.UnitLostCounter, AiMatchReport.UnitsLostCounter);
        Assert.Equal(AiTeamManager.TeamFormedCounter, AiMatchReport.TeamsFormedCounter);
        Assert.Equal(AiTeamManager.TeamReadyCounter, AiMatchReport.TeamsReadyCounter);
        Assert.Equal(AiTeamManager.TeamMemberLostCounter, AiMatchReport.TeamMembersLostCounter);
        Assert.Equal(AiBaseManager.FoundationOkCounter, AiMatchReport.FoundationConstructCounter);
    }

    [Fact]
    public void Deltas_AreEndMinusStart_NotEndTotals()
    {
        // A start snapshot is captured after the game is already running, so its counters are
        // not necessarily zero - grading end totals would credit the window with earlier work.
        var start = SnapWith(0, 0, CombatCounters(unitsQueued: 4, teamsReady: 1, wavesLaunched: 2, engagements: 1, unitsLost: 3, teamMembersLost: 2));
        var end = SnapWith(0, 30, CombatCounters(unitsQueued: 10, teamsReady: 3, wavesLaunched: 5, engagements: 4, unitsLost: 6, teamMembersLost: 5));

        var result = new AiMatchReport.PlayerResult(start, end);

        Assert.Equal(6, result.UnitsQueued);
        Assert.Equal(2, result.TeamsReady);
        Assert.Equal(3, result.WavesLaunched);
        Assert.Equal(3, result.Engagements);
        Assert.Equal(6, result.Losses); // (6-3) produced units + (5-2) team members
        Assert.Equal(0, result.Delta("never.bumped"));
    }

    [Fact]
    public void PassesMilestoneC_NeedsBothQueuedProductionAndAReadyTeam()
    {
        var start = SnapWith(0, 0, CombatCounters());

        Assert.True(new AiMatchReport.PlayerResult(start, SnapWith(0, 30, CombatCounters(unitsQueued: 1, teamsReady: 1))).PassesMilestoneC);
        Assert.False(new AiMatchReport.PlayerResult(start, SnapWith(0, 30, CombatCounters(unitsQueued: 9, teamsReady: 0))).PassesMilestoneC);
        Assert.False(new AiMatchReport.PlayerResult(start, SnapWith(0, 30, CombatCounters(unitsQueued: 0, teamsReady: 9))).PassesMilestoneC);
    }

    [Fact]
    public void PassesMilestoneD_NeedsAWaveLaunchedAndAnEngagement()
    {
        var start = SnapWith(0, 0, CombatCounters());

        Assert.True(new AiMatchReport.PlayerResult(start, SnapWith(0, 30, CombatCounters(wavesLaunched: 1, engagements: 1))).PassesMilestoneD);
        // A wave that marched out and never met anyone is exactly the M-d failure mode the
        // roadmap's S9-12 contingency exists for - it must NOT read as a pass.
        Assert.False(new AiMatchReport.PlayerResult(start, SnapWith(0, 30, CombatCounters(wavesLaunched: 3, engagements: 0))).PassesMilestoneD);
        Assert.False(new AiMatchReport.PlayerResult(start, SnapWith(0, 30, CombatCounters(wavesLaunched: 0, engagements: 2))).PassesMilestoneD);
    }

    [Fact]
    public void MissingWaveCounters_GradeMilestoneDFalse_RatherThanThrowing()
    {
        // The wave keys are frozen by AiMatchReport before any manager bumps them: a report from
        // a build with no wave producer must still grade, as mD=false.
        var start = new AiMatchReport.PlayerSnapshot(0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>());
        var end = new AiMatchReport.PlayerSnapshot(0, 30, 0, 0, 0, 1, 1, 1, new Dictionary<string, int>());

        var report = AiMatchReport.Build(new[] { start }, new[] { end });

        Assert.False(report.MilestoneD);
        Assert.Equal(0, report.Players[0].WavesLaunched);
    }

    [Fact]
    public void MilestoneCAndD_RequireEveryPlayer_AndAreFalseWithNoPlayers()
    {
        var start = new[] { SnapWith(0, 0, CombatCounters()), SnapWith(1, 0, CombatCounters()) };
        var bothPass = new[]
        {
            SnapWith(0, 30, CombatCounters(unitsQueued: 2, teamsReady: 1, wavesLaunched: 1, engagements: 1)),
            SnapWith(1, 30, CombatCounters(unitsQueued: 5, teamsReady: 2, wavesLaunched: 2, engagements: 3)),
        };
        var oneStraggler = new[]
        {
            SnapWith(0, 30, CombatCounters(unitsQueued: 2, teamsReady: 1, wavesLaunched: 1, engagements: 1)),
            SnapWith(1, 30, CombatCounters(unitsQueued: 5, teamsReady: 0, wavesLaunched: 0, engagements: 0)),
        };

        Assert.True(AiMatchReport.Build(start, bothPass).MilestoneC);
        Assert.True(AiMatchReport.Build(start, bothPass).MilestoneD);
        Assert.False(AiMatchReport.Build(start, oneStraggler).MilestoneC);
        Assert.False(AiMatchReport.Build(start, oneStraggler).MilestoneD);

        var empty = AiMatchReport.Build(System.Array.Empty<AiMatchReport.PlayerSnapshot>(), System.Array.Empty<AiMatchReport.PlayerSnapshot>());
        Assert.False(empty.MilestoneC);
        Assert.False(empty.MilestoneD);
    }

    [Fact]
    public void MilestoneE_IsNullUnlessASoakDriverStampsIt()
    {
        var start = new[] { Snap(0, 100, 0, 0) };
        var end = new[] { Snap(0, 200, 1, 1) };

        Assert.Null(AiMatchReport.Build(start, end).MilestoneE);
        Assert.True(AiMatchReport.Build(start, end, milestoneE: true).MilestoneE);
        Assert.False(AiMatchReport.Build(start, end, milestoneE: false).MilestoneE);
    }

    [Fact]
    public void ToJson_CarriesTheMilestonesBlock_WithMeNullWhenNotGraded()
    {
        var start = new[] { SnapWith(0, 0, CombatCounters()) };
        var end = new[] { SnapWith(0, 30, CombatCounters(unitsQueued: 3, teamsReady: 1, wavesLaunched: 1, engagements: 2, unitsLost: 1, teamMembersLost: 4)) };
        var report = AiMatchReport.Build(start, end, generatedAtUtc: "2026-08-21T00:00:00.000Z");

        using var doc = JsonDocument.Parse(report.ToJson());
        var root = doc.RootElement;

        Assert.Equal("bfme2-ai-match/report/v2", root.GetProperty("schema").GetString());

        var milestones = root.GetProperty("milestones");
        Assert.False(milestones.GetProperty("mA").GetBoolean()); // money flat in this fixture
        Assert.False(milestones.GetProperty("mB").GetBoolean());
        Assert.True(milestones.GetProperty("mC").GetBoolean());
        Assert.True(milestones.GetProperty("mD").GetBoolean());
        Assert.Equal(JsonValueKind.Null, milestones.GetProperty("mE").ValueKind);

        // v1's top-level booleans are still emitted, with their v1 meanings.
        Assert.False(root.GetProperty("milestoneA").GetBoolean());
        Assert.False(root.GetProperty("milestoneB").GetBoolean());
        Assert.False(root.GetProperty("pass").GetBoolean());
        Assert.False(root.GetProperty("partial").GetBoolean());

        var player0 = root.GetProperty("players")[0];
        Assert.True(player0.GetProperty("passesMilestoneC").GetBoolean());
        Assert.True(player0.GetProperty("passesMilestoneD").GetBoolean());

        var deltas = player0.GetProperty("deltas");
        Assert.Equal(3, deltas.GetProperty("unitsQueued").GetInt32());
        Assert.Equal(1, deltas.GetProperty("teamsReady").GetInt32());
        Assert.Equal(1, deltas.GetProperty("wavesLaunched").GetInt32());
        Assert.Equal(2, deltas.GetProperty("engagements").GetInt32());
        Assert.Equal(5, deltas.GetProperty("losses").GetInt32());
    }

    [Fact]
    public void ToJson_EmitsMeAsABooleanOnceStamped()
    {
        var start = new[] { Snap(0, 100, 0, 0) };
        var end = new[] { Snap(0, 200, 1, 1) };
        var report = AiMatchReport.Build(start, end, generatedAtUtc: "2026-08-21T00:00:00.000Z", milestoneE: true);

        using var doc = JsonDocument.Parse(report.ToJson());
        var mE = doc.RootElement.GetProperty("milestones").GetProperty("mE");

        Assert.Equal(JsonValueKind.True, mE.ValueKind);
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
