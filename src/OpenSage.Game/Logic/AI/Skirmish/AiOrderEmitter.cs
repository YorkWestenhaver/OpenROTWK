#nullable enable

// S9-04 (R15 L3): the AI's intent -> order translator, and the only place a manager's
// "I want these units to move there" becomes actual Order objects.
//
// WHY THIS EXISTS AT ALL
//
// The legacy order pipe is a command-bar pipe: almost every OrderType is implicitly addressed
// to "whatever this player currently has selected" (OrderProcessor.cs resolves MoveTo,
// AttackObject, CreateUnit, BuildObject, Sell, StopMoving... all against player.SelectedUnits).
// A manager that submitted a bare MoveTo would move whatever the player happened to be holding
// - possibly nothing, possibly the wrong army. So every command MUST be preceded by a
// SetSelection naming its actors, and the two MUST arrive adjacent and in that order.
//
// Doing that by hand in every manager would mean every manager knows the pairing rule, the
// budget rule and the id-ordering rule, and each would get one of them subtly wrong. Instead
// managers call ONE intent method here and this class owns all three rules. That is also what
// makes the dr-0040 sink swap (packet S9-16) a one-file change: managers never construct an
// Order, so replacing the IAiOrderSink implementation under this class touches zero manager
// files. The published contract for that swap is design-netcode.md 9.x "P4 swap contract
// (AI lane, S9-04 -> S9-16)".
//
// PER-PLAYER SAFETY (verified against current main, not assumed)
//
// Emitting SetSelection for an AI player does NOT disturb the human's selection:
//   * Player.SelectedUnits (Player.cs:205) is a per-Player set, mutated only through
//     Player.SelectUnits/DeselectUnits (Player.cs:259/295) on that one Player instance;
//   * OrderProcessor.Process (OrderProcessor.cs:26-35) picks the Player from order.PlayerIndex
//     and does every selection read/write against THAT player;
//   * SelectionSystem.SetSelectedObjects (SelectionSystem.cs:134) mutates the passed player,
//     and only reaches the shared OrderGenerator / selection audio when
//     `player == Game.Scene3D.LocalPlayer` - i.e. never for an AI player.
// So the AI's own playerIndex is the correct and sufficient isolation mechanism, and every
// order this class builds carries it.
//
// ADJACENCY SURVIVES THE PIPE
//
// NetworkMessageBuffer.AddLocalOrder appends to a plain List<Order> and Tick sends/receives
// that list in order into FrameOrders[frame] (NetworkMessageBuffer.cs:28-57), which
// OrderProcessor then walks in order. Two orders submitted back-to-back with nothing in
// between therefore execute back-to-back. "Nothing in between" is why a batch is emitted in a
// single tight loop below and why the budget can never split one.
//
// DETERMINISM (audited by S9-16 against R-S9-4)
//
// - Actor ids are normalized to ascending order and deduplicated, so two peers that computed
//   the same SET of units emit byte-identical selections regardless of how their manager
//   happened to enumerate them.
// - The per-frame budget and the FIFO backlog are pure functions of (batch sizes, arrival
//   order, frame), with no wall clock and no RNG.
// - Backlog overflow drops the OLDEST batch, deterministically (see MaxBacklogBatches).
//
// KNOWN HAZARD, deliberately not papered over here (blackboard S9-04 finding): an actor id can
// die between the snapshot the manager read and the frame the order executes on. OrderProcessor
// resolves stale ids to null and its SetSelection case swallows the resulting exception AFTER
// Player.SelectUnits has already unioned the nulls in, so the paired command then dereferences
// a null outside any try/catch. That is an OrderProcessor/Player robustness bug, not something
// this class can see - it belongs to whoever owns those files. Managers should prefer ids they
// re-read from the current frame's snapshot.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using OpenSage.Logic.Orders;

namespace OpenSage.Logic.AI.Skirmish;

/// <summary>
/// The intent a batch of orders came from. Trace tag and counter suffix only - the engine never
/// sees this, it sees the <see cref="Order"/>s the intent expanded into.
/// </summary>
public enum AiOrderIntent
{
    /// <summary>Move a set of units to a position.</summary>
    MoveGroup,

    /// <summary>Attack one object with a set of units.</summary>
    AttackWith,

    /// <summary>Queue one unit at one producer structure.</summary>
    QueueUnit,

