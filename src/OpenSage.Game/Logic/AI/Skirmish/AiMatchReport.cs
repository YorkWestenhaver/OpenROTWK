#nullable enable

// S9-02 (R15 L3): the frozen match-report schema the AI harness grades against.
//
// dr-0039 E2's R1 gate is graded off two milestones, checked per skirmish-AI player:
//   M-a  "heartbeats appear and money rises" - at least one AiTrace heartbeat was emitted, and
//        the player's money at the end of the match is strictly higher than at the start.
//   M-b  "at least one successful FoundationConstruct" - AiTrace.Counters[FoundationConstructCounter]
//        ("base.foundation.ok") is > 0 by the end of the match. That exact key is the worked
//        example in AiTrace.Count's own doc comment, and it is the key
//        SkirmishAIBrainSpineTests' Counters_* tests already exercise under the heading "the
//        machine-readable half of S9-02's report" - this class treats it as frozen, not chosen.
//
// This file only READS AiTrace/IAiWorldView/SkirmishAIBrain (S9-01's files, not this packet's
// reservation) - it never adds, renames or reinterprets a counter or heartbeat field. "Money
// rises" needs a start-of-match value to compare against, so callers take two snapshots
// (Capture at match start, Capture again at match end) rather than AiTrace growing history of
// its own - AiTrace stays a pure per-instant counter bag, per its own ownership note.
//
// ai-match.sh (this packet, a thin layer over L1-06's wrapper) is the PASS/FAIL runner: it
// reads this JSON's "pass"/"milestoneA"/"milestoneB" fields alongside L1-06's run-result JSON
// (clean exit vs crash/timeout/inconclusive), so a milestone can only read PASS when the
// process also terminated cleanly - that gate lives in the shell layer, not here.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace OpenSage.Logic.AI.Skirmish;

/// <summary>
/// Builds and serializes a <c>bfme2-ai-match/report/v1</c> JSON document from a start/end pair
/// of per-player <see cref="PlayerSnapshot"/>s.
/// </summary>
public sealed class AiMatchReport
{
    public const string SchemaId = "bfme2-ai-match/report/v1";

    /// <summary>
    /// The counter key S9-06's base manager is contracted to bump on a successful foundation
    /// placement. M-b reads no other key. See this file's header comment for why the name is
    /// frozen rather than chosen here.
    /// </summary>
    public const string FoundationConstructCounter = "base.foundation.ok";

    /// <summary>One skirmish-AI player's <see cref="IAiWorldView"/>/<see cref="AiTrace"/> state, copied out at a single instant.</summary>
    public sealed class PlayerSnapshot
    {
        public int PlayerIndex { get; }
        public uint Frame { get; }
        public int Money { get; }
        public int OwnObjects { get; }
        public int EnemyObjects { get; }
        public uint TicksRun { get; }
        public int HeartbeatsEmitted { get; }
        public int LinesEmitted { get; }

        /// <summary>An independent copy taken at capture time - never a live view onto the brain's own AiTrace, so an earlier snapshot cannot silently change under a caller holding it.</summary>
        public IReadOnlyDictionary<string, int> Counters { get; }

        public PlayerSnapshot(
            int playerIndex,
            uint frame,
            int money,
            int ownObjects,
            int enemyObjects,
            uint ticksRun,
            int heartbeatsEmitted,
            int linesEmitted,
            IReadOnlyDictionary<string, int> counters)
        {
            PlayerIndex = playerIndex;
            Frame = frame;
            Money = money;
            OwnObjects = ownObjects;
            EnemyObjects = enemyObjects;
            TicksRun = ticksRun;
            HeartbeatsEmitted = heartbeatsEmitted;
            LinesEmitted = linesEmitted;
            Counters = counters;
        }

        /// <summary>Reads a counter from this snapshot, or 0 when it was never bumped by capture time.</summary>
        public int GetCount(string name) => Counters.TryGetValue(name, out var value) ? value : 0;

        /// <summary>
        /// Reads a point-in-time snapshot off a live brain. Every read here goes through
        /// <see cref="SkirmishAIBrain.World"/>/<see cref="SkirmishAIBrain.Trace"/>, the brain's
        /// own public read surface - nothing here reaches around it into a GameObject or Player
        /// directly.
        /// </summary>
        public static PlayerSnapshot Capture(SkirmishAIBrain brain)
        {
            ArgumentNullException.ThrowIfNull(brain);

            var world = brain.World;
            var trace = brain.Trace;

            // Copy, not alias: trace.Counters is a live view over AiTrace's own mutable
            // dictionary, and this snapshot must stay frozen as of right now even if the brain
            // keeps ticking (and bumping counters) afterward.
            var counters = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var pair in trace.Counters)
            {
                counters[pair.Key] = pair.Value;
            }

