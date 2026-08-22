#nullable enable

// S9-08 (R15 L3): AiProductionManager v1 - the manager the dr-0039 guard's M-c criterion grades
// on its production half ("unitsQueued > 0"), plus the production world types and the pure
// planner they feed.
//
// WHAT IT DOES
//
// Once per logic frame it looks at the player's finished producer structures, decides whether
// the army has room for another unit (UnitBudget), picks ONE producer and ONE unit, and hands a
// QueueUnit intent to the brain's shared AiOrderEmitter. It also gives each producer a rally
// point exactly once, so trained units walk out towards the fight instead of piling up in the
// door.
//
// WHY "CONFIRMED", NOT "I SENT THE ORDER" (same reasoning as AiBaseManager)
//
// The AI cannot see an order result: it submits and OrderProcessor executes a couple of frames
// later, logging any rejection but telling the sender nothing. So this manager remembers the
// producer it queued at and the queue length it saw, and bumps the M-c grading counter
// (<see cref="UnitConfirmedCounter"/>) only when a later snapshot shows that producer's queue
// grown. "prod.unit.queued" (issued), ".timeout" and ".rejected" get their own counters so a
// failing gate says which half broke.
//
// SIM ANCHORS (engine facts, verified against current main - no binary-derived material)
//
//   ProductionUpdate.QueueProduction (ProductionUpdate.cs:475) is what a CreateUnit order
//   eventually reaches, and ProductionUpdate.CanEnqueue (ProductionUpdate.cs:554) is the sim's
//   own "will this queue take another entry" test: MaxQueueEntries == 0 means unlimited,
//   otherwise the queue must be shorter than it. LiveAiWorldView asks the module that exact
//   question rather than re-deriving it, so the AI and the sim cannot disagree about whether a
//   barracks is full.
//
// ONE AT A TIME, AND WHY THAT IS ENOUGH
//
// Exactly one queue attempt is in flight. At the default cooldown that is one unit per ~1s per
// AI, which fills a skirmish army well inside a gate run while keeping the confirmation logic a
// single-slot state machine rather than a correlation problem over an order pipe that reports
// nothing back.
//
// CLEAN-ROOM: the cooldowns, the default unit cap and "cheapest trainable first" are v1
// heuristics chosen to make an army appear, not recovered retail behaviour.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace OpenSage.Logic.AI.Skirmish;

/// <summary>
/// One unit template a producer may train, as the AI sees it.
/// </summary>
/// <param name="DefinitionId">
/// <c>ObjectDefinition.InternalId</c> - the id form the CreateUnit order carries and
/// OrderProcessor resolves.
/// </param>
/// <param name="TemplateName">Definition name, e.g. "MordorFighterHorde". Trace text only.</param>
/// <param name="Cost">Build cost as int sim money, for the economy manager's afford check.</param>
/// <param name="IsHorde">
/// True when the definition is KINDOF HORDE. Recorded because a horde is what the AI wants to
/// train and what it can later order: the individual orcs a horde spawns are horde MEMBERS and
/// are unorderable (see <see cref="AiObjectView.IsHordeMember"/>).
/// </param>
public readonly record struct AiTrainableUnit(
    int DefinitionId,
    string TemplateName,
    int Cost,
    bool IsHorde);

/// <summary>
/// The AI's per-frame snapshot of one owned unit-producing structure.
/// </summary>
/// <remarks>
/// A reference record rather than a struct because it carries the trainable list; the list is
/// resolved once per match (a CommandSet does not change mid-game) and shared by every snapshot
/// of that producer.
/// </remarks>
/// <param name="Id">Engine object id. CreateUnit / SetRallyPoint orders address this.</param>
/// <param name="TemplateName">The producer's own definition name. Trace text only.</param>
/// <param name="Position">World position, used to derive a rally point.</param>
/// <param name="CanEnqueue">
/// The sim's own answer from <c>ProductionUpdate.CanEnqueue()</c> (ProductionUpdate.cs:554), not
/// a second opinion computed here.
/// </param>
/// <param name="QueueLength">Entries currently in the production queue. The confirmation signal.</param>
/// <param name="Trainable">What this producer may train, cheapest first, ties by ordinal name.</param>
public sealed record AiProducerView(
    ObjectId Id,
    string TemplateName,
    Vector3 Position,
    bool CanEnqueue,
    int QueueLength,
    IReadOnlyList<AiTrainableUnit> Trainable);