    /// <summary>Place one structure with one builder.</summary>
    BuildStructure,

    /// <summary>Set one producer structure's rally point.</summary>
    SetRallyPoint,

    /// <summary>(S9-06) Unpack one packed castle/camp foundation into its castle.</summary>
    UnpackCastle,

    /// <summary>(S9-06) Build one structure on one castle build plot.</summary>
    ConstructOnFoundation,
}

/// <summary>
/// Translates skirmish-AI intents into paired <see cref="Order"/>s on the AI's own player
/// index, rate-limits them per logic frame, and holds the overflow in a FIFO backlog.
/// </summary>
/// <remarks>
/// <para>
/// Implements <see cref="IAiBrainManager"/> purely so the brain's existing registration list
/// drives the per-frame roll-over; it makes no decisions of its own. Register it FIRST on the
/// brain, before any decision-making manager: its <see cref="Update"/> resets the frame budget
/// and drains the backlog, and a manager that ran before it would spend a budget belonging to
/// the previous frame.
/// </para>
/// <para>
/// Not thread-safe and not meant to be: one emitter per brain, touched only from the logic tick.
/// </para>
/// </remarks>
public sealed class AiOrderEmitter : IAiBrainManager
{
    /// <summary>
    /// Orders one brain may submit per logic frame by default. Eight is four selection+command
    /// pairs per frame - far more than a human can click, far less than a looping manager bug
    /// needs to bury the order pipe, which is the only thing this budget is defending against.
    /// </summary>
    public const int DefaultOrdersPerFrame = 8;

    /// <summary>Default backlog depth, in batches (not orders). See <see cref="MaxBacklogBatches"/>.</summary>
    public const int DefaultMaxBacklogBatches = 64;

    /// <summary>Trace category for every line this class writes.</summary>
    public const string TraceCategory = "orders";

    private readonly IAiWorldView _world;
    private readonly IAiOrderSink _sink;
    private readonly AiTrace? _trace;
    private readonly Queue<PendingBatch> _backlog = new();

    private uint _currentFrame;
    private bool _rolledOnce;
    private int _spentThisFrame;

    /// <summary>Player index stamped on every order this emitter builds.</summary>
    public int PlayerIndex { get; }

    /// <summary>Maximum orders submitted per logic frame. See the oversize-batch note on <see cref="Emit"/>.</summary>
    public int OrdersPerFrame { get; }

    /// <summary>
    /// Maximum batches held in the backlog. On overflow the OLDEST batch is dropped, not the
    /// newest: a batch that has waited this long is addressed to a world state that no longer
    /// exists (a move to a position the army already walked past), whereas the batch arriving
    /// now is the AI's current opinion. The rule is deterministic either way; this one throws
    /// away the less useful order.
    /// </summary>
    public int MaxBacklogBatches { get; }

    /// <summary>Batches waiting for budget, oldest first.</summary>
    public int BacklogCount => _backlog.Count;

    /// <summary>Orders submitted so far on the current frame.</summary>
    public int OrdersEmittedThisFrame => _spentThisFrame;

    /// <summary>Frame this emitter last rolled over to.</summary>
    public uint CurrentFrame => _currentFrame;

    /// <summary>Total <see cref="Order"/>s handed to the sink over the emitter's life.</summary>
    public int TotalOrdersEmitted { get; private set; }

    /// <summary>Intents accepted (emitted now or queued for later).</summary>
    public int TotalIntentsAccepted { get; private set; }

    /// <summary>Intents refused as malformed (no valid actor, bad definition id). Never queued.</summary>
    public int TotalIntentsRejected { get; private set; }

    /// <summary>Batches that could not fit their frame's budget and went to the backlog.</summary>
    public int TotalBatchesDeferred { get; private set; }

    /// <summary>Batches discarded because the backlog was full. Non-zero means a manager is over-ordering.</summary>
    public int TotalBatchesDropped { get; private set; }

    /// <summary>
    /// Batches emitted whole despite exceeding the per-frame budget, because pairing integrity
    /// outranks the budget. Only possible when a single batch is larger than
    /// <see cref="OrdersPerFrame"/>; with the default budget it is structurally impossible.
    /// </summary>
    public int TotalOverBudgetBatches { get; private set; }

