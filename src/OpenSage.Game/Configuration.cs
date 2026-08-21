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
}
