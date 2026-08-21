#nullable enable

// S9-06 (R15 L3): AiBaseManager v1 - the manager the dr-0039 guard's M-b criterion grades.
//
// M-b is "at least one successful FoundationConstruct per AI player", read off
// AiTrace.Counters[AiMatchReport.FoundationConstructCounter] ("base.foundation.ok"). This file
// is the only thing in the engine that bumps that counter, so if this manager does nothing, the
// round-1 gate fails - which is the whole point of the criterion.
//
// WHAT "SUCCESSFUL" MEANS HERE, AND WHY IT IS NOT "I SENT THE ORDER"
//
// The AI cannot see a CastleOrderResult: it submits an Order and OrderProcessor executes it a
// couple of frames later, logging any rejection but returning nothing to the sender (S9-04's
// seam is deliberately fire-and-forget so the S9-16 SimOrder swap stays a one-file change). So
// counting "base.foundation.ok" at emission time would make M-b pass on an AI that emitted a
// hundred orders and got a hundred FoundationOccupied rejections back.
//
// Instead the manager CONFIRMS: it remembers the plot it built on, and bumps the counter only
// when a later frame's snapshot shows that plot occupied. That is observable proof the sim
// accepted the order and a structure exists. The intermediate states get their own counters
// ("base.foundation.issued", ".timeout", ".lost", ".rejected") so a failing gate says which
// half broke rather than just "zero".
//
// ONE AT A TIME
//
// A frame's snapshot is stale by the time an order executes; issuing a second FoundationConstruct
// before the first shows up would target a plot the AI already spent on and would re-spend the
// money. So exactly one construct is in flight, and the next decision waits for it to confirm,
// time out, or be lost.
//
// COOLDOWN CONVENTION (matters for the tests)
//
// Every wait in this file is stored as an inclusive "not before" frame: an action taken on
// frame F with an N-frame cooldown sets the gate to F + N, and the gate opens when
// frame > F + N. An N-frame window therefore takes N + 1 frames to lapse - the T+1 convention
// the round's test discipline uses.
//
// REBUILD ON DESTROYED
//
// There is no separate rebuild path and there deliberately isn't one. When a structure dies its
// plot reappears in the next snapshot as a free build plot, the ordinary fill loop picks it up
// (lowest free plot id first), and the cooldown between constructs is what keeps a base under
// artillery from turning into an order-pipe flood.
//
// CLEAN-ROOM: the fill order, the cooldowns and the economy target here are v1 heuristics chosen
// to make a base go up, not recovered retail behaviour. .bse-driven layout is packet S9-13.

using System;
using System.Globalization;

namespace OpenSage.Logic.AI.Skirmish;

/// <summary>
/// v1 base manager: unpacks the castle, then fills its plots one structure at a time, gated on
/// the economy manager's afford check.
/// </summary>
public sealed class AiBaseManager : IAiBrainManager
{
    /// <summary>Trace/report tag. Keep stable - the match report groups evidence on it.</summary>
    public const string ManagerName = "base";

    /// <summary>Counter bumped when a construct is confirmed built. THE M-b grading key.</summary>
    public const string FoundationOkCounter = AiMatchReport.FoundationConstructCounter;

    /// <summary>Counter bumped when a FoundationConstruct order is handed to the emitter.</summary>
    public const string FoundationIssuedCounter = "base.foundation.issued";

    /// <summary>Counter bumped when the emitter refused the intent as malformed.</summary>
    public const string FoundationRejectedCounter = "base.foundation.rejected";

    /// <summary>Counter bumped when a pending construct never showed up on its plot.</summary>
    public const string FoundationTimeoutCounter = "base.foundation.timeout";

    /// <summary>Counter bumped when the plot itself vanished while a construct was pending.</summary>
    public const string FoundationLostCounter = "base.foundation.lost";

    /// <summary>Counter bumped when a CastleUnpack order is handed to the emitter.</summary>
    public const string UnpackIssuedCounter = "base.unpack.issued";

    /// <summary>
    /// Frames between two construct attempts. ~1s at the SAGE logic rate: fast enough that a
    /// castle ring fills inside a gate run, slow enough that a rejected order does not respawn
    /// sixty times a second.
    /// </summary>
    public const uint DefaultBuildCooldownFrames = 30;

    /// <summary>
    /// Frames a pending construct may go unconfirmed before the manager gives up on it. ~3s
    /// covers the order's couple-of-frames scheduling delay plus the spawn, with room to spare;
    /// giving up too early would double-spend, giving up never would wedge the AI forever.
    /// </summary>
    public const uint DefaultConfirmWindowFrames = 90;

    /// <summary>
    /// Frames between two CastleUnpack attempts. Longer than the build cooldown because the
    /// unpack has an animation to play before the plots appear, and re-asking during it is pure
    /// noise in the order pipe.
    /// </summary>
    public const uint DefaultUnpackCooldownFrames = 60;

    /// <summary>
    /// Frames to wait after a no-op decision (nothing affordable, nothing to build). Same as the
    /// build cooldown: an idle AI should re-check at the same cadence it acts at, not spin.
    /// </summary>
    public const uint DefaultIdleCooldownFrames = 30;

