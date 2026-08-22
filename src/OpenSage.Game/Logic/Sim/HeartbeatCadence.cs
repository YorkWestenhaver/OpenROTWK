// OBS-3 (R15 observability plan): the "every Nth heartbeat is also an Info line" rule.
//
// Split out of HeadedSimSystems.Heartbeat as a pure function so the cadence itself is testable
// without standing up a sim loop or scraping NLog output (NLog is deliberately not a test
// surface here - see the header of SimHeartbeatTests).

namespace OpenSage.Logic.Sim;

/// <summary>
/// Decides which sim heartbeats are loud (Info, i.e. visible in the console/wrapper log) as
/// opposed to merely recorded (Debug, i.e. output.log only).
/// </summary>
internal static class HeartbeatCadence
{
    /// <summary>
    /// True when the heartbeat numbered <paramref name="heartbeatOrdinal"/> should ALSO be
    /// emitted at Info.
    /// </summary>
    /// <param name="heartbeatOrdinal">
    /// 1-based count of heartbeats emitted so far in this process, including this one.
    /// </param>
    /// <param name="everyNth">
    /// <see cref="Configuration.SimHeartbeatInfoEveryNth"/>. 0 or less disables the Info echo.
    /// 1 makes every heartbeat loud.
    /// </param>
    /// <remarks>
    /// The very first heartbeat is always loud when the echo is enabled: a run that dies during
    /// startup should still leave one "the sim ran at all" line in the console log, and the
    /// heartbeat fires at loop frame 0 precisely so that signal exists.
    /// </remarks>
    public static bool ShouldEmitAtInfo(long heartbeatOrdinal, int everyNth)
    {
        if (everyNth <= 0 || heartbeatOrdinal <= 0)
        {
            return false;
        }

        return (heartbeatOrdinal - 1) % everyNth == 0;
    }
}
