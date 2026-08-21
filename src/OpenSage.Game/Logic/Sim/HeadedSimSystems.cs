// R14 packet 1 (workbench research/design-sim-presentation-bridge.md, §2 packet 1): the
// headed game's logic frame, expressed through SimCore's frozen phase sequence.
//
// This is a restructuring of Game.LogicTick(), not a second simulation. The headed frame
// already called the same GameLogic.Update() the headless host calls (HeadlessSimGame.Step);
// what was missing was the frame DRIVER - a frozen phase sequence instead of a bare
// wall-clock accumulator. Nothing about the sim itself changes here.
//
// ONE intentional behavior change: NetworkMessageBuffer.Tick() moves from AFTER
// GameLogic.Update() to BEFORE it, because IngestOrders precedes ModuleUpdate in the frozen
// sequence (SimLoop.cs, SimPhase declaration order). Draining the connection after running
// the frame is backwards for lockstep. Everything else runs in the same relative order it
// ran in before - see _prePartitionResidue for how that is kept true.
//
// Non-goals, deliberately left for later packets: splitting Scene3D.LogicTick into sim and
// presentation (packet 2), folding the second scripting accumulator into a phase and
// unifying the two frame counters (packet 3), routing real orders through
// SimLoop.Orders/ScheduledOrder so DispatchOrder stops being a no-op (packet 4), and
// attaching SyncChecker to the CrcCheckpoint phase (packet 5).

using System;
using OpenSage.Diagnostics;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Sim;