    public AiOrderEmitter(
        IAiWorldView world,
        IAiOrderSink sink,
        AiTrace? trace = null,
        int ordersPerFrame = DefaultOrdersPerFrame,
        int maxBacklogBatches = DefaultMaxBacklogBatches)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(sink);

        if (ordersPerFrame < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ordersPerFrame),
                ordersPerFrame,
                "A brain must be allowed at least one order per frame; 0 would stall the AI forever.");
        }

        if (maxBacklogBatches < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBacklogBatches),
                maxBacklogBatches,
                "The backlog must hold at least one batch.");
        }

        _world = world;
        _sink = sink;
        _trace = trace;
        PlayerIndex = world.PlayerIndex;
        OrdersPerFrame = ordersPerFrame;
        MaxBacklogBatches = maxBacklogBatches;
        _currentFrame = world.CurrentFrame;
    }

    /// <summary>Convenience constructor wiring the emitter to a brain's own seams.</summary>
    public AiOrderEmitter(
        SkirmishAIBrain brain,
        int ordersPerFrame = DefaultOrdersPerFrame,
        int maxBacklogBatches = DefaultMaxBacklogBatches)
        : this(
            NotNull(brain).World,
            NotNull(brain).Orders,
            NotNull(brain).Trace,
            ordersPerFrame,
            maxBacklogBatches)
    {
    }

    private static SkirmishAIBrain NotNull(SkirmishAIBrain brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        return brain;
    }

    /// <inheritdoc />
    public string Name => TraceCategory;

    /// <summary>
    /// Rolls the frame budget and drains what the backlog can now afford. Does not decide
    /// anything; see the class remarks for why this must be the first registered manager.
    /// </summary>
    public void Update(SkirmishAIBrain brain) => RollFrame();

    // ---- intents ----------------------------------------------------------------------
    //
    // Every intent returns true when it was accepted (emitted this frame or queued) and false
    // when it was refused as malformed. "Queued" is still success: the manager's decision stands,
    // it just waits for budget. A manager must NOT retry on false - false means the arguments
    // were unusable, and retrying with the same arguments only burns the rejection counter.

    /// <summary>
    /// Moves <paramref name="unitIds"/> to <paramref name="targetPosition"/>.
    /// Emits SetSelection(ids) + MoveTo(position).
    /// </summary>
    public bool MoveGroup(IReadOnlyList<ObjectId> unitIds, in Vector3 targetPosition)
    {
        var actors = NormalizeIds(unitIds);
        if (actors.Length == 0)
        {
            return Reject(AiOrderIntent.MoveGroup, "no valid actor");
        }

        return Accept(
            AiOrderIntent.MoveGroup,
            [
                Order.CreateSetSelection(PlayerIndex, actors),
                Order.CreateMoveOrder(PlayerIndex, targetPosition)
            ]);
    }

    /// <summary>
    /// Attacks <paramref name="targetId"/> with <paramref name="unitIds"/>.
    /// Emits SetSelection(ids) + AttackObject/ForceAttackObject(target).
    /// </summary>
    /// <param name="unitIds">Attackers. Normalized to ascending, deduplicated, valid ids.</param>
    /// <param name="targetId">The object to attack.</param>
    /// <param name="force">
    /// True issues ForceAttackObject, which attacks regardless of alliance. The AI should pass
    /// false for ordinary aggression so that OrderProcessor's normal targeting rules apply.
    /// </param>
    public bool AttackWith(IReadOnlyList<ObjectId> unitIds, ObjectId targetId, bool force = false)
    {
        if (targetId.IsInvalid)
        {
            return Reject(AiOrderIntent.AttackWith, "invalid target");
        }

        var actors = NormalizeIds(unitIds);
        if (actors.Length == 0)
        {
            return Reject(AiOrderIntent.AttackWith, "no valid actor");
        }

        return Accept(
            AiOrderIntent.AttackWith,
            [
                Order.CreateSetSelection(PlayerIndex, actors),
                Order.CreateAttackObject(PlayerIndex, targetId, force)
            ]);
    }

    /// <summary>
    /// Queues one <paramref name="objectDefinitionId"/> at <paramref name="producerId"/>.
    /// Emits SetSelection(producer) + CreateUnit(definitionId, 1).
    /// </summary>
    /// <remarks>
    /// Exactly ONE producer, on purpose: OrderProcessor's CreateUnit case queues the unit at
    /// every selected object that has a ProductionUpdate and withdraws the cost only once
    /// (OrderProcessor.cs:171-183), so a two-producer selection would buy one unit and build
    /// two. The order's second argument mirrors the human command bar, which always sends 1
    /// (CommandButtonCallback.cs:73-75).
    /// </remarks>
    public bool QueueUnit(ObjectId producerId, int objectDefinitionId)
    {
        if (producerId.IsInvalid)
        {
            return Reject(AiOrderIntent.QueueUnit, "invalid producer");
        }

        if (objectDefinitionId <= 0)
        {
            return Reject(AiOrderIntent.QueueUnit, "invalid definition id");
        }

        // Order.cs is read-only for this packet (the missing factory lands in S9-05), so the
        // CreateUnit order is assembled here in exactly the human command bar's shape.
        var createUnit = new Order(PlayerIndex, OrderType.CreateUnit);
        createUnit.AddIntegerArgument(objectDefinitionId);
        createUnit.AddIntegerArgument(1);

        return Accept(
            AiOrderIntent.QueueUnit,
            [
                Order.CreateSetSelection(PlayerIndex, producerId),
                createUnit
            ]);
    }

    /// <summary>
    /// Places <paramref name="objectDefinitionId"/> at <paramref name="position"/> using
    /// <paramref name="builderId"/>. Emits SetSelection(builder) + BuildObject(def, pos, angle).
    /// </summary>
    /// <remarks>
    /// Exactly ONE builder, on purpose: OrderProcessor's BuildObject case finds the dozer with
    /// <c>SingleOrDefault</c> (OrderProcessor.cs:95-113), which THROWS when the selection holds
    /// two builders. <paramref name="angle"/> is the placement yaw in radians.
    /// </remarks>
    public bool BuildStructure(ObjectId builderId, int objectDefinitionId, in Vector3 position, float angle)
    {
        if (builderId.IsInvalid)
        {
            return Reject(AiOrderIntent.BuildStructure, "invalid builder");
        }

        if (objectDefinitionId <= 0)
        {
            return Reject(AiOrderIntent.BuildStructure, "invalid definition id");
        }

        return Accept(
            AiOrderIntent.BuildStructure,
            [
                Order.CreateSetSelection(PlayerIndex, builderId),
                Order.CreateBuildObject(PlayerIndex, objectDefinitionId, position, angle)
            ]);
    }

    /// <summary>
    /// Sets <paramref name="structureId"/>'s rally point. Emits SetSelection(structure) +
    /// SetRallyPoint(structure, position).
    /// </summary>
    /// <remarks>
    /// Single structure, on purpose. The SetRallyPoint order names its object explicitly, so the
    /// selection is strictly redundant for execution - it is emitted anyway so that every intent
    /// produces the same selection+command shape a human's click produces, which is what lets
    /// the S9-16 order-stream diff compare AI and human streams field-for-field. The multi-object
    /// form of the order is deliberately not used: OrderProcessor's >2-argument branch passes
    /// <c>new Vector3()</c> instead of the carried position (OrderProcessor.cs:323-345), i.e. it
    /// discards the rally point.
    /// </remarks>
    public bool SetRallyPoint(ObjectId structureId, in Vector3 rallyPoint)
    {
        if (structureId.IsInvalid)
        {
            return Reject(AiOrderIntent.SetRallyPoint, "invalid structure");
        }

        return Accept(
            AiOrderIntent.SetRallyPoint,
            [
                Order.CreateSetSelection(PlayerIndex, structureId),
                Order.CreateSetRallyPointOrder(PlayerIndex, [structureId], rallyPoint)
            ]);
    }

    // ---- (S9-06) castle intents -------------------------------------------------------
    //
    // WHY THESE TWO EMIT A SINGLE ORDER AND NO SetSelection
    //
    // Everything above is command-bar shaped: the order says WHAT to do and the preceding
    // SetSelection says WHO does it. The castle orders are not - each one names its target
    // object in its own payload, and OrderProcessor's four castle cases read that payload and
    // hand it straight to CastleOrderHandler without touching player.SelectedUnits at all
    // (OrderProcessor.cs, the FoundationConstruct / CastleUnpack cases; the handler's own guards
    // then re-derive the owner from the order's player index). Emitting a SetSelection alongside
    // them would therefore change the AI player's selection for no reason, and would leave that
    // selection pointing at a build plot when the next MoveGroup arrived - a real bug, not a
    // harmless extra order. The batch is still a batch, so the budget still cannot split it.

    /// <summary>
    /// Unpacks the packed castle/camp foundation <paramref name="foundationId"/>.
    /// Emits a single CastleUnpack order.
    /// </summary>
    /// <remarks>
    /// Affordability is NOT checked here: the unpack cost lives in the CastleBehavior's matched
    /// per-faction entry, which is sim state this seam cannot see, and CastleOrderHandler's
    /// guard 6 charges it and returns CannotAfford when it cannot. The caller is expected to
    /// re-issue on a cooldown rather than to pre-compute a cost it does not have.
    /// </remarks>
    public bool UnpackCastle(ObjectId foundationId)
    {
        if (foundationId.IsInvalid)
        {
            return Reject(AiOrderIntent.UnpackCastle, "invalid foundation");
        }

        return Accept(
            AiOrderIntent.UnpackCastle,
            [Order.CreateCastleUnpack(PlayerIndex, foundationId)]);
    }

    /// <summary>
    /// Builds <paramref name="objectDefinitionId"/> on the castle build plot
    /// <paramref name="plotId"/>. Emits a single FoundationConstruct order.
    /// </summary>
    /// <param name="plotId">A KINDOF BASE_FOUNDATION object this player owns.</param>
    /// <param name="objectDefinitionId">
    /// An <c>ObjectDefinition.InternalId</c>. Internal ids start at 1, so a non-positive value is
    /// always malformed and is refused here rather than becoming a "no object definition with
    /// internal id 0" line in the match log.
    /// </param>
    public bool ConstructOnFoundation(ObjectId plotId, int objectDefinitionId)
    {
        if (plotId.IsInvalid)
        {
            return Reject(AiOrderIntent.ConstructOnFoundation, "invalid plot");
        }

        if (objectDefinitionId <= 0)
        {
            return Reject(AiOrderIntent.ConstructOnFoundation, "invalid definition id");
        }

        return Accept(
            AiOrderIntent.ConstructOnFoundation,
            [Order.CreateFoundationConstruct(PlayerIndex, plotId, objectDefinitionId)]);
    }

    // ---- budget, backlog, emission ----------------------------------------------------

    /// <summary>
    /// Rolls over to the world's current frame if it moved: resets the budget, then drains as
    /// much of the backlog as the fresh budget affords. Idempotent within a frame.
    /// </summary>
    private void RollFrame()
    {
        var frame = _world.CurrentFrame;

        if (_rolledOnce && frame == _currentFrame)
        {
            return;
        }

        _rolledOnce = true;
        _currentFrame = frame;
        _spentThisFrame = 0;

        DrainBacklog();
    }

    private void DrainBacklog()
    {
        while (_backlog.Count > 0)
        {
            var batch = _backlog.Peek();

            if (!TryAfford(batch.Orders.Length, out var overBudget))
            {
                return;
            }

            _backlog.Dequeue();
            Emit(batch, overBudget, drained: true);
        }
    }

    private bool Accept(AiOrderIntent intent, Order[] orders)
    {
        RollFrame();

        TotalIntentsAccepted++;
        _trace?.Count("orders.intent." + intent);

        // The backlog is strictly FIFO: while anything is waiting, a new batch queues behind it
        // even if this frame still has budget. Letting a fresh intent overtake a waiting one
        // would make the emitted stream depend on arrival timing rather than arrival order,
        // which is exactly the kind of thing the S9-16 determinism audit hunts.
        if (_backlog.Count == 0 && TryAfford(orders.Length, out var overBudget))
        {
            Emit(new PendingBatch(intent, orders, _currentFrame), overBudget, drained: false);
            return true;
        }

        Defer(intent, orders);
        return true;
    }

    private bool Reject(AiOrderIntent intent, string reason)
    {
        RollFrame();

        TotalIntentsRejected++;
        _trace?.Count("orders.rejected");
        Line(string.Create(
            CultureInfo.InvariantCulture,
            $"reject f={_currentFrame} intent={intent} reason={reason}"));

        return false;
    }

    /// <summary>
    /// Whether <paramref name="count"/> more orders fit this frame.
    /// </summary>
    /// <param name="count">Size of the batch being considered, in orders.</param>
    /// <param name="overBudget">
    /// Set when the batch is larger than the entire per-frame budget and is being let through
    /// anyway. A batch is atomic - splitting it would put a SetSelection on one frame and its
    /// command on the next, by which time some other order may have changed the selection - so
    /// when the batch simply cannot ever fit, pairing wins and the budget bends, once, on an
    /// otherwise-untouched frame.
    /// </param>
    private bool TryAfford(int count, out bool overBudget)
    {
        overBudget = false;

        if (_spentThisFrame + count <= OrdersPerFrame)
        {
            return true;
        }

        if (_spentThisFrame == 0)
        {
            // Reaching here with a spend of 0 means count > OrdersPerFrame: unfittable forever.
            overBudget = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Submits a batch's orders back-to-back. The tight loop with nothing between the calls is
    /// the same-frame pairing guarantee: see the file header on adjacency through
    /// NetworkMessageBuffer.
    /// </summary>
    private void Emit(in PendingBatch batch, bool overBudget, bool drained)
    {
        var orders = batch.Orders;

        for (var i = 0; i < orders.Length; i++)
        {
            _sink.Submit(orders[i]);
        }

        _spentThisFrame += orders.Length;
        TotalOrdersEmitted += orders.Length;
        _trace?.Count("orders.emitted", orders.Length);

        if (drained)
        {
            Line(string.Create(
                CultureInfo.InvariantCulture,
                $"drain f={_currentFrame} intent={batch.Intent} n={orders.Length} waited={_currentFrame - batch.QueuedFrame} backlog={_backlog.Count}"));
        }

        if (overBudget)
        {
            TotalOverBudgetBatches++;
            _trace?.Count("orders.overbudget");

            Line(string.Create(
                CultureInfo.InvariantCulture,
                $"overbudget f={_currentFrame} intent={batch.Intent} n={orders.Length} budget={OrdersPerFrame}"));
        }
    }

    private void Defer(AiOrderIntent intent, Order[] orders)
    {
        if (_backlog.Count >= MaxBacklogBatches)
        {
            var dropped = _backlog.Dequeue();

            TotalBatchesDropped++;
            _trace?.Count("orders.dropped");

            Line(string.Create(
                CultureInfo.InvariantCulture,
                $"drop f={_currentFrame} intent={dropped.Intent} n={dropped.Orders.Length} queued={dropped.QueuedFrame} reason=backlogfull"));
        }

        _backlog.Enqueue(new PendingBatch(intent, orders, _currentFrame));

        TotalBatchesDeferred++;
        _trace?.Count("orders.deferred");

        Line(string.Create(
            CultureInfo.InvariantCulture,
            $"defer f={_currentFrame} intent={intent} n={orders.Length} spent={_spentThisFrame}/{OrdersPerFrame} backlog={_backlog.Count}"));
    }

    private void Line(string message) => _trace?.Line(TraceCategory, message);

    /// <summary>
    /// Copies, drops invalid ids, sorts ascending by index and deduplicates. Ascending order is
    /// a determinism requirement, not tidiness: the emitted SetSelection must depend only on
    /// WHICH objects a manager chose, never on the order it happened to walk them in.
    /// </summary>
    private static ObjectId[] NormalizeIds(IReadOnlyList<ObjectId>? ids)
    {
        if (ids == null || ids.Count == 0)
        {
            return [];
        }

        var buffer = new List<ObjectId>(ids.Count);

        for (var i = 0; i < ids.Count; i++)
        {
            if (ids[i].IsValid)
            {
                buffer.Add(ids[i]);
            }
        }

        if (buffer.Count == 0)
        {
            return [];
        }

        buffer.Sort(static (a, b) => a.Index.CompareTo(b.Index));

        var result = new List<ObjectId>(buffer.Count) { buffer[0] };

        for (var i = 1; i < buffer.Count; i++)
        {
            if (buffer[i].Index != result[^1].Index)
            {
                result.Add(buffer[i]);
            }
        }

        return result.ToArray();
    }

    /// <summary>One intent's worth of orders, kept together so the budget can never split it.</summary>
    private readonly struct PendingBatch(AiOrderIntent intent, Order[] orders, uint queuedFrame)
    {
        public AiOrderIntent Intent { get; } = intent;

        public Order[] Orders { get; } = orders;

        /// <summary>Frame the batch was created on; the drain line reports how long it waited.</summary>
        public uint QueuedFrame { get; } = queuedFrame;
    }
}
