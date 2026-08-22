namespace OpenSage;

// TODO: Should this be immutable?
// TODO: Should there be a way of merging Configuration instances?
/// <summary>
/// Contains configuration for a game instance, typically gathered from
/// command line parameters and configuration files.
/// </summary>
public sealed class Configuration
{
    public bool LoadShellMap { get; set; } = true;
    public bool UseRenderDoc { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to use a unique port for each client in a multiplayer game.
    /// Normally, <see cref="Network.Ports.SkirmishGame"/> is used, but when we want to run multiple game
    /// instances on the same machine (for debugging purposes), each client needs a different port.
    /// </summary>
    /// <value>
    ///   <c>true</c> if [use unique ports]; otherwise, <c>false</c>.
    /// </value>
    public bool UseUniquePorts { get; set; } = false;

    /// <summary>
    /// Logic-frame interval (5 Hz) between periodic sim heartbeat emissions - a log line plus,
    /// when a <see cref="Diagnostics.GameTrace"/> session is active, a GameTrace instant event
    /// (see <c>HeadedSimSystems.OnPhase</c>). 0 disables the heartbeat. CLI: <c>--trace-frames</c>.
    /// </summary>
    public int SimHeartbeatIntervalInFrames { get; set; } = 50;

    /// <summary>
    /// OBS-3: how often a heartbeat is ALSO echoed at Info level, counted in heartbeats (not
    /// frames). Every heartbeat still goes to the Debug log unconditionally - that contract is
    /// unchanged - but the console/wrapper log only sees the 1st, (1+N)th, (1+2N)th ... one, so
    /// a run's stdout alone proves sim liveness without carrying 15k lines of Debug detail.
    /// 0 or less disables the Info echo entirely.
    /// </summary>
    /// <remarks>
    /// Default 10: with the default 50-frame interval that is one Info line per 500 logic frames
    /// (~33 s of sim at 15 Hz) - dense enough to bound "when did it stop" for a crash, sparse
    /// enough to stay noise.
    /// </remarks>
    public int SimHeartbeatInfoEveryNth { get; set; } = 10;

    /// <summary>
    /// R15 packet 5: logic-frame interval between deep-CRC checkpoints in a HEADED game.
    /// 0 (the default) disables the CRC entirely - the loop keeps
    /// <c>CrcCheckpointIntervalInFrames = 0</c>, nothing is attached to the CrcCheckpoint
    /// phase, and the run is byte-identical to one built before the flag existed. Non-zero
    /// values are clamped by <c>SyncChecker.EffectiveInterval</c> (max 100). CLI:
    /// <c>--headed-crc</c>.
    /// </summary>
    public uint HeadedCrcIntervalInFrames { get; set; } = 0;

    /// <summary>
    /// R15 packet 5: where the headed deep-CRC dump is written (the "opensage-deepdump v2"
    /// format the ScenarioDriver writes, so <c>DumpDiff</c> can compare the two). Required
    /// whenever <see cref="HeadedCrcIntervalInFrames"/> is non-zero; ignored otherwise.
    /// CLI: <c>--headed-crc-out</c>.
    /// </summary>
    public string HeadedCrcDumpPath { get; set; } = null;
}