/// <summary>
/// The <see cref="ISimSystems"/> implementation for a headed (rendering) game. Drives the
/// same subsystems the legacy <c>Game.LogicTick()</c> drove, one per phase.
/// </summary>
/// <remarks>
/// Also serves as its own <see cref="ISimPhaseObserver"/> so the EndFrame phase can assert the
/// frame-counter reconciliation described on <see cref="OnPhase"/>, and can emit the periodic
/// sim heartbeat (A1-G9, see <c>Heartbeat</c>) once that assertion has passed. Both live in the
/// same EndFrame callback but are independent: the assertion reads counters only and crashes
/// the process on mismatch, while the heartbeat is read-only telemetry (a log line, and a
/// GameTrace instant event when a trace session is active) that never alters sim state.
/// </remarks>
internal sealed class HeadedSimSystems : ISimSystems, ISimPhaseObserver
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly IGame _game;
    private readonly Action _prePartitionResidue;

    private uint _logicFrameBeforeModuleUpdate;
    private bool _frameOpen;

    // Heartbeat bookkeeping (see Heartbeat below). Wall time is read from IGame.RenderTime
    // rather than a Stopwatch of our own, so the logic-FPS figure agrees with whatever clock
    // the rest of the headed game already reports through.
    private uint _lastHeartbeatLoopFrame;
    private TimeSpan _lastHeartbeatWallTime;

    /// <param name="game">The headed game whose subsystems this drives.</param>
    /// <param name="prePartitionResidue">
    /// Temporary carrier for the one piece of the legacy tick that has no phase yet:
    /// <c>Scene3D.LogicTick(timeInterval)</c>, which is still a mix of sim (PlayerManager,
    /// the per-object GameObject.LogicTick loop, DeleteDestroyed) and presentation. It ran
    /// between <c>GameLogic.Update()</c> and <c>PartitionCellManager.Update()</c> in the
    /// legacy tick, so it runs at the head of <see cref="PartitionUpdate"/> here, which is
    /// the same slot in the same order. Running it after <c>Advance()</c> instead would have
    /// silently moved the partition tick ahead of the object loop that dirties it - a second,
    /// unclaimed behavior change. Packet 2 splits it into real phases and deletes this hook.
    /// </param>
    public HeadedSimSystems(IGame game, Action prePartitionResidue = null)
    {
        ArgumentNullException.ThrowIfNull(game);

        _game = game;
        _prePartitionResidue = prePartitionResidue;
    }

    /// <summary>
    /// Drain the connection for this frame. Today that is the legacy
    /// <c>NetworkMessageBuffer</c> pump, which both sends local orders and applies received
    /// ones immediately; nothing is submitted to <c>SimLoop.Orders</c> yet, so
    /// <see cref="DispatchOrder"/> stays empty until packet 4 swaps the pipe.
    /// </summary>
    public void IngestOrders(LogicFrame frame)
    {
        _game.NetworkMessageBuffer?.Tick();
    }

    /// <summary>
    /// No-op: nothing submits to <c>SimLoop.Orders</c> in a headed game yet, so the loop
    /// never has an order to hand back. Packet 4 makes this real.
    /// </summary>
    public void DispatchOrder(in ScheduledOrder order)
    {
    }

    public void ModuleUpdate(LogicFrame frame)
    {
        // Frame-counter reconciliation (see OnPhase): remember where the logic clock stood
        // immediately before the module update advanced it. Captured here, not at
        // IngestOrders, so that anything an order handler does to the logic clock (an order
        // that ends the game or loads a save) falls outside the window being asserted.
        _logicFrameBeforeModuleUpdate = _game.GameLogic.CurrentFrame.Value;
        _frameOpen = true;

        _game.GameLogic.Update();
    }

    public void PartitionUpdate(LogicFrame frame)
    {
        // Unphased residue first - it ran here in the legacy tick. See the ctor doc.
        _prePartitionResidue?.Invoke();

        _game.PartitionCellManager.Update();
    }

    /// <summary>
    /// No-op. A headed game runs with <c>CrcCheckpointIntervalInFrames = 0</c>, so the loop
    /// never calls this at all; packet 5 attaches <c>SyncChecker</c> behind a launcher flag.
    /// </summary>
    public void CrcCheckpoint(LogicFrame frame)
    {
    }

    /// <summary>
    /// Frame-counter reconciliation, asserted at EndFrame.
    /// </summary>
    /// <remarks>
    /// There are two frame counters and packet 1 deliberately does not unify them (that is
    /// packet 3). <c>GameLogic.CurrentFrame</c> increments inside <c>GameLogic.Update()</c>,
    /// i.e. in the ModuleUpdate phase; <c>SimLoop.CurrentFrame</c> increments in EndFrame.
    /// So within a frame, after ModuleUpdate, the logic clock reads one ahead of the loop's,
    /// and at a frame boundary the two agree - the same pairing AutoHealScenario documents
    /// for the headless host.
    /// <para>
    /// The invariant asserted here is the reset-proof half: the logic clock advanced by
    /// exactly one during this <c>Advance()</c>. Plain equality is NOT asserted, because
    /// <c>GameLogic.Reset()</c> (which Scene3D construction calls) re-zeros the logic clock
    /// while the loop keeps counting, and a loaded save restores an arbitrary logic frame.
    /// Giving SimLoop a reset seam so the two can be pinned equal is packet 3's business.
    /// </para>
    /// </remarks>
    public void OnPhase(SimPhase phase, LogicFrame frame)
    {
        if (phase != SimPhase.EndFrame || !_frameOpen)
        {
            return;
        }

        _frameOpen = false;

        var logicFrame = _game.GameLogic.CurrentFrame.Value;
        if (logicFrame != _logicFrameBeforeModuleUpdate + 1)
        {
            DebugUtility.Crash(
                $"GameLogic went {_logicFrameBeforeModuleUpdate} -> {logicFrame} across one " +
                $"SimLoop.Advance() (loop frame {frame.Value}); the logic clock and the loop " +
                "must move in lockstep.");
        }

        Heartbeat(frame, logicFrame);
    }

    /// <summary>
    /// Periodic liveness signal for unattended runs (A1-G9): every
    /// <see cref="Configuration.SimHeartbeatIntervalInFrames"/> logic frames, log where the
    /// loop frame, the logic clock, and the render-frame counter each stand, plus the wall
    /// time and effective logic-FPS since the previous heartbeat. When a
    /// <see cref="GameTrace"/> session is active, the same snapshot also goes out as a
    /// GameTrace instant event, so a headed trace capture carries a coarse progress marker
    /// without needing a duration event around every frame.
    /// </summary>
    /// <remarks>
    /// Fires at loop frame 0 (an immediate "the sim is alive" signal at boot) and every
    /// <c>interval</c> frames after that. An interval of 0 or less disables the heartbeat
    /// entirely - useful for tests and for anyone who wants a quiet log.
    /// </remarks>
    private void Heartbeat(LogicFrame loopFrame, uint logicFrame)
    {
        var interval = _game.Configuration.SimHeartbeatIntervalInFrames;
        if (interval <= 0 || loopFrame.Value % (uint)interval != 0)
        {
            return;
        }

        var wallNow = _game.RenderTime.TotalTime;
        var wallDelta = wallNow - _lastHeartbeatWallTime;
        var framesSinceLastHeartbeat = loopFrame.Value - _lastHeartbeatLoopFrame;
        var logicFps = wallDelta.TotalSeconds > 0
            ? framesSinceLastHeartbeat / wallDelta.TotalSeconds
            : 0.0;

        var message =
            $"SimHeartbeat loopFrame={loopFrame.Value} logicFrame={logicFrame} " +
            $"renderFrame={_game.RenderFrameCount} wallDeltaMs={wallDelta.TotalMilliseconds:F0} " +
            $"logicFps={logicFps:F2}";

        Logger.Debug(message);

        if (GameTrace.IsTracing)
        {
            GameTrace.TraceInstantEvent(message);
        }

        _lastHeartbeatLoopFrame = loopFrame.Value;
        _lastHeartbeatWallTime = wallNow;
    }
}
