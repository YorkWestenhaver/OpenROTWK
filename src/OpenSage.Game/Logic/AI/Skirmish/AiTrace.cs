#nullable enable

// S9-01 (R15 L3): the AI's evidence channel.
//
// The dr-0039 guard is graded off text this class emits: M-a is "heartbeats appear and money
// rises", M-b is "at least one successful FoundationConstruct per AI player". So the trace
// format is a contract, not debug spew - the R1 gate's grader (S9-02's report schema v1)
// parses it, and changing a field name breaks the grade.
//
// Ownership note (roadmap MERGED ruling): L1-05 solely owns the sim heartbeat and GameTrace.cs
// structure. AiTrace is an ADDITIVE, self-contained emitter - it does not restructure, wrap or
// re-route GameTrace, and must not grow into a second general tracing framework.

using System.Collections.Generic;
using System.Globalization;

namespace OpenSage.Logic.AI.Skirmish;

/// <summary>Receives formatted AI trace lines. The default sink logs; tests record.</summary>
public interface IAiTraceSink
{
    void Write(string line);
}

/// <summary>Default sink: writes to the NLog logger, one line per call.</summary>
public sealed class LoggingAiTraceSink : IAiTraceSink
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    public static readonly LoggingAiTraceSink Instance = new();

    public void Write(string line) => Logger.Info(line);
}

/// <summary>
/// Per-brain trace emitter and counter bag. Every line is prefixed "[AI p{index}] " so a match
/// log can be split per player with a single grep.
/// </summary>
public sealed class AiTrace
{
    private readonly IAiTraceSink _sink;
    private readonly SortedDictionary<string, int> _counters = new(System.StringComparer.Ordinal);

    /// <summary>Player index this trace belongs to; appears in every line's prefix.</summary>
    public int PlayerIndex { get; }

    /// <summary>Total lines written, of any kind. Cheap liveness check for the harness.</summary>
    public int LinesEmitted { get; private set; }

    /// <summary>Heartbeat lines written. M-a grades off this being non-zero and growing.</summary>
    public int HeartbeatsEmitted { get; private set; }

    /// <summary>
    /// Named counters the managers bump (e.g. "base.foundation.ok"). Sorted by ordinal name so
    /// that a serialized report (S9-02) is byte-stable across runs and machines.
    /// </summary>
    public IReadOnlyDictionary<string, int> Counters => _counters;

    public AiTrace(int playerIndex, IAiTraceSink? sink = null)
    {
        PlayerIndex = playerIndex;
        _sink = sink ?? LoggingAiTraceSink.Instance;
        Prefix = string.Create(CultureInfo.InvariantCulture, $"[AI p{playerIndex}] ");
    }

    /// <summary>The "[AI p3] " prefix every line of this trace carries. Built once.</summary>
    public string Prefix { get; }

    /// <summary>
    /// Writes the per-brain heartbeat. FORMAT IS A CONTRACT (report schema v1):
    /// <c>[AI p0] hb f=30 money=1500 own=12 enemy=3 mgr=2</c>
    /// </summary>
    public void Heartbeat(uint frame, int money, int ownObjects, int enemyObjects, int managers)
    {
        HeartbeatsEmitted++;

        Write(string.Create(
            CultureInfo.InvariantCulture,
            $"hb f={frame} money={money} own={ownObjects} enemy={enemyObjects} mgr={managers}"));
    }

    /// <summary>
    /// Writes a manager line: <c>[AI p0] econ f=30 spend=500</c>. <paramref name="category"/>
    /// is the manager's short tag; keep it stable, the report groups on it.
    /// </summary>
    public void Line(string category, string message)
    {
        Write(category + " " + message);
    }

    /// <summary>Bumps a named counter. Counters are the machine-readable half of the evidence.</summary>
    public void Count(string name, int by = 1)
    {
        _counters.TryGetValue(name, out var current);
        _counters[name] = current + by;
    }

    /// <summary>Reads a counter, or 0 when it was never bumped.</summary>
    public int GetCount(string name)
    {
        _counters.TryGetValue(name, out var current);
        return current;
    }

    private void Write(string body)
    {
        LinesEmitted++;
        _sink.Write(Prefix + body);
    }
}

/// <summary>Records trace lines in memory. Test and match-report helper.</summary>
public sealed class RecordingAiTraceSink : IAiTraceSink
{
    private readonly List<string> _lines = new();

    public IReadOnlyList<string> Lines => _lines;

    public void Write(string line) => _lines.Add(line);
}
