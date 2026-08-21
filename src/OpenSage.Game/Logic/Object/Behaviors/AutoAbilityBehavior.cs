// AutoAbilityBehavior - R13 port (api-freeze-v1 §6 / template v1.1; see
// bfme2-workbench/research/modules-r13/specs/AutoAbilityBehaviorModuleData.md for the full
// port spec this file implements).
//
// Behavioral reference: generals-gpl carries no AutoAbilityBehavior.cpp/.h (BFME2-only class,
// confirmed by grep); the closest GPL relative is AutoHealBehavior.cpp/.h, whose upgrade-gated,
// periodically-ticking, radius-scan shape this port's structure mirrors (AutoHealBehavior.cs is
// the canonical Round-4 port template - api-freeze-v1 §5). This module's own INI vocabulary
// (SpecialAbility/BaseMaxRangeFromStartPos/AdjustAttackMeleePosition/MaxScanRange/MinScanRange/
// AllowSelf/IdleTimeSeconds/Query/ForbiddenStatus) carries none of AutoHealBehavior's fields, so
// none of its healing-specific machinery is invented here - this is fresh code against the
// frozen contract, following AutoHealBehavior's shape only for the upgrade-mux + scan-loop
// idiom.
//
// BASE CLASS CORRECTION (spec §2.1): the pre-port class inherited UpgradeModuleData, but
// UpgradeModule (Upgrade/UpgradeModule.cs) has no per-frame Update() hook - only the one-shot
// OnUpgrade() callback. This module needs a periodic idle-scan loop, so it is UpdateModuleData
// instead, hand-composing upgrade-gating via an owned UpgradeLogicData field, exactly as
// AutoHealBehaviorModuleData already does.
//
// STATE MACHINE (engineering choice, not a GPL translation): once the upgrade mux triggers,
// Update() ticks every frame (cheap phase-machine idiom, same as ToggleHiddenSpecialAbilityUpdate)
// and gates the actual scan behind a re-armable NextScanFrame timer. On a scan, the module
// queries [MinScanRange, MaxScanRange] (an annulus, using the generous-superset-then-point-filter
// idiom SpectreGunshipUpdate.cs's header documents) for a candidate that (a) is not carrying
// ForbiddenStatus and (b) matches at least one configured Query's ObjectFilter (first-match-in-
// list-order per §2.3/§5 gap 2 - no spec exists for a different tie-break). AllowSelf (direct
// analog to AutoHealBehaviorModuleData.SkipSelfForHealing, polarity inverted) additionally makes
// the object itself eligible, checked ahead of the radius scan since the partition query never
// returns the querying object itself.
//
// PARSED, NOT MODELED (audited gaps, not invented - spec §2.5):
//   - IdleTimeSeconds' "idle" gate: no order/AI-state concept exists on GameObject or
//     ISimContext (api-freeze-v1 §7: the AIUpdate sub-surface is deliberately unfrozen). The
//     module therefore behaves as if always-eligible-to-scan once upgraded. IdleTimeSeconds is
//     reused instead as the only timing field this module owns: the fixed re-scan cadence
//     between scan attempts (a port-review judgment call, not a GPL/Ghidra-encoded fact).
//   - BaseMaxRangeFromStartPos' leash range reuses MaxScanRange for lack of a dedicated field
//     (no spec states which range governs the leash) - see IsWithinLeash below.
//   - AdjustAttackMeleePosition: no melee-approach/positioning system exists on this seam (the
//     AI/locomotor "move into melee range" behavior lives in the unfrozen AIUpdate sub-surface).
//     Parsed and held, exposed read-only via AdjustsAttackMeleePosition.
//   - SpecialAbility (the SpecialPowerTemplate to fire): there is no landed Fix64/[SimState]
//     dispatch path from module code into the pre-SimCore float SpecialPowerModule system
//     (Logic/Object/SpecialPower/*.cs is still float-substrate - ISimContext.cs:58's own
//     "FixVector3 transform port - that is a finding, not a cast" flags this class of gap).
//     This module owns only the *decision* (when + at what target), exposed as
//     PendingActivationTargetId / TryConsumePendingActivation - a driven, Xfer'd seam with no
//     landed caller yet, same posture as ToggleHiddenSpecialAbilityUpdate.
//     InitiateIntentToDoSpecialPower / ReplaceObjectUpdate's identical seam (mirrored for the
//     opposite call direction).
//
// Every mutable sim field appears in Xfer exactly once (§3); tolerances are the field's
// conformance class at its declaration site (§4).