/// <summary>
/// How much army the AI is allowed to have in existence plus in production.
/// </summary>
/// <remarks>
/// Pure value, computed fresh from a snapshot every frame - the manager holds no running total,
/// so a unit dying or a queue draining is reflected immediately and no counter can drift.
/// Everything is int (matching AiEconomyManager's int-only rule); there is no float here.
/// </remarks>
/// <param name="UnitCount">Orderable units alive now - hordes and standalone units, never horde members.</param>
/// <param name="InFlight">Entries sitting in producer queues.</param>
/// <param name="Cap">The ceiling this AI plays to.</param>
public readonly record struct UnitBudget(int UnitCount, int InFlight, int Cap)
{
    /// <summary>Units alive plus units bought and not yet delivered.</summary>
    public int Committed => UnitCount + InFlight;

    /// <summary>How many more the AI may commit to. Never negative.</summary>
    public int Headroom => Cap > Committed ? Cap - Committed : 0;

    /// <summary>True when the AI may queue at least one more unit.</summary>
    public bool AllowsMore => Headroom > 0;
}

/// <summary>
/// The pure half of the production manager: everything that is a function of a snapshot alone.
/// </summary>
/// <remarks>
/// Split out for the same reason <see cref="BasePlotPlan"/> is: the choices below are testable
/// with no frames, no orders and no manager state. Every one of them breaks its ties explicitly
/// and none of them compares floats.
/// </remarks>
public static class AiProductionPlan
{
    /// <summary>
    /// Units an AI plays to when the mod ships no better number. v1 placeholder tuning, not a
    /// recovered retail constant: big enough for the AI to field several teams of the default
    /// group size, small enough that a runaway production bug is bounded and visible.
    /// </summary>
    public const int DefaultUnitCap = 24;

