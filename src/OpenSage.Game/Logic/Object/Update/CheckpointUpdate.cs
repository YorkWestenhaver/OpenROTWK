// CheckpointUpdate - R12 port, translated from generals-gpl CheckpointUpdate.cpp/.h (GPL
// semantics reference; api-freeze-v1 §6 / template v1.1, direct analog of the R9
// EnemyNearUpdate port - same scan-and-flag shape, extended with a second (ally) scan and a
// collision-radius animation).
//
// Behavioral facts translated from the GPL source:
//   - state is { enemyScanDelay (a countdown), enemyNear, allyNear, maxMinorRadius }.
//   - ctor: capture the object's current geometry minor radius as maxMinorRadius (GPL reads
//     getGeometryInfo().getMinorRadius() once, assuming the object starts fully "closed"),
//     then bias the first scan by a logic-RNG draw in [0, scanDelayTime] frames so a crowd of
//     checkpoints does not all scan on the same frame (GPL "bias a random amount so everyone
//     doesn't spike at once", GameLogicRandomValue(0, m_enemyScanDelayTime)) - same stagger
//     shape as EnemyNearUpdate.
//   - update() every frame (GPL returns UPDATE_SLEEP_NONE):
//       1. remember the prior allyNear/enemyNear:
//       2. checkForAlliesAndEnemies(): on the periodic scan window, look within the object's
//          current vision range for the closest enemy and the closest ally (GPL
//          findClosestEnemy(obj, visionRange, 0) / findClosestAlly(obj, visionRange, 0), both
//          rejecting buildings, neither restricted to line-of-sight since CAN_SEE is not set
//          in the qualifier mask (0) - see F-CKU-1/F-CKU-2 below for the parts genuinely
//          unmodeled). Sets allyNear/enemyNear to whether either search found something.
//          NOTE (F-CKU-3, deliberate deviation from the GPL text): the retail source reads
//          `if (m_enemyScanDelay == 0 || TRUE)`, an unconditional-true guard that makes the
//          scan run every single frame and leaves the decrement branch dead code, silently
//          discarding the ScanDelayTime the module data still parses. The task's own contract
//          (ScanDelayTime respected) and every other scan-delay module in this codebase
//          (EnemyNearUpdate) implement the throttle as designed, so this port applies the
//          throttle EnemyNearUpdate already establishes rather than replicating the retail
//          typo. Filed as a finding, not invented: the delay field's INI default and semantics
//          are otherwise translated verbatim.
//       3. open = allyNear && !enemyNear. On a change in either flag: clear-and-set the
//          Door1Opening/Door1Closing model condition pair (GPL
//          clearAndSetModelConditionState, "for now assumes at most one door" - client-side
//          presentation output, not sim CRC state, same rationale as EnemyNearUpdate's
//          ENEMY_NEAR flag).
//       4. radius animation (GPL's literal ±0.333f/frame step, translated to Fix64): while
//          open, shrink the minor radius toward zero; otherwise grow it back toward
//          maxMinorRadius. GPL clamps only at the far end each frame (radius > 0 / radius <
//          max guards) - translated the same way, one 0.333-unit step per frame, never
//          overshooting past the 0/max bound (Fix64.Max/Min clamp added since Fix64 has no
//          float-style "just clamp on next frame" slack to rely on for the CRC-relevant value).
//
// FINDINGS (behavior-fact gaps, filed not invented):
//   F-CKU-1 filterLOS: AI::findClosestEnemy / findClosestAlly under CAN_SEE reject candidates
//     the scanner has no terrain line-of-sight to; CAN_SEE is not in the qualifier mask (0)
//     that CheckpointUpdate passes, so GPL itself does not apply this filter here - nothing
//     unported.
//   F-CKU-2 filterStealth (enemy search only): AI::findClosestEnemy always appends
//     PartitionFilterStealthedAndUndetected regardless of qualifiers ("goes last" comment,
//     AI.cpp). Stealth/detection state is not exposed to a [SimState] module (same gap as
//     EnemyNearUpdate's F-ENU-2); not modeled here either.
//   F-CKU-3 the retail `|| TRUE` scan-delay bypass - see the ctor/update note above.
//
// Every mutable sim field appears in Xfer exactly once (§3); tolerances are the field's
// conformance class at its declaration site (§4). Field order mirrors the GPL xfer() order
// (enemyNear, allyNear, maxMinorRadius, enemyScanDelay) so a save-file archaeologist can read
// the two side by side.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class CheckpointUpdate : UpdateModule
{
    private readonly CheckpointUpdateModuleData _data;

    /// <summary>The GPL literal 0.333f-per-frame radius animation step.</summary>
    private static readonly Fix64 RadiusStep = Fix64.FromDecimalLiteral("0.333");

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Whether an enemy was within vision range at the last scan.</summary>
    private bool _enemyNear;

    /// <summary>Whether an ally was within vision range at the last scan.</summary>
    private bool _allyNear;

    /// <summary>
    /// The geometry minor radius captured at construction (the "fully closed" size). Not
    /// readonly: a load must be able to overwrite it, same as every other Xfer'd field (a
    /// shadow-copy target starts with its own constructor-time capture, which Load replaces).
    /// </summary>
    private Fix64 _maxMinorRadius;

    /// <summary>Frames remaining until the next scan; re-armed to ScanDelayTime.</summary>
    private LogicFrameSpan _enemyScanDelay;

    public CheckpointUpdate(GameObject gameObject, ISimContext context, CheckpointUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _maxMinorRadius = gameObject.CollisionMinorRadius;

        // GPL ctor: bias the first scan by [0, scanDelayTime] frames drawn from the context
        // logic stream (S3) so the stagger is lockstep-identical on every peer, same shape as
        // EnemyNearUpdate. (Next(lo, hi) is inclusive, matching GameLogicRandomValue.)
        if (_data.ScanDelayTime.Value > 0)
        {
            var stagger = Context.GameLogicRandom.Next(0, (int)_data.ScanDelayTime.Value);
            _enemyScanDelay = new LogicFrameSpan((uint)stagger);
        }

        // GPL update() ticks every frame (UPDATE_SLEEP_NONE); the countdown gates the scan.
        SetWakeFrame(UpdateSleepTime.None);
    }

    public override UpdateSleepTime Update()
    {
        var wasAlly = _allyNear;
        var wasEnemy = _enemyNear;

        CheckForAlliesAndEnemies();

        var changed = wasAlly != _allyNear || wasEnemy != _enemyNear;
        var open = _allyNear && !_enemyNear;

        if (changed)
        {
            if (open)
            {
                // clearAndSetModelConditionState(DOOR_1_CLOSING, DOOR_1_OPENING).
                GameObject.ClearModelConditionState(ModelConditionFlag.Door1Closing);
                GameObject.SetModelConditionState(ModelConditionFlag.Door1Opening);
            }
            else
            {
                // clearAndSetModelConditionState(DOOR_1_OPENING, DOOR_1_CLOSING).
                GameObject.ClearModelConditionState(ModelConditionFlag.Door1Opening);
                GameObject.SetModelConditionState(ModelConditionFlag.Door1Closing);
            }
        }

        AnimateRadius(open);

        return UpdateSleepTime.None;
    }

    /// <summary>GPL checkForAlliesAndEnemies: periodic vision-range ally/enemy scan.</summary>
    private void CheckForAlliesAndEnemies()
    {
        if (_enemyScanDelay != LogicFrameSpan.Zero)
        {
            _enemyScanDelay -= LogicFrameSpan.One;
            return;
        }

        _enemyScanDelay = _data.ScanDelayTime;

        // GPL sets the geometry to its max extent before scanning "or else the stretch
        // reaction to finding one will oscillate states open->closed->open", then restores
        // it afterward. The scan itself only reads vision range (unaffected by the object's
        // own collision radius), so nothing here actually depends on the temporary widen -
        // the guard is preserved as a no-op comment rather than invented machinery.
        var visionRange = GameObject.VisionRange;

        var foundEnemy = false;
        var foundAlly = false;
        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, visionRange))
        {
            if (!foundEnemy && IsVisibleEnemy(candidate))
            {
                foundEnemy = true;
            }
            else if (!foundAlly && IsVisibleAlly(candidate))
            {
                foundAlly = true;
            }

            if (foundEnemy && foundAlly)
            {
                break;
            }
        }

        _enemyNear = foundEnemy;
        _allyNear = foundAlly;
    }

    /// <summary>
    /// GPL findClosestEnemy(obj, visionRange, 0): live, on-map, enemy, non-building object
    /// other than ourselves (F-CKU-2: the retail stealth filter is unmodeled, same gap as
    /// EnemyNearUpdate).
    /// </summary>
    private bool IsVisibleEnemy(GameObject candidate)
    {
        if (candidate == GameObject)
        {
            return false;
        }

        if (candidate.IsEffectivelyDead || candidate.IsOffMap)
        {
            return false;
        }

        if (GameObject.Owner is null ||
            candidate.Owner is null ||
            !GameObject.Owner.Enemies.Contains(candidate.Owner))
        {
            return false;
        }

        if (candidate.Definition.KindOf is not null &&
            candidate.Definition.KindOf.Get(ObjectKinds.Structure))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// GPL findClosestAlly(obj, visionRange, 0): live, on-map, allied, non-building object
    /// other than ourselves.
    /// </summary>
    private bool IsVisibleAlly(GameObject candidate)
    {
        if (candidate == GameObject)
        {
            return false;
        }

        if (candidate.IsEffectivelyDead || candidate.IsOffMap)
        {
            return false;
        }

        // Same-owner is always allied (matches AutoHealBehavior's ally predicate, the closest
        // existing analog: candidate.Owner == me.Owner || candidate.Owner.Allies.Contains(me.Owner)).
        if (GameObject.Owner is null || candidate.Owner is null)
        {
            return false;
        }
        if (candidate.Owner != GameObject.Owner && !candidate.Owner.Allies.Contains(GameObject.Owner))
        {
            return false;
        }

        if (candidate.Definition.KindOf is not null &&
            candidate.Definition.KindOf.Get(ObjectKinds.Structure))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// GPL's literal radius step: open shrinks toward zero, closed grows back toward
    /// maxMinorRadius, one RadiusStep per frame, never overshooting the bound.
    /// </summary>
    private void AnimateRadius(bool open)
    {
        var radius = GameObject.CollisionMinorRadius;

        if (open)
        {
            if (radius > Fix64.Zero)
            {
                radius -= RadiusStep;
                if (radius < Fix64.Zero)
                {
                    radius = Fix64.Zero;
                }
                GameObject.SetCollisionMinorRadius(radius);
            }
        }
        else
        {
            if (radius < _maxMinorRadius)
            {
                radius += RadiusStep;
                if (radius > _maxMinorRadius)
                {
                    radius = _maxMinorRadius;
                }
                GameObject.SetCollisionMinorRadius(radius);
            }
        }
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order mirrors the GPL xfer() order (enemyNear, allyNear, maxMinorRadius,
    // enemyScanDelay).

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferBool("EnemyNear", ref _enemyNear);
        xfer.XferBool("AllyNear", ref _allyNear);
        xfer.XferFix64("MaxMinorRadius", ref _maxMinorRadius, Tolerance.Exact);
        xfer.XferFrameSpan("EnemyScanDelay", ref _enemyScanDelay, Tolerance.Exact); // frame count: Exact (A3)
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

/// <summary>
/// Allows object to open and close like a gate when a friendly object approaches it. Requires
/// <see cref="ModelConditionFlag.Door1Opening"/> and
/// <see cref="ModelConditionFlag.Door1Closing"/> condition states.
/// </summary>
[SimDataAudited]
public sealed class CheckpointUpdateModuleData : UpdateModuleData
{
    internal static CheckpointUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<CheckpointUpdateModuleData> FieldParseTable =
        new IniParseTable<CheckpointUpdateModuleData>
        {
            { "ScanDelayTime", (parser, x) => x.ScanDelayTime = parser.ParseDurationLogicFrames() },
        };

    /// <summary>
    /// Frames between ally/enemy scans (ms in INI, ceil-quantized at parse, S5). GPL default
    /// is LOGICFRAMES_PER_SECOND (1 second) = 5 frames at the 5 Hz BFME2 title rate (F6).
    /// </summary>
    public LogicFrameSpan ScanDelayTime { get; private set; } = new LogicFrameSpan(5);

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new CheckpointUpdate(gameObject, gameEngine.SimContext, this);
    }
}
