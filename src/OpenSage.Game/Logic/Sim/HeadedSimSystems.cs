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
// ran in before.
//
// R15 packet 2 (br-p2-scene3d-split) then retired the unphased residue hook: Scene3D.LogicTick
// is gone, split into IScene3D.SimObjectTick (head of PartitionUpdate) and
// IScene3D.ReapDestroyed (tail of PartitionUpdate, after PartitionCellManager.Update), with
// the player tick moved into GameLogic.Update beside the pathfind queue - GPL's AI::update
// slot. That reap move is packet 2's ONE claimed behavior change: GPL reaps its pending-delete
// list after ThePartitionManager->update, not before it.
//
// R15 packet 4 (br-p4b) then filled in the two order phases. IngestOrders no longer executes
// what it drains: NetworkMessageBuffer schedules received orders into SimLoop.Orders, and
// DispatchOrder - a no-op until now - executes them one at a time, at their scheduled frame,
// in the deterministic (playerIndex, submissionIndex) sequence. Packet 4's claimed behaviour
// changes are stated in full in NetworkMessageBuffer's header; the one to expect in a playtest
// is that local input now takes effect two logic frames (400ms) after the click, which is what
// every peer in a lockstep match sees and is not a regression.
//
// Non-goals, deliberately left for later packets: folding the second scripting accumulator
// into a phase and unifying the two frame counters (packet 3), and attaching SyncChecker to
// the CrcCheckpoint phase (packet 5).

using System;
using OpenSage.Diagnostics;
using OpenSage.Logic.Orders;
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

    private uint _logicFrameBeforeModuleUpdate;
    private bool _frameOpen;

    // Heartbeat bookkeeping (see Heartbeat below). Wall time is read from IGame.RenderTime
    // rather than a Stopwatch of our own, so the logic-FPS figure agrees with whatever clock
    // the rest of the headed game already reports through.
    private uint _lastHeartbeatLoopFrame;
    private TimeSpan _lastHeartbeatWallTime;

    /// <summary>OBS-3: 1-based count of heartbeats emitted, driving the Info echo cadence.</summary>
    private long _heartbeatOrdinal;

    /// <param name="game">The headed game whose subsystems this drives.</param>
    public HeadedSimSystems(IGame game)
    {
        ArgumentNullException.ThrowIfNull(game);

        _game = game;
    }

    /// <summary>
    /// Drain the connection for this frame: <c>NetworkMessageBuffer</c> broadcasts the local
    /// orders stamped for frame + 2 and schedules everything the connection delivers into
    /// <c>SimLoop.Orders</c>. Nothing executes here any more - that is
    /// <see cref="DispatchOrder"/>, one phase later (R15 packet 4).
    /// </summary>
    /// <remarks>
    /// Null-tolerant: a headed game sitting in the main menu has no buffer, and the loop still
    /// ticks (Game.Update runs LogicTick whenever IsLogicRunning, which is true from the
    /// constructor).
    /// </remarks>
    public void IngestOrders(LogicFrame frame)
    {
        _game.NetworkMessageBuffer?.Tick(frame);
    }

    /// <summary>
    /// Execute one scheduled order. The loop hands them over in the deterministic
    /// (playerIndex, submissionIndex) sequence; each is converted back out of SimCore and run
    /// by the legacy dispatcher, which is still the thing that actually moves units
    /// (A2-uiflow #2).
    /// </summary>
    /// <remarks>
    /// A <c>GameMessageType</c> with no <see cref="OrderIdentityMap"/> counterpart is logged
    /// and skipped, never guessed at: the two enum numberings collide at identical integers
    /// with different meanings (L2-plan #2), so casting one to the other would silently
    /// execute the wrong order.
    /// </remarks>
    public void DispatchOrder(in ScheduledOrder order)
    {
        if (!SimOrderConverter.TryConvertBack(order.Order, out var legacyOrder))
        {
            Logger.Warn(
                $"No legacy OrderType for {order.Order.Type} (player {order.PlayerIndex}, " +
                $"frame {order.Frame.Value}, submission {order.SubmissionIndex}); skipping it.");
            return;
        }

        _game.OrderProcessor.Process(legacyOrder);
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

    /// <summary>
    /// The partition slot, in GPL's order: the per-object loop dirties positions, the
    /// partition manager re-anchors against them, and only then is the frame's destroy list
    /// reaped.
    /// </summary>
    /// <remarks>
    /// GPL <c>GameLogic::update</c> runs <c>ThePartitionManager->update()</c> and reaps its
    /// pending-delete list immediately afterwards. The legacy headed tick reaped BEFORE the
    /// partition tick (<c>DeleteDestroyed</c> sat at the tail of <c>Scene3D.LogicTick</c>,
    /// which ran ahead of <c>PartitionCellManager.Update()</c>); moving the reap after it is
    /// this packet's one claimed behavior change. A dying object therefore stays visible to
    /// the partition update for the frame it dies on, as it does in the retail order.
    /// </remarks>
    public void PartitionUpdate(LogicFrame frame)
    {
        var timeInterval = GetTimeInterval();

        _game.Scene3D?.SimObjectTick(timeInterval);

        _game.PartitionCellManager.Update();

        _game.Scene3D?.ReapDestroyed();
    }

    /// <summary>
    /// The logic-frame time interval the per-object tick is handed. Same expression the
    /// legacy <c>Game.GetTimeInterval()</c> used - map time plus one logic frame's worth of
    /// wall clock.
    /// </summary>
    // TODO: Calculate time correctly (inherited from the legacy tick).
    private TimeInterval GetTimeInterval() =>
        new(_game.MapTime.TotalTime, TimeSpan.FromMilliseconds(_game.GameEngine.MsPerLogicFrame));

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

        // Every heartbeat is recorded at Debug (output.log) - unchanged contract, the soak
        // driver greps these. OBS-3 additionally echoes every Nth one at Info so the console /
        // wrapper log on its own proves the sim is alive; before this, heartbeat evidence and
        // crash evidence lived in different files and a run with a healthy sim could be
        // misbucketed as "0 heartbeats".
        Logger.Debug(message);

        _heartbeatOrdinal++;
        if (HeartbeatCadence.ShouldEmitAtInfo(_heartbeatOrdinal, _game.Configuration.SimHeartbeatInfoEveryNth))
        {
            Logger.Info(message);
        }

        if (GameTrace.IsTracing)
        {
            GameTrace.TraceInstantEvent(message);
        }

        _lastHeartbeatLoopFrame = loopFrame.Value;
        _lastHeartbeatWallTime = wallNow;
    }
}
