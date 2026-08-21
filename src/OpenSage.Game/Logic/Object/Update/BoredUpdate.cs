// BoredUpdate - R13 port. Re-audit resolved the phase-1 block (bfme2-workbench/research/
// modules-r13/specs/BoredUpdateModuleData.md §0): composed from two landed idioms, not a single
// GPL translation target. generals-gpl's EnemyNearUpdate (Include/GameLogic/Module/
// EnemyNearUpdate.h, Source/GameLogic/Object/Update/EnemyNearUpdate.cpp) confirms the same
// "periodic delayed scan -> boolean edge -> do X" cadence idiom (same ScanDelayTime field name)
// but has no SpecialPowerTemplate firing and no explicit ScanDistance/ObjectFilter of its own -
// precedent for the cadence only, not a direct translation target. The scan half is a
// near-identical sibling to AutoPickUpUpdate (same ScanDelayTime/ScanDistance/<X>Filter
// vocabulary, attached to the same objects, e.g. cavetroll.ini), and the activation seam
// (PendingActivationTargetId / TryConsumePendingActivation) is copied verbatim from
// AutoAbilityBehavior, the only landed module that establishes a module autonomously deciding to
// activate its own SpecialPowerTemplate.
//
// Derived behavior (data-derivable composition, no invented mechanic - see spec §1):
//   1. Scan: every ScanDelayTime, scan Context.Partition.QueryObjectsInRadius(self, ScanDistance)
//      (ascending ObjectId, the frozen S3 partition-order convention) for the first candidate,
//      other than self and not destroyed, that passes BoredFilter.Matches.
//   2. Polarity - F-BU-1 (data-derived, not an assumption): a BoredFilter match FIRES
//      SpecialPowerTemplate; the absence of a match does nothing (module re-arms and waits). Only
//      coherent reading of trollheroes.ini:684-690 (BoredFilter=+TrollishStew ->
//      SpecialAbilityWildTrollCooking) and cavetroll.ini:984-989 (BoredFilter=+<troll kinds> ->
//      SpecialAbilityMountainTrollBored) - a "fire when nothing matches" polarity would mean the
//      troll cooks precisely when there is no stew to cook, incoherent against the field's own
//      name.
//   3. Activation target - F-BU-3 (composition choice, not GPL/data-sourced): the scanned
//      candidate is the eligibility gate, not necessarily the power's target - cooking is
//      something the troll itself performs, not an ability cast onto the stew object. Mirrors
//      AutoAbilityBehavior's own AllowSelf self-target composition: the activation is recorded as
//      targeting self (GameObject.Id) once a BoredFilter match is found. No corpus/GPL evidence
//      names an alternative (targeting the found candidate); revisit if one surfaces - a one-line
//      change, no structural rework.
//   4. Re-arm: always re-arms ScanDelayTime frames later regardless of outcome (matches
//      AutoPickUpUpdate's own unconditional re-arm). A found match overwrites any
//      not-yet-consumed PendingActivationTargetId rather than suppressing the scan while one is
//      pending - mirrors AutoAbilityBehavior's own unconditional re-arm-on-cadence.
//
// FINDINGS (filed, not invented):
//   F-BU-1 (BoredFilter polarity - match found fires): see above.
//   F-BU-2 (CanScanWhileAttackingOrMoving - real parse gap, parsed not modeled): authored in live
//     AotR data (trollheroes.ini:688,1429,2128; sibling usage cavetroll.ini:1209) but absent from
//     the engine entirely prior to this port. No idle/attacking AI-state facade exists on
//     GameObject (api-freeze-v1.md §7, AIUpdate deliberately unfrozen) - same category of gap
//     AutoAbilityBehavior already filed and shipped for IdleTimeSeconds. Parsed and Xfer'd for
//     round-trip fidelity; the scan always behaves as if the gate passes.
//   F-BU-3 (activation target = self, not the scanned candidate): see above. Port-review judgment
//     call, not a decoded fact.
//
// S5 parser-type fixes (same class as AutoPickUpUpdate's own R13 fix): ScanDelayTime ->
// ParseDurationLogicFrames (LogicFrameSpan), ScanDistance -> ParseFix64 (deterministic S3-query
// radius, exactly AutoPickUpUpdate.ScanDistance's / EmotionTrackerUpdate.FearScanDistance's own
// S5 fix - same field, same conversion, third module to need it).

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class BoredUpdate : UpdateModule
{
    private readonly BoredUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>ObjectId of the pending activation's target (always self, F-BU-3), or invalid
    /// if none is pending. Never cleared by Update() itself - only by
    /// TryConsumePendingActivation - so a save/load mid-pending-activation round-trips
    /// correctly, same seam as AutoAbilityBehavior.</summary>
    private ObjectId _pendingActivationTargetId;

    public BoredUpdate(GameObject gameObject, ISimContext context, BoredUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        if (_data.ScanDelayTime.Value > 0 && _data.ScanDistance > Fix64.Zero)
        {
            SetWakeFrame(UpdateSleepTime.Frames(_data.ScanDelayTime));
        }
        else
        {
            SetWakeFrame(UpdateSleepTime.Forever);
        }
    }

    /// <summary>ObjectId of the pending activation's target, or invalid if none is pending.</summary>
    public ObjectId PendingActivationTargetId => _pendingActivationTargetId;

    public override UpdateSleepTime Update()
    {
        var candidate = FindBoredCandidate();
        if (candidate != null)
        {
            // F-BU-3: the match is the eligibility gate, not the target - activation always
            // targets self.
            _pendingActivationTargetId = GameObject.Id;
        }

        return UpdateSleepTime.Frames(_data.ScanDelayTime);
    }

    /// <summary>
    /// Consumes a pending activation (if any), returning the target. Called by a future
    /// order-pipeline wiring task (no landed caller exists yet - same posture as
    /// AutoAbilityBehavior.TryConsumePendingActivation).
    /// </summary>
    public bool TryConsumePendingActivation(out ObjectId targetId)
    {
        if (!_pendingActivationTargetId.IsValid)
        {
            targetId = ObjectId.Invalid;
            return false;
        }

        targetId = _pendingActivationTargetId;
        _pendingActivationTargetId = ObjectId.Invalid;
        return true;
    }

    /// <summary>F-BU-1: first BoredFilter match in range fires the power (match-found-fires,
    /// data-derived from trollheroes.ini/cavetroll.ini). Ascending-ObjectId partition order,
    /// same as AutoPickUpUpdate.</summary>
    private GameObject FindBoredCandidate()
    {
        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _data.ScanDistance))
        {
            if (candidate == GameObject || candidate.IsDestroyed)
            {
                continue;
            }

            if (_data.BoredFilter != null && !_data.BoredFilter.Matches(candidate))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    // ---- the single walk (F8 Objects channel; declaration order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferObjectId("PendingActivationTargetId", ref _pendingActivationTargetId); // identity field: Exact
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

[SimDataAudited]
[AddedIn(SageGame.Bfme2)]
public sealed class BoredUpdateModuleData : UpdateModuleData
{
    internal static BoredUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<BoredUpdateModuleData> FieldParseTable = new IniParseTable<BoredUpdateModuleData>
    {
        // S5: ms -> logic frames (same duration-field convention as AutoPickUpUpdate.ScanDelayTime).
        { "ScanDelayTime", (parser, x) => x.ScanDelayTime = parser.ParseDurationLogicFrames() },
        // S5: deterministic S3-query radius -> Fix64, exactly AutoPickUpUpdate.ScanDistance's /
        // EmotionTrackerUpdate.FearScanDistance's own S5 fix.
        { "ScanDistance", (parser, x) => x.ScanDistance = parser.ParseFix64() },
        { "BoredFilter", (parser, x) => x.BoredFilter = ObjectFilter.Parse(parser) },
        { "SpecialPowerTemplate", (parser, x) => x.SpecialPowerTemplate = parser.ParseAssetReference() },
        // F-BU-2: real parse gap - authored in live AotR data (trollheroes.ini:688,1429,2128;
        // cavetroll.ini:1209 on the sibling AutoPickUpUpdate) but absent from the engine
        // entirely. Parsed for round-trip fidelity; no idle/attacking AI-state facade exists on
        // GameObject to wire it to (api-freeze-v1 §7, AIUpdate deliberately unfrozen) - same
        // gap AutoAbilityBehavior already filed for IdleTimeSeconds.
        { "CanScanWhileAttackingOrMoving", (parser, x) => x.CanScanWhileAttackingOrMoving = parser.ParseBoolean() },
    };

    /// <summary>Frames between scans (ms in INI, ceil-quantized at parse, S5).</summary>
    public LogicFrameSpan ScanDelayTime { get; private set; }

    /// <summary>Deterministic S3-query radius (S5).</summary>
    public Fix64 ScanDistance { get; private set; }

    public ObjectFilter BoredFilter { get; private set; }

    public string SpecialPowerTemplate { get; private set; }

    /// <summary>F-BU-2: parsed for authoring round-trip fidelity; not wired into the scan gate
    /// (no idle/attacking AI-state facade exists on GameObject). The module always behaves as
    /// if this is true.</summary>
    public bool CanScanWhileAttackingOrMoving { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new BoredUpdate(gameObject, gameEngine.SimContext, this);
    }
}