    private readonly AiOrderEmitter _emitter;
    private readonly AiEconomyManager? _economy;
    private readonly uint _buildCooldownFrames;
    private readonly uint _confirmWindowFrames;
    private readonly uint _unpackCooldownFrames;

    private bool _hasPending;
    private ObjectId _pendingPlotId;
    private string _pendingTemplateName = string.Empty;
    private uint _pendingSinceFrame;

    private bool _gated;
    private uint _gateUntilFrame;

    private bool _disabledReported;

    /// <inheritdoc />
    public string Name => ManagerName;

    /// <summary>True while a FoundationConstruct has been issued and not yet resolved.</summary>
    public bool HasPendingConstruct => _hasPending;

    /// <summary>The plot the pending construct targets. Meaningless when <see cref="HasPendingConstruct"/> is false.</summary>
    public ObjectId PendingPlotId => _pendingPlotId;

    /// <summary>The template the pending construct is placing.</summary>
    public string PendingTemplateName => _pendingTemplateName;

    /// <summary>Constructs confirmed standing on their plot over this manager's life.</summary>
    public int ConstructsConfirmed { get; private set; }

    /// <summary>FoundationConstruct intents accepted by the emitter over this manager's life.</summary>
    public int ConstructsIssued { get; private set; }

    /// <summary>CastleUnpack intents accepted by the emitter over this manager's life.</summary>
    public int UnpacksIssued { get; private set; }

    /// <summary>
    /// Builds a base manager over an emitter, and optionally the brain's economy manager.
    /// </summary>
    /// <param name="emitter">
    /// The brain's shared <see cref="AiOrderEmitter"/>. Shared on purpose: the per-frame order
    /// budget is a property of the brain, not of one manager, and two emitters would each think
    /// they owned the whole budget.
    /// </param>
    /// <param name="economy">
    /// The brain's economy manager, whose <see cref="AiEconomyManager.CanAfford"/> is the single
    /// reserve policy (S9-03). Null means "no reserve policy installed": the manager then falls
    /// back to a plain money comparison so that a brain built without an economy manager still
    /// builds, rather than silently never affording anything.
    /// </param>
    /// <param name="buildCooldownFrames">Frames between construct attempts.</param>
    /// <param name="confirmWindowFrames">Frames a pending construct may stay unconfirmed.</param>
    /// <param name="unpackCooldownFrames">Frames between unpack attempts.</param>
    public AiBaseManager(
        AiOrderEmitter emitter,
        AiEconomyManager? economy = null,
        uint buildCooldownFrames = DefaultBuildCooldownFrames,
        uint confirmWindowFrames = DefaultConfirmWindowFrames,
        uint unpackCooldownFrames = DefaultUnpackCooldownFrames)
    {
        ArgumentNullException.ThrowIfNull(emitter);

        _emitter = emitter;
        _economy = economy;
        _buildCooldownFrames = buildCooldownFrames;
        _confirmWindowFrames = confirmWindowFrames;
        _unpackCooldownFrames = unpackCooldownFrames;
    }

    /// <summary>
    /// One frame of base building: resolve what is pending, then (if the gate is open) either
    /// unpack the castle or fill one plot.
    /// </summary>
    public void Update(SkirmishAIBrain brain)
    {
        ArgumentNullException.ThrowIfNull(brain);

        var world = brain.World;
        var frame = world.CurrentFrame;

        // Mod-level off switch. AotR ships SkirmishAIData; a mod that sets DisableBaseBuilding
        // means it, and an AI that ignored it would fight the mod's own scripted base.
        if (world.SkirmishAIData is { DisableBaseBuilding: true })
        {
            if (!_disabledReported)
            {
                _disabledReported = true;
                Line(brain, string.Create(CultureInfo.InvariantCulture, $"f={frame} disabled=databasebuilding"));
            }

            return;
        }

        ResolvePending(brain, world, frame);

        // One at a time: a construct still in flight owns the manager until it resolves.
        if (_hasPending)
        {
            return;
        }

        if (_gated && frame <= _gateUntilFrame)
        {
            return;
        }

        _gated = false;

        var packed = BasePlotPlan.FindPackedCastle(world.Plots);
        if (packed is not null)
        {
            TryUnpack(brain, frame, packed.Value);
            return;
        }

        TryBuild(brain, world, frame);
    }

    // ---- pending-construct resolution --------------------------------------------------

