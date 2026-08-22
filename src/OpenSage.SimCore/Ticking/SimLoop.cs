// The fixed-step logic loop and its frozen phase sequence
// (api-freeze-v1 F6; design-simcore-scaffolding §4.1-4.2).
//
// 5 Hz, confirmed by the written behavioral spec (LOGICFRAMES_PER_SECOND = 5 -
// crc-byteorder §3.2). SimCore owns the frame counter; OpenSage.Game keeps its real-time
// accumulation and render interpolation and calls Advance() once per due logic frame.
// The phase sequence is a contract: tests assert it, the deep-CRC dump labels records by it,
// and any future insertion is a netplay protocol-version bump.

using System;
using OpenSage.SimCore.Orders;

namespace OpenSage.SimCore.Ticking;

/// <summary>
/// The frozen per-frame phase sequence (F6). Declaration order IS execution order;
/// <see cref="SimLoop.PhaseSequence"/> exposes it for iteration and assertion.
/// </summary>
public enum SimPhase : byte
{
    IngestOrders,      // drain network/replay connection for frame N (lockstep barrier lives in transport)
    DispatchOrders,    // ordered by (playerIndex, submissionIndex); handlers are the only out-of-tick mutators
    ModuleUpdate,      // sleepy-update queue (§4.4)
    PartitionUpdate,   // spatial partition / collision bookkeeping
    CrcCheckpoint,     // iff frame % interval == 0  (§5)
    EndFrame,          // frame counter increment
}

/// <summary>
/// The systems the loop drives, one callback per phase. OpenSage.Game's GameLogic grows
/// into this seam as subsystems migrate; tests implement it directly.
/// </summary>
public interface ISimSystems
{
    /// <summary>Drain the connection (network, replay, or local echo) into <see cref="SimLoop.Orders"/>.</summary>
    void IngestOrders(LogicFrame frame);

    /// <summary>Called once per order, in deterministic (playerIndex, submissionIndex) sequence.</summary>
    void DispatchOrder(in ScheduledOrder order);

    void ModuleUpdate(LogicFrame frame);

    void PartitionUpdate(LogicFrame frame);

    void CrcCheckpoint(LogicFrame frame);
}

/// <summary>
/// Observer hook for tests and the deep-CRC dump: sees every phase entry of every frame,
/// in the frozen sequence.
/// </summary>
public interface ISimPhaseObserver
{
    void OnPhase(SimPhase phase, LogicFrame frame);
}

public sealed class SimLoop
{
    /// <summary>5 Hz fixed-step logic rate, confirmed in-binary (F6).</summary>
    public const int LogicFramesPerSecond = 5;

    /// <summary>Milliseconds per logic frame (1000 / 5). Integer by construction.</summary>
    public const int MsPerLogicFrame = 1000 / LogicFramesPerSecond;

    /// <summary>
    /// The +2-frame order schedule (F6), re-exported from the order pipe for the
    /// transport's convenience.
    /// </summary>
    public const int OrderSchedulingOffsetInFrames = OrderIngest.OrderSchedulingOffsetInFrames;

    /// <summary>
    /// The frozen phase sequence. The array is a defensive copy source - callers get the
    /// declaration order of <see cref="SimPhase"/>, which is the execution order.
    /// </summary>
    public static ReadOnlySpan<SimPhase> PhaseSequence => new[]
    {
        SimPhase.IngestOrders,
        SimPhase.DispatchOrders,
        SimPhase.ModuleUpdate,
        SimPhase.PartitionUpdate,
        SimPhase.CrcCheckpoint,
        SimPhase.EndFrame,
    };

    private readonly ISimSystems _systems;
    private readonly ISimPhaseObserver? _observer;

    /// <summary>
    /// OPEN-3 escape hatch (api-freeze-v1 §7): the AotR patch retunes two update-loop
    /// divisors whose sim-relevance is unresolved. The loop carries the divisor as plain
    /// data so whichever way OPEN-3 resolves is a config change, not a pipeline change.
    /// Not consulted by any arithmetic in this class today.
    /// </summary>
    public uint SubFrameDivisor { get; set; } = 1;

    /// <summary>
    /// Checkpoint cadence in frames: the CrcCheckpoint phase body runs iff
    /// frame % interval == 0. Data, not hardcode (our own netplay interval is OPEN-9).
    /// </summary>
    public uint CrcCheckpointIntervalInFrames { get; set; } = 100;

    public LogicFrame CurrentFrame { get; private set; }

    /// <summary>
    /// Re-seat the loop's frame counter (R15 packet 3, "one clock").
    /// </summary>
    /// <remarks>
    /// The loop counts frames; it does not own when a match begins. A host whose own logic
    /// clock can jump - a save being loaded restores an arbitrary frame number - has to be
    /// able to put the loop back on that number, otherwise the two counters are permanently
    /// offset and "loop frame N" and "logic frame N" stop naming the same frame. With this
    /// seam the headed host can assert plain equality at EndFrame instead of the weaker
    /// "advanced by exactly one" invariant packet 1 had to settle for.
    /// <para>
    /// Deliberately NOT a general-purpose setter for the sim: it resets the counter only.
    /// Anything already scheduled in <see cref="Orders"/> is stamped with absolute frames and
    /// is not rewritten, so a reset with orders in flight would strand them - callers reset at
    /// a match/save boundary, where the order pipe is empty.
    /// </para>
    /// </remarks>
    public void ResetTo(LogicFrame frame) => CurrentFrame = frame;

    public OrderIngest Orders { get; } = new();

    public SimLoop(ISimSystems systems, ISimPhaseObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(systems);
        _systems = systems;
        _observer = observer;
    }

    /// <summary>
    /// Runs exactly one logic frame through the frozen phase sequence and increments the
    /// frame counter. The caller (real-time accumulator, replay driver, or test) decides
    /// when a frame is due; the loop only guarantees what happens inside it.
    /// </summary>
    public void Advance()
    {
        var frame = CurrentFrame;

        Observe(SimPhase.IngestOrders, frame);
        _systems.IngestOrders(frame);

        Observe(SimPhase.DispatchOrders, frame);
        var orders = Orders.DrainForFrame(frame);
        for (var i = 0; i < orders.Count; i++)
        {
            _systems.DispatchOrder(orders[i]);
        }

        Observe(SimPhase.ModuleUpdate, frame);
        _systems.ModuleUpdate(frame);

        Observe(SimPhase.PartitionUpdate, frame);
        _systems.PartitionUpdate(frame);

        Observe(SimPhase.CrcCheckpoint, frame);
        if (CrcCheckpointIntervalInFrames != 0 && frame.Value % CrcCheckpointIntervalInFrames == 0)
        {
            _systems.CrcCheckpoint(frame);
        }

        Observe(SimPhase.EndFrame, frame);
        CurrentFrame = new LogicFrame(frame.Value + 1);
    }

    private void Observe(SimPhase phase, LogicFrame frame) => _observer?.OnPhase(phase, frame);
}