    /// <summary>
    /// Counts the objects that count against the unit cap and are eligible for a team.
    /// </summary>
    /// <remarks>
    /// <see cref="AiObjectView.IsOrderableUnit"/>, i.e. horde MEMBERS are not counted. Counting
    /// them would make a single ten-orc horde eat eleven slots of a 24-unit cap and the AI would
    /// stop producing after two hordes.
    /// </remarks>
    public static int CountOrderableUnits(IReadOnlyList<AiObjectView>? ownObjects)
    {
        if (ownObjects == null)
        {
            return 0;
        }

        var count = 0;

        for (var i = 0; i < ownObjects.Count; i++)
        {
            if (ownObjects[i].IsOrderableUnit)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Total entries queued across every producer.</summary>
    public static int CountInFlight(IReadOnlyList<AiProducerView>? producers)
    {
        if (producers == null)
        {
            return 0;
        }

        var total = 0;

        for (var i = 0; i < producers.Count; i++)
        {
            var producer = producers[i];
            if (producer is not null && producer.QueueLength > 0)
            {
                total += producer.QueueLength;
            }
        }

        return total;
    }

    /// <summary>Builds the frame's budget from the snapshot.</summary>
    public static UnitBudget Budget(
        IReadOnlyList<AiObjectView>? ownObjects,
        IReadOnlyList<AiProducerView>? producers,
        int cap)
        => new(CountOrderableUnits(ownObjects), CountInFlight(producers), cap > 0 ? cap : DefaultUnitCap);

    /// <summary>
    /// The producer to use this frame, or null when none can take an order.
    /// </summary>
    /// <remarks>
    /// Lowest object id among producers that report <see cref="AiProducerView.CanEnqueue"/>, have
    /// something to train, and are off cooldown. Explicitly independent of the order the list
    /// arrived in: the live view sorts by id, but a manager that relied on that would break
    /// silently the day something re-orders the snapshot, and the shuffled-input determinism test
    /// exists to pin exactly that.
    /// </remarks>
    /// <param name="producers">Producers from <see cref="IAiWorldView.Producers"/>.</param>
    /// <param name="isReady">Cooldown predicate: false hides a producer for this frame.</param>
    public static AiProducerView? ChooseProducer(
        IReadOnlyList<AiProducerView>? producers,
        Func<ObjectId, bool>? isReady = null)
    {
        if (producers == null)
        {
            return null;
        }

        AiProducerView? best = null;

        for (var i = 0; i < producers.Count; i++)
        {
            var producer = producers[i];

            if (producer is null || !producer.CanEnqueue || producer.Trainable == null || producer.Trainable.Count == 0)
            {
                continue;
            }

            if (isReady is not null && !isReady(producer.Id))
            {
                continue;
            }

            if (best is null || producer.Id.Index < best.Id.Index)
            {
                best = producer;
            }
        }

        return best;
    }

    /// <summary>
    /// The unit to train at <paramref name="producer"/>: the cheapest affordable one, or null.
    /// </summary>
    /// <remarks>
    /// Cheapest, not best, for the same reason <see cref="BasePlotPlan.CheapestOfRole"/> is: an
    /// AI that always reaches for its most expensive unit stalls at the affordability gate and
    /// fields nothing, which is precisely the dr-0039 failure. Ties break on ordinal template
    /// name so two machines pick the same unit. Definitions with a non-positive definition id are
    /// skipped - the emitter would reject them anyway, and burning the cooldown on a guaranteed
    /// rejection is how an AI ends up doing nothing forever.
    /// </remarks>
    public static AiTrainableUnit? ChooseUnit(AiProducerView? producer, Func<int, bool> canAfford)
    {
        ArgumentNullException.ThrowIfNull(canAfford);

        var trainable = producer?.Trainable;
        if (trainable == null)
        {
            return null;
        }

        AiTrainableUnit? best = null;

        for (var i = 0; i < trainable.Count; i++)
        {
            var candidate = trainable[i];

            if (candidate.DefinitionId <= 0 || !canAfford(candidate.Cost))
            {
                continue;
            }

            if (best is null
                || candidate.Cost < best.Value.Cost
                || (candidate.Cost == best.Value.Cost
                    && string.CompareOrdinal(candidate.TemplateName, best.Value.TemplateName) < 0))
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// The centroid of <paramref name="objects"/>, or null when the list is empty.
    /// </summary>
    /// <remarks>
    /// Summed in list order. The live view hands lists in ascending object id, so two peers that
    /// see the same objects sum the same floats in the same order and get bit-identical results;
    /// a manager must never re-order a list before summing it.
    /// </remarks>
    public static Vector3? Centroid(IReadOnlyList<AiObjectView>? objects)
    {
        if (objects == null || objects.Count == 0)
        {
            return null;
        }

        var sum = Vector3.Zero;

        for (var i = 0; i < objects.Count; i++)
        {
            sum += objects[i].Position;
        }

        return sum / objects.Count;
    }

    /// <summary>
    /// Where a producer's trained units should gather: <paramref name="distance"/> world units
    /// from the producer along the direction of <paramref name="towards"/>, or null when there is
    /// no direction to face (no enemy seen yet, or the enemy centroid is on top of the producer).
    /// </summary>
    /// <remarks>
    /// Returning null rather than inventing a direction is deliberate. A rally point placed on an
    /// arbitrary axis walks half the army off a cliff on half the maps; not setting one yet just
    /// leaves units at the door, and the manager re-asks every frame until an enemy is visible.
    /// </remarks>
    public static Vector3? RallyPoint(in Vector3 producerPosition, Vector3? towards, float distance)
    {
        if (towards is null || distance <= 0.0f)
        {
            return null;
        }

        var delta = towards.Value - producerPosition;
        var length = delta.Length();

        if (length <= float.Epsilon)
        {
            return null;
        }

        // Never overshoot the target: a rally point past the enemy centroid is a suicide order.
        var travel = length < distance ? length : distance;

        return producerPosition + (delta / length * travel);
    }
}

/// <summary>
/// v1 production manager: trains units at the player's producer structures, gated on a
/// <see cref="UnitBudget"/> and on the economy manager's afford check.
/// </summary>
public sealed class AiProductionManager : IAiBrainManager
{
    /// <summary>Trace/report tag. Keep stable - the match report groups evidence on it.</summary>
    public const string ManagerName = "prod";

    /// <summary>
    /// Counter bumped when a producer's queue is observed to have grown after our order. THE M-c
    /// production grading key - the confirmed one, matching AiBaseManager's M-b discipline.
    /// </summary>
    public const string UnitConfirmedCounter = "prod.unit.ok";

    /// <summary>Counter bumped when a QueueUnit intent is accepted by the emitter.</summary>
    public const string UnitQueuedCounter = "prod.unit.queued";

    /// <summary>Counter bumped when the emitter refused the intent as malformed.</summary>
    public const string UnitRejectedCounter = "prod.unit.rejected";

    /// <summary>Counter bumped when a queued unit never showed up in the producer's queue.</summary>
    public const string UnitTimeoutCounter = "prod.unit.timeout";

    /// <summary>Counter bumped when the producer we queued at vanished before confirming.</summary>
    public const string UnitLostCounter = "prod.unit.lost";

    /// <summary>Counter bumped when a rally point is set on a producer (once per producer).</summary>
    public const string RallyPointCounter = "prod.rally.set";

    /// <summary>Frames between two queue attempts. ~1s at the SAGE logic rate.</summary>
    public const uint DefaultQueueCooldownFrames = 30;

    /// <summary>
    /// Frames a queued unit may go unconfirmed before the manager gives up on it. Same ~3s as
    /// AiBaseManager's construct window and for the same reason: it covers the order's couple of
    /// frames of scheduling delay with room to spare, without wedging the AI forever.
    /// </summary>
    public const uint DefaultConfirmWindowFrames = 90;

    /// <summary>Frames to wait after a no-op decision (cap reached, nothing affordable).</summary>
    public const uint DefaultIdleCooldownFrames = 30;

    /// <summary>
    /// How far in front of a producer its rally point sits, in world units. v1 placeholder: far
    /// enough that units clear the building's footprint and the door, near enough that they are
    /// still defending the base rather than walking into the enemy alone.
    /// </summary>
    public const float DefaultRallyDistance = 120.0f;

    private readonly AiOrderEmitter _emitter;
    private readonly AiEconomyManager? _economy;
    private readonly int _unitCap;
    private readonly uint _queueCooldownFrames;
    private readonly uint _confirmWindowFrames;
    private readonly float _rallyDistance;

    // Producers that already have a rally point, ascending by id. A List and a linear scan
    // rather than a HashSet on purpose: the manager contract forbids iterating unordered hash
    // collections, and a base has tens of producers at most.
    private readonly List<ObjectId> _rallied = new();

    private bool _hasPending;
    private ObjectId _pendingProducerId;
    private string _pendingTemplateName = string.Empty;
    private int _pendingQueueLength;
    private uint _pendingSinceFrame;

    private bool _gated;
    private uint _gateUntilFrame;

    private bool _disabledReported;

    /// <inheritdoc />
    public string Name => ManagerName;

    /// <summary>The budget computed on the most recent tick.</summary>
    public UnitBudget Budget { get; private set; }

    /// <summary>True while a QueueUnit has been issued and not yet resolved.</summary>
    public bool HasPendingQueue => _hasPending;

    /// <summary>The producer the pending queue targets.</summary>
    public ObjectId PendingProducerId => _pendingProducerId;

    /// <summary>The unit template the pending queue is buying.</summary>
    public string PendingTemplateName => _pendingTemplateName;

    /// <summary>QueueUnit intents accepted by the emitter over this manager's life.</summary>
    public int UnitsQueued { get; private set; }

    /// <summary>Queue growths observed after our own order - the M-c evidence.</summary>
    public int UnitsConfirmed { get; private set; }

    /// <summary>Producers given a rally point over this manager's life.</summary>
    public int RallyPointsSet { get; private set; }

    /// <summary>
    /// Builds a production manager over the brain's shared emitter.
    /// </summary>
    /// <param name="emitter">
    /// The brain's shared <see cref="AiOrderEmitter"/> - shared because the per-frame order
    /// budget belongs to the brain, not to one manager.
    /// </param>
    /// <param name="economy">
    /// The brain's economy manager, whose <see cref="AiEconomyManager.CanAfford"/> is the single
    /// reserve policy. Null falls back to a plain money comparison, so a brain assembled without
    /// an economy manager still produces rather than never affording anything.
    /// </param>
    /// <param name="unitCap">Army ceiling. Non-positive means <see cref="AiProductionPlan.DefaultUnitCap"/>.</param>
    /// <param name="queueCooldownFrames">Frames between queue attempts.</param>
    /// <param name="confirmWindowFrames">Frames a pending queue may stay unconfirmed.</param>
    /// <param name="rallyDistance">Rally-point distance in world units.</param>
    public AiProductionManager(
        AiOrderEmitter emitter,
        AiEconomyManager? economy = null,
        int unitCap = AiProductionPlan.DefaultUnitCap,
        uint queueCooldownFrames = DefaultQueueCooldownFrames,
        uint confirmWindowFrames = DefaultConfirmWindowFrames,
        float rallyDistance = DefaultRallyDistance)
    {
        ArgumentNullException.ThrowIfNull(emitter);

        _emitter = emitter;
        _economy = economy;
        _unitCap = unitCap > 0 ? unitCap : AiProductionPlan.DefaultUnitCap;
        _queueCooldownFrames = queueCooldownFrames;
        _confirmWindowFrames = confirmWindowFrames;
        _rallyDistance = rallyDistance;
    }

    /// <summary>
    /// One frame of production: resolve what is pending, set at most one rally point, then (if
    /// the gate is open and the budget allows) queue at most one unit.
    /// </summary>
    public void Update(SkirmishAIBrain brain)
    {
        ArgumentNullException.ThrowIfNull(brain);

        var world = brain.World;
        var frame = world.CurrentFrame;

        // Mod-level off switch, same policy as AiBaseManager's DisableBaseBuilding: a mod that
        // sets DisableUnitBuilding means it.
        if (world.SkirmishAIData is { DisableUnitBuilding: true })
        {
            if (!_disabledReported)
            {
                _disabledReported = true;
                Line(brain, string.Create(CultureInfo.InvariantCulture, $"f={frame} disabled=dataunitbuilding"));
            }

            return;
        }

        var producers = world.Producers;

        ResolvePending(brain, producers, frame);

        Budget = AiProductionPlan.Budget(world.OwnObjects, producers, _unitCap);

        // Rally points are cheap, happen once per producer and are not gated on money or on the
        // unit cap: a producer with no rally point delivers its units into the doorway, which
        // wedges the horde behind it. Doing it before the pending/gate checks means a producer
        // built during a long cooldown still gets one promptly.
        if (TrySetRallyPoint(brain, world, producers, frame))
        {
            return;
        }

        if (_hasPending)
        {
            return;
        }

        if (_gated && frame <= _gateUntilFrame)
        {
            return;
        }

        _gated = false;

        if (!Budget.AllowsMore)
        {
            Line(brain, string.Create(
                CultureInfo.InvariantCulture,
                $"f={frame} capped units={Budget.UnitCount} inflight={Budget.InFlight} cap={Budget.Cap}"));

            Gate(frame, DefaultIdleCooldownFrames);
            return;
        }

        TryQueue(brain, world, producers, frame);
    }

    // ---- pending-queue resolution ------------------------------------------------------

    /// <summary>
    /// Turns the pending queue attempt into one of: confirmed (M-c), lost (producer gone) or
    /// timed out.
    /// </summary>
    private void ResolvePending(SkirmishAIBrain brain, IReadOnlyList<AiProducerView> producers, uint frame)
    {
        if (!_hasPending)
        {
            return;
        }

        var producer = Find(producers, _pendingProducerId);

        if (producer is null)
        {
            // Producer destroyed or sold. Nothing to confirm against, and re-issuing against a
            // dead id would only feed OrderProcessor a stale object id.
            Resolve(brain, frame, UnitLostCounter, "lost");
            return;
        }

        if (producer.QueueLength > _pendingQueueLength)
        {
            UnitsConfirmed++;
            brain.Trace.Count(UnitConfirmedCounter);

            Line(brain, string.Create(
                CultureInfo.InvariantCulture,
                $"f={frame} trained producer={_pendingProducerId.Index} unit={_pendingTemplateName} queue={producer.QueueLength} waited={frame - _pendingSinceFrame} total={UnitsConfirmed}"));

            ClearPending();
            Gate(frame, _queueCooldownFrames);
            return;
        }

        // Inclusive window: a W-frame window lapses on the (W+1)th frame after issue.
        if (frame - _pendingSinceFrame > _confirmWindowFrames)
        {
            Resolve(brain, frame, UnitTimeoutCounter, "timeout");
        }
    }

    private void Resolve(SkirmishAIBrain brain, uint frame, string counter, string tag)
    {
        brain.Trace.Count(counter);

        Line(brain, string.Create(
            CultureInfo.InvariantCulture,
            $"f={frame} {tag} producer={_pendingProducerId.Index} unit={_pendingTemplateName} waited={frame - _pendingSinceFrame}"));

        ClearPending();
        Gate(frame, _queueCooldownFrames);
    }

    // ---- actions -----------------------------------------------------------------------

    /// <summary>
    /// Gives ONE producer a rally point, if any producer still needs one and a direction exists.
    /// Returns true when an intent was submitted (the frame's action is spent).
    /// </summary>
    /// <remarks>
    /// Once per producer for the whole match: the AI does not chase the front line with rally
    /// points, because re-issuing one every time the enemy centroid drifts would flood the order
    /// pipe with the least valuable order in the game. A producer whose rally point was set stays
    /// in <see cref="_rallied"/> even if it later disappears; re-using an object id in the same
    /// match is not something the engine does.
    /// </remarks>
    private bool TrySetRallyPoint(
        SkirmishAIBrain brain,
        IAiWorldView world,
        IReadOnlyList<AiProducerView> producers,
        uint frame)
    {
        var target = ChooseUnralliedProducer(producers);
        if (target is null)
        {
            return false;
        }

        var rally = AiProductionPlan.RallyPoint(
            target.Position,
            AiProductionPlan.Centroid(world.EnemyObjects),
            _rallyDistance);

        if (rally is null)
        {
            // No enemy seen yet: keep the producer on the list and re-ask next frame. Not an
            // error and not traced per-frame - it is the ordinary state of the first minute.
            return false;
        }

        var accepted = _emitter.SetRallyPoint(target.Id, rally.Value);

        // Marked either way: a rejected SetRallyPoint means malformed arguments, and the emitter
        // contract says a manager must not retry the same arguments.
        _rallied.Add(target.Id);

        if (accepted)
        {
            RallyPointsSet++;
            brain.Trace.Count(RallyPointCounter);
        }

        Line(brain, string.Create(
            CultureInfo.InvariantCulture,
            $"f={frame} rally producer={target.Id.Index} accepted={(accepted ? 1 : 0)}"));

        return true;
    }

    /// <summary>Lowest-id producer that has no rally point yet, or null.</summary>
    private AiProducerView? ChooseUnralliedProducer(IReadOnlyList<AiProducerView> producers)
    {
        if (producers == null)
        {
            return null;
        }

        AiProducerView? best = null;

        for (var i = 0; i < producers.Count; i++)
        {
            var producer = producers[i];

            if (producer is null || HasRallyPoint(producer.Id))
            {
                continue;
            }

            if (best is null || producer.Id.Index < best.Id.Index)
            {
                best = producer;
            }
        }

        return best;
    }

    private bool HasRallyPoint(ObjectId id)
    {
        for (var i = 0; i < _rallied.Count; i++)
        {
            if (_rallied[i] == id)
            {
                return true;
            }
        }

        return false;
    }

    private void TryQueue(
        SkirmishAIBrain brain,
        IAiWorldView world,
        IReadOnlyList<AiProducerView> producers,
        uint frame)
    {
        var producer = AiProductionPlan.ChooseProducer(producers);

        if (producer is null)
        {
            Line(brain, string.Create(
                CultureInfo.InvariantCulture,
                $"f={frame} idle producers={(producers?.Count ?? 0)}"));

            Gate(frame, DefaultIdleCooldownFrames);
            return;
        }

        var unit = AiProductionPlan.ChooseUnit(producer, cost => CanAfford(world, cost));

        if (unit is null)
        {
            Line(brain, string.Create(
                CultureInfo.InvariantCulture,
                $"f={frame} wait producer={producer.Id.Index} options={producer.Trainable.Count} money={world.Money}"));

            Gate(frame, DefaultIdleCooldownFrames);
            return;
        }

        var pick = unit.Value;
        var accepted = _emitter.QueueUnit(producer.Id, pick.DefinitionId);

        if (!accepted)
        {
            brain.Trace.Count(UnitRejectedCounter);

            Line(brain, string.Create(
                CultureInfo.InvariantCulture,
                $"f={frame} rejected producer={producer.Id.Index} unit={pick.TemplateName}"));

            Gate(frame, _queueCooldownFrames);
            return;
        }

        UnitsQueued++;
        brain.Trace.Count(UnitQueuedCounter);

        _hasPending = true;
        _pendingProducerId = producer.Id;
        _pendingTemplateName = pick.TemplateName;
        _pendingQueueLength = producer.QueueLength;
        _pendingSinceFrame = frame;

        Line(brain, string.Create(
            CultureInfo.InvariantCulture,
            $"f={frame} queue producer={producer.Id.Index} unit={pick.TemplateName} cost={pick.Cost} horde={(pick.IsHorde ? 1 : 0)} budget={Budget.Headroom}"));
    }

    // ---- helpers -----------------------------------------------------------------------

    private static AiProducerView? Find(IReadOnlyList<AiProducerView> producers, ObjectId id)
    {
        if (producers == null)
        {
            return null;
        }

        for (var i = 0; i < producers.Count; i++)
        {
            if (producers[i] is { } producer && producer.Id == id)
            {
                return producer;
            }
        }

        return null;
    }

    private bool CanAfford(IAiWorldView world, int cost)
        => _economy is not null ? _economy.CanAfford(cost) : world.Money >= cost;

    private void ClearPending()
    {
        _hasPending = false;
        _pendingProducerId = default;
        _pendingTemplateName = string.Empty;
        _pendingQueueLength = 0;
        _pendingSinceFrame = 0;
    }

    /// <summary>Closes the decision gate until <c>frame + frames</c> has been passed.</summary>
    private void Gate(uint frame, uint frames)
    {
        _gated = true;
        _gateUntilFrame = frame + frames;
    }

    private void Line(SkirmishAIBrain brain, string message) => brain.Trace.Line(Name, message);
}
