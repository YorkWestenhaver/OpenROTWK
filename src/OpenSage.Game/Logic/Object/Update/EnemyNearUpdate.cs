// EnemyNearUpdate - R9 port through the full task packet (api-freeze-v1 §6 / template v1.1).
//
// Behavioral reference: generals-gpl GeneralsMD EnemyNearUpdate.cpp/.h + AI::findClosestEnemy
// (GPL semantics reference only; this is fresh code against the frozen contract). Behavior
// facts used:
//   - state is exactly { enemyScanDelay (a countdown), enemyNear }.
//   - ctor: bias the first scan by a logic-RNG draw in [0, scanDelayTime] frames so a crowd
//     does not all scan on the same frame (GPL "bias a random amount so everyone doesn't
//     spike at once", GameLogicRandomValue(0, m_enemyScanDelayTime)).
//   - update() every frame (GPL returns UPDATE_SLEEP_NONE): remember the prior enemyNear,
//     run the periodic check, and on a rising edge set the ENEMY_NEAR model condition, on a
//     falling edge clear it. The model condition is a client-side presentation output (GPL
//     drives it through the Drawable) - it is not folded into the sim CRC.
//   - checkForEnemies(): when the countdown reaches 0, re-arm it to scanDelayTime and scan;
//     otherwise decrement. The scan is getVisionRange() + findClosestEnemy(me, range, CAN_SEE):
//     enemyNear is true iff any live, on-map, enemy, non-building object is within the object's
//     current vision range.
//
// The scan consumes the LANDED systems only (task packet): the S3 partition query via the
// stable ISimContext.Partition seam (QueryObjectsInRadius, ascending ObjectId), and the
// object's vision range as Fix64 via the GameObject Fix64 facade (GPL getVisionRange, the same
// D-7 boundary shape as AttemptHealing/SetMaxHealth). No S6/S7.
//
// FINDINGS (behavior-fact gaps, filed not invented - see modules-r9/EnemyNearUpdate.md):
//   F-ENU-1 filterLOS (line-of-sight): AI::findClosestEnemy under CAN_SEE rejects enemies the
//     scanner has no terrain line-of-sight to. SimPartitionGrid.IsClearLineOfSightTerrain
//     exists (S3) but is NOT reachable through the module-facing ISimContext.Partition seam,
//     and the seam still routes the float quadtree, not the grid. LOS is therefore not modeled
//     here (every in-range enemy is treated as seen); a seam growth is required.
//   F-ENU-2 filterStealth (stealthed-and-undetected): rejected enemies that are stealthed and
//     not detected by the scanner's team. Stealth/detection state is not exposed to a
//     [SimState] module; not modeled.
//   F-ENU-3 "buildings that can attack" exception: GPL's filterRejectBuildings keeps a building
//     that is able to attack; we reject all STRUCTURE-kind objects (isAbleToAttack is unported).
//
// Every mutable sim field appears in Xfer exactly once (§3); tolerances are the field's
// conformance class at its declaration site (§4).

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class EnemyNearUpdate : UpdateModule
{
    private readonly EnemyNearUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Frames remaining until the next enemy scan; re-armed to ScanDelayTime.</summary>
    private LogicFrameSpan _enemyScanDelay;

    /// <summary>Whether an enemy was within vision range at the last scan.</summary>
    private bool _enemyNear;

    public EnemyNearUpdate(GameObject gameObject, ISimContext context, EnemyNearUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // GPL ctor: bias the first scan by [0, scanDelayTime] frames drawn from the context
        // logic stream (S3) so the stagger is lockstep-identical on every peer. (Next(lo, hi)
        // is inclusive, matching GameLogicRandomValue.) The degenerate zero-delay case skips
        // the draw - see finding F-ENU-4.
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
        var enemyWasNear = _enemyNear;

        CheckForEnemies();

        if (_enemyNear && !enemyWasNear)
        {
            // Rising edge: switch the art to its "enemy near" state (client output).
            GameObject.SetModelConditionState(ModelConditionFlag.EnemyNear);
        }
        else if (!_enemyNear && enemyWasNear)
        {
            // Falling edge: return to idle art (client output).
            GameObject.ClearModelConditionState(ModelConditionFlag.EnemyNear);
        }

        return UpdateSleepTime.None;
    }

    /// <summary>GPL checkForEnemies: periodic vision-range enemy scan.</summary>
    private void CheckForEnemies()
    {
        if (_enemyScanDelay == LogicFrameSpan.Zero)
        {
            _enemyScanDelay = _data.ScanDelayTime;

            var found = false;
            foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, GameObject.VisionRange))
            {
                if (IsVisibleEnemy(candidate))
                {
                    found = true;
                    break;
                }
            }

            _enemyNear = found;
        }
        else
        {
            _enemyScanDelay -= LogicFrameSpan.One;
        }
    }

    /// <summary>
    /// The findClosestEnemy(me, range, CAN_SEE) predicate, minus the unmodeled LOS/stealth
    /// filters (F-ENU-1/-2): a live, on-map, enemy, non-building object other than ourselves.
    /// </summary>
    private bool IsVisibleEnemy(GameObject candidate)
    {
        if (candidate == GameObject)
        {
            // The partition query already excludes the center; belt-and-suspenders.
            return false;
        }

        // GPL PartitionFilterLiveMapEnemies: live and on-map.
        if (candidate.IsEffectivelyDead || candidate.IsOffMap)
        {
            return false;
        }

        // Enemies only. Consumes the same Player relationship set AutoHealBehavior consumes
        // for its ally check (Owner.Enemies is the mirror of Owner.Allies); the dual
        // relationship representations in Player are a reconciliation finding (F-ENU-5).
        if (GameObject.Owner is null ||
            candidate.Owner is null ||
            !GameObject.Owner.Enemies.Contains(candidate.Owner))
        {
            return false;
        }

        // GPL PartitionFilterRejectBuildings (ATTACK_BUILDINGS not set under CAN_SEE): never
        // count buildings. The "building that can attack" exception is unmodeled (F-ENU-3).
        if (candidate.Definition.KindOf is not null &&
            candidate.Definition.KindOf.Get(ObjectKinds.Structure))
        {
            return false;
        }

        return true;
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferFrameSpan("EnemyScanDelay", ref _enemyScanDelay, Tolerance.Exact); // frame count: Exact (A3)
        xfer.XferBool("EnemyNear", ref _enemyNear);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================
[SimDataAudited]
public sealed class EnemyNearUpdateModuleData : UpdateModuleData
{
    internal static EnemyNearUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<EnemyNearUpdateModuleData> FieldParseTable =
        new IniParseTable<EnemyNearUpdateModuleData>
        {
            { "ScanDelayTime", (parser, x) => x.ScanDelayTime = parser.ParseDurationLogicFrames() },
        };

    /// <summary>
    /// Frames between enemy scans (ms in INI, ceil-quantized at parse, S5). GPL default is
    /// LOGICFRAMES_PER_SECOND (1 second) = 5 frames at the 5 Hz BFME2 title rate (F6).
    /// </summary>
    public LogicFrameSpan ScanDelayTime { get; private set; } = new LogicFrameSpan(5);

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new EnemyNearUpdate(gameObject, gameEngine.SimContext, this);
    }
}