            return new PlayerSnapshot(
                world.PlayerIndex,
                world.CurrentFrame,
                world.Money,
                world.OwnObjects.Count,
                world.EnemyObjects.Count,
                brain.TicksRun,
                trace.HeartbeatsEmitted,
                trace.LinesEmitted,
                counters);
        }
    }

    /// <summary>One player's graded result: its start/end snapshots plus the two per-player milestone checks.</summary>
    public sealed class PlayerResult
    {
        public int PlayerIndex { get; }
        public PlayerSnapshot Start { get; }
        public PlayerSnapshot End { get; }

        public PlayerResult(PlayerSnapshot start, PlayerSnapshot end)
        {
            ArgumentNullException.ThrowIfNull(start);
            ArgumentNullException.ThrowIfNull(end);

            if (start.PlayerIndex != end.PlayerIndex)
            {
                throw new ArgumentException(
                    $"Start/end snapshot player index mismatch: {start.PlayerIndex} vs {end.PlayerIndex}.",
                    nameof(end));
            }

            PlayerIndex = start.PlayerIndex;
            Start = start;
            End = end;
        }

        /// <summary>M-a's money half: strictly higher money at the end than at the start.</summary>
        public bool MoneyRose => End.Money > Start.Money;

        /// <summary>M-a's heartbeat half: at least one heartbeat was emitted by the end of the match.</summary>
        public bool HeartbeatsPresent => End.HeartbeatsEmitted > 0;

        /// <summary>M-b: at least one <see cref="FoundationConstructCounter"/> bump by the end of the match.</summary>
        public bool FoundationConstructed => End.GetCount(FoundationConstructCounter) > 0;

        public bool PassesMilestoneA => HeartbeatsPresent && MoneyRose;

        public bool PassesMilestoneB => FoundationConstructed;
    }

    public string Schema => SchemaId;

    public string GeneratedAtUtc { get; }

    /// <summary>Per-player results, ascending player index (byte-stable serialization).</summary>
    public IReadOnlyList<PlayerResult> Players { get; }

    /// <summary>True iff there is at least one AI player and EVERY one of them passed M-a.</summary>
    public bool MilestoneA => Players.Count > 0 && Players.All(p => p.PassesMilestoneA);

    /// <summary>True iff there is at least one AI player and EVERY one of them passed M-b.</summary>
    public bool MilestoneB => Players.Count > 0 && Players.All(p => p.PassesMilestoneB);

    /// <summary>The R1-gate verdict this run contributes: both milestones, for every AI player.</summary>
    public bool Pass => MilestoneA && MilestoneB;

    /// <summary>
    /// OBS-2: true when this report was flushed from a crash/teardown path instead of a clean
    /// end-of-match capture, i.e. the match was cut short by an unhandled exception. The
    /// milestone booleans are still computed from whatever the AI managed to do before the
    /// crash - a partial report with milestoneB=true is real evidence that a foundation was
    /// built; a partial report with milestoneA=false is NOT evidence that money failed to
    /// rise, only that the run died before it could. Graders must read this field before
    /// scoring a FAIL: partial=true means "no verdict", not "failed".
    /// </summary>
    public bool Partial { get; }

    public AiMatchReport(IReadOnlyList<PlayerResult> players, string? generatedAtUtc = null, bool partial = false)
    {
        ArgumentNullException.ThrowIfNull(players);

        Players = players;
        Partial = partial;
        GeneratedAtUtc = generatedAtUtc ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }

    /// <summary>Every skirmish-AI-controlled player among <paramref name="players"/>, i.e. <c>Player.SkirmishAIBrain != null</c>, in ascending player-index order.</summary>
    public static IReadOnlyList<SkirmishAIBrain> SkirmishAiBrains(IReadOnlyList<Player> players)
    {
        ArgumentNullException.ThrowIfNull(players);

        var brains = new List<SkirmishAIBrain>();
        foreach (var player in players)
        {
            if (player.SkirmishAIBrain is { } brain)
            {
                brains.Add(brain);
            }
        }

        return brains;
    }

    /// <summary>Captures one <see cref="PlayerSnapshot"/> per brain, in the order given.</summary>
    public static IReadOnlyList<PlayerSnapshot> CaptureAll(IReadOnlyList<SkirmishAIBrain> brains)
    {
        ArgumentNullException.ThrowIfNull(brains);

        var snapshots = new List<PlayerSnapshot>(brains.Count);
        foreach (var brain in brains)
        {
            snapshots.Add(PlayerSnapshot.Capture(brain));
        }

        return snapshots;
    }

    /// <summary>
    /// Builds the report by pairing <paramref name="start"/> and <paramref name="end"/>
    /// snapshots on <see cref="PlayerSnapshot.PlayerIndex"/>. A player present in only one list
    /// is skipped defensively (the launcher always captures the exact same brain list twice, so
    /// this should never happen outside a test that deliberately constructs a mismatch).
    /// </summary>
    public static AiMatchReport Build(
        IReadOnlyList<PlayerSnapshot> start,
        IReadOnlyList<PlayerSnapshot> end,
        string? generatedAtUtc = null,
        bool partial = false)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(end);

        var startByIndex = new Dictionary<int, PlayerSnapshot>();
        foreach (var snapshot in start)
        {
            startByIndex[snapshot.PlayerIndex] = snapshot;
        }

        var results = new List<PlayerResult>();
        foreach (var endSnapshot in end.OrderBy(snapshot => snapshot.PlayerIndex))
        {
            if (startByIndex.TryGetValue(endSnapshot.PlayerIndex, out var startSnapshot))
            {
                results.Add(new PlayerResult(startSnapshot, endSnapshot));
            }
        }

        return new AiMatchReport(results, generatedAtUtc, partial);
    }

    /// <summary>
    /// OBS-2's crash/teardown flush. Before this existed the report was written only after a
    /// clean game-loop exit, so any run that crashed produced NO report at all and the R1 gate
    /// could not distinguish "the AI achieved nothing" from "the process died at frame 127".
    ///
    /// The end capture is passed as a delegate rather than a list because on the crash path it
    /// reads live world state that may be exactly what just blew up: if it throws, the report
    /// degrades to start-vs-start (every delta reads zero) instead of being lost.
    /// <paramref name="onCaptureFailed"/> is how the caller logs that degradation. The result is
    /// always <see cref="Partial"/>.
    /// </summary>
    public static AiMatchReport BuildPartial(
        IReadOnlyList<PlayerSnapshot> start,
        Func<IReadOnlyList<PlayerSnapshot>> captureEnd,
        Action<Exception>? onCaptureFailed = null,
        string? generatedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(captureEnd);

        IReadOnlyList<PlayerSnapshot> end;
        try
        {
            end = captureEnd() ?? start;
        }
        catch (Exception ex)
        {
            onCaptureFailed?.Invoke(ex);
            end = start;
        }

        return Build(start, end, generatedAtUtc, partial: true);
    }

    // ---- serialization: hand-rolled, dependency-free (same house rule as GameTrace.cs's
    // --tracefile output and the bfme2harness Python tools: no JSON package dependency for a
    // small frozen shape). ----

    public string ToJson()
    {
        var sb = new StringBuilder();
        sb.Append('{');
        AppendStringField(sb, "schema", Schema, first: true);
        AppendStringField(sb, "generatedAtUtc", GeneratedAtUtc);
        AppendRawField(sb, "milestoneA", Bool(MilestoneA));
        AppendRawField(sb, "milestoneB", Bool(MilestoneB));
        AppendRawField(sb, "pass", Bool(Pass));
        AppendRawField(sb, "partial", Bool(Partial));

        sb.Append(",\"players\":[");
        for (var i = 0; i < Players.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            AppendPlayer(sb, Players[i]);
        }

        sb.Append(']');
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>Writes <see cref="ToJson"/> to <paramref name="path"/>, creating parent directories as needed.</summary>
    public void WriteToFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, ToJson());
    }

    private static void AppendPlayer(StringBuilder sb, PlayerResult result)
    {
        sb.Append('{');
        AppendRawField(sb, "playerIndex", result.PlayerIndex.ToString(CultureInfo.InvariantCulture), first: true);
        AppendRawField(sb, "passesMilestoneA", Bool(result.PassesMilestoneA));
        AppendRawField(sb, "passesMilestoneB", Bool(result.PassesMilestoneB));
        sb.Append(",\"start\":");
        AppendSnapshot(sb, result.Start);
        sb.Append(",\"end\":");
        AppendSnapshot(sb, result.End);
        sb.Append('}');
    }

    private static void AppendSnapshot(StringBuilder sb, PlayerSnapshot snapshot)
    {
        sb.Append('{');
        AppendRawField(sb, "frame", snapshot.Frame.ToString(CultureInfo.InvariantCulture), first: true);
        AppendRawField(sb, "money", snapshot.Money.ToString(CultureInfo.InvariantCulture));
        AppendRawField(sb, "ownObjects", snapshot.OwnObjects.ToString(CultureInfo.InvariantCulture));
        AppendRawField(sb, "enemyObjects", snapshot.EnemyObjects.ToString(CultureInfo.InvariantCulture));
        AppendRawField(sb, "ticksRun", snapshot.TicksRun.ToString(CultureInfo.InvariantCulture));
        AppendRawField(sb, "heartbeatsEmitted", snapshot.HeartbeatsEmitted.ToString(CultureInfo.InvariantCulture));
        AppendRawField(sb, "linesEmitted", snapshot.LinesEmitted.ToString(CultureInfo.InvariantCulture));

        sb.Append(",\"counters\":{");
        var firstCounter = true;
        foreach (var pair in snapshot.Counters)
        {
            if (!firstCounter)
            {
                sb.Append(',');
            }

            firstCounter = false;
            AppendJsonString(sb, pair.Key);
            sb.Append(':');
            sb.Append(pair.Value.ToString(CultureInfo.InvariantCulture));
        }

        sb.Append('}');
        sb.Append('}');
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static void AppendStringField(StringBuilder sb, string name, string value, bool first = false)
    {
        if (!first)
        {
            sb.Append(',');
        }

        AppendJsonString(sb, name);
        sb.Append(':');
        AppendJsonString(sb, value);
    }

    private static void AppendRawField(StringBuilder sb, string name, string rawValue, bool first = false)
    {
        if (!first)
        {
            sb.Append(',');
        }

        AppendJsonString(sb, name);
        sb.Append(':');
        sb.Append(rawValue);
    }

    private static void AppendJsonString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }

        sb.Append('"');
    }
}