    /// <summary>
    /// Turns the pending construct into one of: confirmed (M-b), lost (plot gone) or timed out.
    /// </summary>
    private void ResolvePending(SkirmishAIBrain brain, IAiWorldView world, uint frame)
    {
        if (!_hasPending)
        {
            return;
        }

        var plots = world.Plots;
        var found = false;
        var occupied = false;

        for (var i = 0; i < plots.Count; i++)
        {
            if (plots[i].Id == _pendingPlotId)
            {
                found = true;
                occupied = plots[i].IsOccupied;
                break;
            }
        }

        if (!found)
        {
            // The plot itself is gone (destroyed, or the castle repacked). Nothing to confirm
            // against, and re-issuing against a dead id would only feed OrderProcessor a stale
            // object id - the exact hazard S9-04's header calls out.
            Resolve(brain, frame, FoundationLostCounter, "lost");
            return;
        }

        if (occupied)
        {
            ConstructsConfirmed++;
            brain.Trace.Count(FoundationOkCounter);

            Line(brain, string.Create(
                CultureInfo.InvariantCulture,
                $"f={frame} built plot={_pendingPlotId.Index} template={_pendingTemplateName} waited={frame - _pendingSinceFrame} total={ConstructsConfirmed}"));

            ClearPending();
            Gate(frame, _buildCooldownFrames);
            return;
        }

        // Inclusive window: a W-frame window lapses on the (W+1)th frame after issue.
        if (frame - _pendingSinceFrame > _confirmWindowFrames)
        {
            Resolve(brain, frame, FoundationTimeoutCounter, "timeout");
        }
    }

    private void Resolve(SkirmishAIBrain brain, uint frame, string counter, string tag)
    {
        brain.Trace.Count(counter);

        Line(brain, string.Create(
            CultureInfo.InvariantCulture,
            $"f={frame} {tag} plot={_pendingPlotId.Index} template={_pendingTemplateName} waited={frame - _pendingSinceFrame}"));

        ClearPending();
        Gate(frame, _buildCooldownFrames);
    }

    // ---- actions -----------------------------------------------------------------------

    /// <summary>
    /// Unpacks the castle anchor. No affordability check here on purpose: the unpack cost lives
    /// in the CastleBehavior's matched faction entry, which is sim-side data the world view does
    /// not surface, and CastleOrderHandler already refuses the order with CannotAfford. Retrying
    /// after the cooldown is therefore the correct behaviour for a broke AI, not a bug.
    /// </summary>
    private void TryUnpack(SkirmishAIBrain brain, uint frame, AiPlotView anchor)
    {
        var accepted = _emitter.UnpackCastle(anchor.Id);

        if (accepted)
        {
            UnpacksIssued++;
            brain.Trace.Count(UnpackIssuedCounter);
        }

        Line(brain, string.Create(
            CultureInfo.InvariantCulture,
            $"f={frame} unpack castle={anchor.Id.Index} template={anchor.TemplateName} accepted={(accepted ? 1 : 0)}"));

        Gate(frame, _unpackCooldownFrames);
    }

    private void TryBuild(SkirmishAIBrain brain, IAiWorldView world, uint frame)
    {
        var choice = BasePlotPlan.Choose(
            world.Plots,
            world.BuildableStructures,
            world.OwnObjects,
            world.Money,
            world.SkirmishAIData,
            world.DifficultyTuning);

        if (choice is null)
        {
            Line(brain, string.Create(
                CultureInfo.InvariantCulture,
                $"f={frame} idle plots={world.Plots.Count} templates={world.BuildableStructures.Count}"));

            Gate(frame, DefaultIdleCooldownFrames);
            return;
        }

        var pick = choice.Value;

        if (!CanAfford(world, pick.Template.Cost))
        {
            Line(brain, string.Create(
                CultureInfo.InvariantCulture,
                $"f={frame} wait template={pick.Template.TemplateName} cost={pick.Template.Cost} money={world.Money}"));

            Gate(frame, DefaultIdleCooldownFrames);
            return;
        }

        var accepted = _emitter.ConstructOnFoundation(pick.PlotId, pick.Template.DefinitionId);

        if (!accepted)
        {
            // Malformed arguments (invalid plot id, non-positive definition id). Per the
            // emitter's contract a manager must NOT retry the same arguments, so this takes the
            // cooldown and re-plans from the next snapshot.
            brain.Trace.Count(FoundationRejectedCounter);

            Line(brain, string.Create(
                CultureInfo.InvariantCulture,
                $"f={frame} rejected plot={pick.PlotId.Index} template={pick.Template.TemplateName}"));

            Gate(frame, _buildCooldownFrames);
            return;
        }

        ConstructsIssued++;
        brain.Trace.Count(FoundationIssuedCounter);

        _hasPending = true;
        _pendingPlotId = pick.PlotId;
        _pendingTemplateName = pick.Template.TemplateName;
        _pendingSinceFrame = frame;

        Line(brain, string.Create(
            CultureInfo.InvariantCulture,
            $"f={frame} build plot={pick.PlotId.Index} template={pick.Template.TemplateName} cost={pick.Template.Cost} why={pick.Reason}"));
    }

    // ---- helpers -----------------------------------------------------------------------

    /// <summary>
    /// The afford check. Goes through <see cref="AiEconomyManager.CanAfford"/> when the brain has
    /// an economy manager, so the reserve policy lives in exactly one place (S9-03).
    /// </summary>
    private bool CanAfford(IAiWorldView world, int cost)
        => _economy is not null ? _economy.CanAfford(cost) : world.Money >= cost;

    private void ClearPending()
    {
        _hasPending = false;
        _pendingPlotId = default;
        _pendingTemplateName = string.Empty;
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
