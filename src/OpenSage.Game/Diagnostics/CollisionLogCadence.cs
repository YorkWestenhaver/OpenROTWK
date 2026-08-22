#nullable enable

namespace OpenSage.Diagnostics;

/// <summary>
/// Frame-cadenced throttle for the per-collision log line in
/// <see cref="OpenSage.Logic.Object.GameObject"/>.OnCollide.
///
/// R15 PROD-FIX: OnCollide logged one Info line per colliding object PAIR per FRAME. On an
/// Age of the Ring demo map that is a multi-gigabyte wrapper log inside ten minutes of a
/// 3600-frame gate run - a disk-exhaustion hazard for every headed sweep, and it drowns the
/// lines the harness actually grades from. The per-pair detail is now Debug; Info keeps only a
/// periodic summary so a gate log still shows that collision handling is alive and roughly how
/// busy it is.
///
/// Shape follows the two existing precedents: <see cref="DegradeLog"/> (process-wide,
/// static, log-only, with a test reset) and
/// <see cref="OpenSage.Logic.Sim.HeartbeatCadence"/> (the "every Nth is also loud" rule split
/// out as a pure function so the cadence is testable without NLog).
///
/// This state is LOG-ONLY. Nothing in the sim reads it, so it is deliberately not part of
/// any persisted or replayed state and cannot affect determinism.
/// </summary>
internal static class CollisionLogCadence
{
    /// <summary>
    /// Minimum number of logic frames between two Info summary lines. 300 frames is ~20 s of
    /// sim at 15 Hz, i.e. about a dozen lines across a 3600-frame gate run.
    /// </summary>
    internal const int DefaultInfoEveryNFrames = 300;

    private static readonly object Gate = new();

    private static long _sinceLastInfo;
    private static long _total;
    private static long _lastInfoFrame;
    private static bool _hasEmitted;

    /// <summary>
    /// Decides whether the frame <paramref name="frame"/> is far enough past
    /// <paramref name="lastInfoFrame"/> to be allowed another Info line.
    /// </summary>
    /// <param name="frame">Current logic frame.</param>
    /// <param name="lastInfoFrame">Frame the previous Info line was emitted on.</param>
    /// <param name="hasEmitted">False until the first Info line has been emitted.</param>
    /// <param name="everyNFrames">
    /// Cadence in frames. 0 or less disables the Info summary entirely (detail still goes to
    /// Debug). 1 makes every frame that sees a collision loud.
    /// </param>
    /// <remarks>
    /// The first collision of a run is always loud when the cadence is enabled, for the same
    /// reason the first sim heartbeat is: a run that dies early should still leave one
    /// "collision handling ran at all" line in the console log.
    /// </remarks>
    internal static bool ShouldEmitAtInfo(long frame, long lastInfoFrame, bool hasEmitted, int everyNFrames)
    {
        if (everyNFrames <= 0)
        {
            return false;
        }

        if (!hasEmitted)
        {
            return true;
        }

        return frame - lastInfoFrame >= everyNFrames;
    }

    /// <summary>
    /// Records one collision observed on <paramref name="frame"/>.
    /// </summary>
    /// <returns>
    /// A summary to write at Info, or <c>null</c> when this collision falls inside the current
    /// quiet window. <c>Since</c> counts the collisions coalesced into this line (including
    /// this one); <c>Total</c> is the process-wide running count.
    /// </returns>
    internal static CollisionLogSummary? Record(long frame, int everyNFrames = DefaultInfoEveryNFrames)
    {
        lock (Gate)
        {
            _total++;
            _sinceLastInfo++;

            if (!ShouldEmitAtInfo(frame, _lastInfoFrame, _hasEmitted, everyNFrames))
            {
                return null;
            }

            var summary = new CollisionLogSummary(frame, _sinceLastInfo, _total);
            _sinceLastInfo = 0;
            _lastInfoFrame = frame;
            _hasEmitted = true;
            return summary;
        }
    }

    /// <summary>
    /// Clears the cadence state. Test-only: keeps cases independent of each other and of the
    /// process-wide counters.
    /// </summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _sinceLastInfo = 0;
            _total = 0;
            _lastInfoFrame = 0;
            _hasEmitted = false;
        }
    }
}

/// <summary>
/// One periodic collision-activity summary line.
/// </summary>
internal readonly record struct CollisionLogSummary(long Frame, long Since, long Total);