using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class AutoAbilityBehavior : UpdateModule, IUpgradeableModule
{
    private readonly AutoAbilityBehaviorModuleData _data;
    private readonly UpgradeLogic _upgradeLogic;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>
    /// Captured once at construction (before any movement): the anchor BaseMaxRangeFromStartPos
    /// leashes against. Immutable thereafter.
    /// </summary>
    private FixVector3 _startPosition;

    /// <summary>Earliest frame the next scan attempt may run; re-armed by every scan.</summary>
    private LogicFrame _nextScanFrame;

    /// <summary>
    /// ObjectId of the pending activation's target, or invalid if none is pending. Never
    /// cleared by Update() itself - only by TryConsumePendingActivation - so a save/load
    /// mid-pending-activation round-trips correctly.
    /// </summary>
    private ObjectId _pendingActivationTargetId;

    public AutoAbilityBehavior(GameObject gameObject, ISimContext context, AutoAbilityBehaviorModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _startPosition = SimTransformBridge.PullPosition(gameObject);

        SetWakeFrame(UpdateSleepTime.Forever);

        // The mux fires OnUpgradeTriggered from its ctor when StartsActive.
        _upgradeLogic = new UpgradeLogic(data.UpgradeData, OnUpgradeTriggered);
    }

    /// <summary>ObjectId of the pending activation's target, or invalid if none is pending.</summary>
    public ObjectId PendingActivationTargetId => _pendingActivationTargetId;

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note.</summary>
    public bool AdjustsAttackMeleePosition => _data.AdjustAttackMeleePosition;

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    private void OnUpgradeTriggered()
    {
        // Start ticking every frame; the scan itself is gated by NextScanFrame below.
        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>
    /// Consumes a pending activation (if any), returning the target. Called by a future
    /// order-pipeline wiring task (no landed caller exists yet - see the file-header
    /// SpecialAbility gap note).
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

    public override UpdateSleepTime Update()
    {
        if (!_upgradeLogic.Triggered)
        {
            return UpdateSleepTime.Forever;
        }

        if (Context.CurrentFrame >= _nextScanFrame)
        {
            _nextScanFrame = Context.CurrentFrame + RescanCadence();

            if (IsWithinLeash())
            {
                var target = FindActivationTarget();
                if (target != null)
                {
                    _pendingActivationTargetId = target.Id;
                }
            }
        }

        return UpdateSleepTime.None;
    }

    /// <summary>
    /// IdleTimeSeconds is the only timing field this module owns (§2.3 of the port spec); reused
    /// as the fixed cadence between scan attempts once triggered, since no dedicated re-scan-
    /// interval field exists. Zero (the common default - none of AotR's shipped Query-bearing
    /// AutoAbilityBehavior blocks set IdleTimeSeconds) falls back to the every-frame tick, which
    /// matches "always-eligible-to-scan" from the unmodeled-idle-gate posture above.
    /// </summary>
    private LogicFrameSpan RescanCadence()
    {
        var frames = (uint)_data.IdleTimeSeconds * SimLoop.LogicFramesPerSecond;
        return frames == 0 ? UpdateSleepTime.None.FrameSpan : new LogicFrameSpan(frames);
    }

    /// <summary>
    /// BaseMaxRangeFromStartPos gates the ability's own legality (not just the scan) on
    /// distance from the captured spawn position, reusing MaxScanRange as the leash range for
    /// lack of a dedicated field (§2.5 gap 3 of the port spec).
    /// </summary>
    private bool IsWithinLeash()
    {
        if (!_data.BaseMaxRangeFromStartPos)
        {
            return true;
        }

        return FixMath.IsWithin(SimTransformBridge.PullPosition(GameObject), _startPosition, _data.MaxScanRange);
    }

    /// <summary>
    /// The range-band scan (§2.3): a min/max annulus around GameObject, plus (when AllowSelf)
    /// the object itself - checked ahead of the radius scan since Context.Partition.
    /// QueryObjectsInRadius never returns the querying object itself. First eligible candidate
    /// wins; no ordering guarantee is specified beyond the partition's own ascending-ObjectId
    /// contract.
    /// </summary>
    private GameObject FindActivationTarget()
    {
        if (_data.AllowSelf && IsEligibleSelf())
        {
            return GameObject;
        }

        if (_data.Querys.Count == 0)
        {
            return null;
        }

        var selfPosition = SimTransformBridge.PullPosition(GameObject);

        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _data.MaxScanRange))
        {
            if (_data.MinScanRange > Fix64.Zero &&
                FixMath.IsWithin(selfPosition, SimTransformBridge.PullPosition(candidate), _data.MinScanRange))
            {
                continue; // inside the dead zone
            }

            if (candidate.TestStatus(_data.ForbiddenStatus))
            {
                continue;
            }

            if (!MatchesAnyQuery(candidate))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    /// <summary>
    /// Self has no "range" from itself (AllowSelf is a binary include-self flag, the direct
    /// analog to AutoHealBehaviorModuleData.SkipSelfForHealing with inverted polarity), so the
    /// Min/MaxScanRange annulus does not apply to it - only ForbiddenStatus and the Query
    /// filters do.
    /// </summary>
    private bool IsEligibleSelf()
    {
        if (_data.Querys.Count == 0)
        {
            return false;
        }

        if (GameObject.TestStatus(_data.ForbiddenStatus))
        {
            return false;
        }

        return MatchesAnyQuery(GameObject);
    }

    /// <summary>
    /// A candidate qualifies if it satisfies any configured Query's ObjectFilter (§2.3/§5 gap
    /// 2: no spec exists for a tie-break order across multiple Query entries beyond "some
    /// order" - implemented as first-match-in-list-order, which for a pure OR predicate is
    /// observationally the only order that matters).
    /// </summary>
    private bool MatchesAnyQuery(GameObject candidate)
    {
        foreach (var query in _data.Querys)
        {
            if (query.ObjectFilter.Matches(candidate))
            {
                return true;
            }
        }

        return false;
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _upgradeLogic.Xfer(xfer);                                                     // ch.1: Exact (mux)
        xfer.XferFixVector3("StartPosition", ref _startPosition, Tolerance.Exact);    // captured once, immutable: Exact
        xfer.XferFrame("NextScanFrame", ref _nextScanFrame, Tolerance.Quantum);       // ch.2 timer
        xfer.XferObjectId("PendingActivationTargetId", ref _pendingActivationTargetId); // identity field: Exact
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================
[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class AutoAbilityBehaviorModuleData : UpdateModuleData
{
    internal static AutoAbilityBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<AutoAbilityBehaviorModuleData> FieldParseTable =
        new IniParseTableChild<AutoAbilityBehaviorModuleData, UpgradeLogicData>(x => x.UpgradeData, UpgradeLogicData.FieldParseTable)
        .Concat(new IniParseTable<AutoAbilityBehaviorModuleData>
        {
            { "SpecialAbility", (parser, x) => x.SpecialAbility = parser.ParseAssetReference() },
            { "BaseMaxRangeFromStartPos", (parser, x) => x.BaseMaxRangeFromStartPos = parser.ParseBoolean() },
            { "AdjustAttackMeleePosition", (parser, x) => x.AdjustAttackMeleePosition = parser.ParseBoolean() },
            { "MaxScanRange", (parser, x) => x.MaxScanRange = parser.ParseFix64() },
            { "MinScanRange", (parser, x) => x.MinScanRange = parser.ParseFix64() },
            { "AllowSelf", (parser, x) => x.AllowSelf = parser.ParseBoolean() },
            { "IdleTimeSeconds", (parser, x) => x.IdleTimeSeconds = parser.ParseInteger() },
            { "Query", (parser, x) => x.Querys.Add(Query.Parse(parser)) },
            { "ForbiddenStatus", (parser, x) => x.ForbiddenStatus = parser.ParseEnum<ObjectStatus>() }
        });

    public UpgradeLogicData UpgradeData { get; } = new();

    [AddedIn(SageGame.Bfme2)]
    public string SpecialAbility { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public bool BaseMaxRangeFromStartPos { get; private set; }

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note.</summary>
    [AddedIn(SageGame.Bfme2)]
    public bool AdjustAttackMeleePosition { get; private set; }

    /// <summary>
    /// Outer scan-band radius (quantized Q31.32; was int, corrected to Fix64 per F1/S5 - every
    /// other ranged-scan ModuleData in the codebase is Fix64, never int). Also reused as the
    /// leash range when BaseMaxRangeFromStartPos is set (port-review judgment call - see the
    /// file-header gap note; no dedicated leash field exists).
    /// </summary>
    [AddedIn(SageGame.Bfme2)]
    public Fix64 MaxScanRange { get; private set; }

    /// <summary>Inner dead-zone radius (quantized Q31.32; was int, corrected to Fix64 per F1/S5).</summary>
    [AddedIn(SageGame.Bfme2)]
    public Fix64 MinScanRange { get; private set; }

    /// <summary>
    /// Whether GameObject itself may satisfy a Query as a candidate. Direct structural analog
    /// to AutoHealBehaviorModuleData.SkipSelfForHealing, polarity inverted.
    /// </summary>
    [AddedIn(SageGame.Bfme2)]
    public bool AllowSelf { get; private set; }

    /// <summary>
    /// Seconds (int; kept as ParseInteger rather than ParseDurationLogicFrames since it drives
    /// no LogicFrameSpan arithmetic from its original "idle" semantics in this port - see the
    /// file-header gap note. Must become ParseDurationLogicFrames the day the idle/order-state
    /// seam lands. Reused in the meantime as the module's own re-scan cadence.
    /// </summary>
    [AddedIn(SageGame.Bfme2)]
    public int IdleTimeSeconds { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public List<Query> Querys { get; } = new List<Query>();

    [AddedIn(SageGame.Bfme2)]
    public ObjectStatus ForbiddenStatus { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new AutoAbilityBehavior(gameObject, gameEngine.SimContext, this);
    }
}

[AddedIn(SageGame.Bfme2)]
public sealed class Query
{
    internal static Query Parse(IniParser parser)
    {
        return new Query
        {
            Unknown = parser.ParseInteger(),
            ObjectFilter = ObjectFilter.Parse(parser)
        };
    }

    // Meaning unidentified (§5 gap 2 of the port spec): no spec exists for what the leading
    // integer in each Query block means (priority weight? group id? unused legacy field?). Do
    // not rename without a grounding source.
    public int Unknown { get; private set; }

    public ObjectFilter ObjectFilter { get; private set; }
}
