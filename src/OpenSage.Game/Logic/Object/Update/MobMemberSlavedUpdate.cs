// MobMemberSlavedUpdate - R10 port through the full task packet (api-freeze-v1 §6 /
// template v1.1). "Will obey spawner... or die trying": a swarm/mob member that keeps itself
// caught up to its mob leader (the "nexus"), snapping to PANIC and heading for the leader when
// it strays too far, and self-destructing if it stays critically far for too long (the
// failsafe that stops an isolated member from making the whole mob effectively invincible).
//
// Behavioral reference: generals-gpl GeneralsMD + Generals MobMemberSlavedUpdate.cpp/.h (GPL
// semantics reference only; this is fresh code against the frozen contract). Behavior facts
// used from update():
//   - ctor: m_framesToWait = GameLogicRandomValue(0,20), a per-member stagger of the first
//     heavy tick so a whole mob does not run its catch-up scan on the same frame.
//   - every frame: look up the master (m_slaver) by id; if it is gone, kill self immediately
//     (this is the invincibility failsafe's first half - an orphaned member does not linger).
//   - a low-priority throttle: the heavy catch-up body runs only once every 16 frames
//     (GPL "++m_framesToWait < 16" gate), the stagger biasing which frame that is.
//   - too far (center-to-center 3D distance > MustCatchUpRadius): snap to the PANIC locomotor
//     set and move to the master; if critically far (> 3*MustCatchUpRadius) advance a crisis
//     counter, re-issue the move past bailTime/3, and kill self past bailTime.
//   - travelling together (I am moving): occasionally vary my own locomotor set from a
//     GameLogicRandomValue(0,10) draw (1->WANDER, 2->PANIC, 3->NORMAL) so the mob does not
//     move in lockstep; reset the crisis counter.
//   - master idle: stop.
//
// LANDED-SEAM MAPPING (task packet: consume only landed systems, add NO pathfinding dep).
// The GPL module drives movement through AIUpdate (getCurLocomotor / chooseLocomotorSet /
// aiMoveToPosition / isMoving / isIdle) - AIUpdate is float substrate and deliberately unfrozen,
// so a [SimState] module may not type it. The landed Fix64 movement seam is S2's
// SimLocomotorUpdate, the blessed owner of the movement frame until AIUpdate ports (LOCO-F1):
//   - "distance to master, FROM_CENTER_3D"  -> 3D distance between the two objects'
//     SimLocomotorUpdate.Physics.Position (FixVector3, F4-quantized, deterministic). The
//     ISimContext.Partition seam exposes QueryObjectsInRadius but not getDistanceSquared, so
//     the faithful center-to-center distance is computed from the two sim positions (D-MMS-1).
//   - chooseLocomotorSet(SET)              -> SimLocomotorUpdate.SetLocomotorSet(type)
//   - aiMoveToPosition(pos)                -> SimLocomotorUpdate.SetTargetPosition(pos, speed)
//   - masterAI->isMoving()                 -> masterLoco.Mode == MoveToPosition
//   - myAI->isMoving()                     -> myLoco.Mode == MoveToPosition
//   - masterAI->isIdle()                   -> masterLoco.Mode == Idle/Maintain -> myLoco.Stop()
// This is the exact S2 seam that the movement scenarios and the landed sibling systems drive;
// it introduces no S5 pathfinding dependency (pathfinding is landing in parallel this round).
//
// FINDINGS (behavior-fact gaps, filed not invented - see modules-r10/MobMemberSlavedUpdate.md):
//   F-MMS-PATHDIST: when the master is moving, GPL chooses WANDER (I am ahead) vs PANIC (I am
//     lagging) by comparing my locomotor-distance-to-goal against the master's, and only
//     re-issues the move when my goal is more than 5 pathfind cells from the master's goal.
//     Goal position / distance-to-goal are not on the landed movement seam, so we always take
//     the lagging case (PANIC + move), which is definitionally true here - we are already
//     beyond MustCatchUpRadius. The map-origin guard (goal length < 1) is subsumed.
//   F-MMS-SELFTASK: the idle branch's self-tasking (SpawnBehavior::maySpawnSelfTaskAI gated on
//     Squirrelliness, AIUpdate::getNextMoodTarget, aiAttackObject) and victim readback
//     (getCurrentVictim / m_primaryVictimID) ride the AI-mood/spawn seams, which have not
//     landed. Only the landed-reachable part is modeled: master-idle -> stop. Consequently
//     m_isSelfTasking and m_primaryVictimID never change and are omitted from the state
//     inventory; they return with the mood-AI seam (and with them Squirrelliness and the
//     NoNeedToCatchUpRadius wander in doCatchUpLogic, both parsed-but-unconsumed today).
//   F-MMS-COLOR: the ctor's three GameLogicRandomValueReal personal-color draws feed a
//     commented-out drawable tint only; they are client cosmetic with no sim consumer and are
//     deliberately not reproduced (our logic RNG is not the original's, so replaying the draws
//     buys no oracle alignment) - mirrors TransitionDamageFX F-TDF-2.
//   F-MMS-WEAPONSET: the "clear firing/reloading model-condition flags under
//     WEAPONSET_PLAYER_UPGRADE" block is client presentation animation cleanup, not sim state;
//     omitted (a [SimState] module does not touch the Drawable's condition flags for cosmetics).
//   F-MMS-MOBSTATE: GPL's m_mobState is written only by doCatchUpLogic(), which update() never
//     calls, so it is dead in the live path; it is omitted from the state inventory rather than
//     persisted as a permanently-NONE field.
//
// Every mutable sim field appears in Xfer exactly once (§3); tolerances are the field's
// conformance class at its declaration site (§4). Field order = declaration order = OUR
// choice (F9), never the original's.

using OpenSage.Data.Ini;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class MobMemberSlavedUpdate : UpdateModule
{
    /// <summary>GPL throttle divisor: the heavy catch-up body runs once every this many frames.</summary>
    private const int UpdateRate = 16;

    private readonly MobMemberSlavedUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>The mob leader (nexus) this member is enslaved to; Invalid = orphaned.</summary>
    private ObjectId _slaverId = ObjectId.Invalid;

    /// <summary>Counts up to <see cref="UpdateRate"/>; the ctor RNG stagger biases the phase.</summary>
    private int _framesToWait;

    /// <summary>Consecutive heavy ticks spent critically far from the master (the bail failsafe).</summary>
    private uint _catchUpCrisisTimer;

    public MobMemberSlavedUpdate(GameObject gameObject, ISimContext context, MobMemberSlavedUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // GPL ctor: stagger the first heavy tick by a logic-RNG draw in [0, 20] frames so a
        // whole mob does not run its catch-up scan on one frame (GameLogicRandomValue(0, 20)).
        // Next(lo, hi) is inclusive, matching GameLogicRandomValue. The three cosmetic
        // personal-color reals GPL also draws here are deliberately not reproduced (F-MMS-COLOR).
        _framesToWait = Context.GameLogicRandom.Next(0, 20);

        // GPL update() returns UPDATE_SLEEP_NONE and self-throttles with the counter.
        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>The leader we are enslaved to (GPL getSlaverID), for readers/tests.</summary>
    public ObjectId SlaverId => _slaverId;

    /// <summary>
    /// Enslave this member to a leader (GPL onEnslave -> startSlavedEffects: sets m_slaver).
    /// The spawner calls this at creation; without it, the first tick finds no master and the
    /// member self-destructs (GPL's orphaned-member behavior).
    /// </summary>
    public void SetSlaver(GameObject slaver)
    {
        _slaverId = slaver?.Id ?? ObjectId.Invalid;
    }

    public override UpdateSleepTime Update()
    {
        // Master lookup runs EVERY frame, before the throttle (GPL order): an orphaned member
        // does not linger for up to 16 frames. findObjectByID(INVALID) is null, so the invalid
        // and destroyed cases collapse to one.
        var master = Context.GameLogic.GetObjectById(_slaverId);
        if (master == null)
        {
            _slaverId = ObjectId.Invalid;
            GameObject.Kill();
            return UpdateSleepTime.None;
        }

        var myLoco = GameObject.FindBehavior<SimLocomotorUpdate>();
        var masterLoco = master.FindBehavior<SimLocomotorUpdate>();
        if (myLoco == null || masterLoco == null)
        {
            // GPL: no AIUpdate on me or master -> nothing to drive. (F-MMS-WEAPONSET: the
            // model-condition cleanup that GPL does before this point is client cosmetic.)
            return UpdateSleepTime.None;
        }

        // GPL "++m_framesToWait < 16": run the heavy body once per UpdateRate frames.
        _framesToWait++;
        if (_framesToWait < UpdateRate)
        {
            return UpdateSleepTime.None;
        }
        _framesToWait = 0;

        // Positions are ingested lazily on each locomotor's first Update (LOCO-F8); until then
        // Physics.Position is default and a distance test would be meaningless.
        if (!myLoco.TransformInitialized || !masterLoco.TransformInitialized)
        {
            return UpdateSleepTime.None;
        }

        var myPosition = myLoco.Physics.Position;
        var masterPosition = masterLoco.Physics.Position;

        var dx = masterPosition.X - myPosition.X;
        var dy = masterPosition.Y - myPosition.Y;
        var dz = masterPosition.Z - myPosition.Z;
        var distanceSquared = dx * dx + dy * dy + dz * dz;

        var mustCatchUp = _data.MustCatchUpRadius;
        var mustCatchUpSquared = mustCatchUp * mustCatchUp;

        if (distanceSquared > mustCatchUpSquared)
        {
            // Too far from the nexus - catch up now. GPL splits master-moving vs master-still and,
            // when moving, picks WANDER/PANIC from a path-distance-to-goal comparison and gates
            // the re-issue on a goal delta; neither is on the landed movement seam, so we take the
            // lagging (PANIC) case, which is definitionally true here (F-MMS-PATHDIST).
            myLoco.SetLocomotorSet(LocomotorSetType.Panic);
            IssueMoveTo(myLoco, masterPosition);

            var crisisRadius = mustCatchUp + mustCatchUp + mustCatchUp; // GPL mustCatchUpRadius * 3
            if (distanceSquared > crisisRadius * crisisRadius)
            {
                // Critically far this tick.
                _catchUpCrisisTimer++;

                if (_catchUpCrisisTimer > (uint)_data.CatchUpCrisisBailTime)
                {
                    // Isolated too long: self-destruct so the mob cannot become invincible.
                    GameObject.Kill();
                    return UpdateSleepTime.None;
                }

                if (_catchUpCrisisTimer > (uint)(_data.CatchUpCrisisBailTime / 3))
                {
                    IssueMoveTo(myLoco, masterPosition);
                }
            }
        }
        else if (myLoco.Mode == SimMoveMode.MoveToPosition)
        {
            // We are all on a trip together - not too far this tick.
            _catchUpCrisisTimer = 0;

            var seed = Context.GameLogicRandom.Next(0, 10);
            if (seed == 1)
            {
                myLoco.SetLocomotorSet(LocomotorSetType.Wander);
            }
            else if (seed == 2)
            {
                myLoco.SetLocomotorSet(LocomotorSetType.Panic);
            }
            else if (seed == 3)
            {
                myLoco.SetLocomotorSet(LocomotorSetType.Normal);
            }
        }
        else
        {
            // Standing near the nexus - not too far this tick.
            _catchUpCrisisTimer = 0;

            // GPL idle branch does spawner self-tasking + mood targeting here; those seams have
            // not landed (F-MMS-SELFTASK). The landed-reachable part: if the controlling player
            // has stopped the master (it is idle), stop too.
            if (masterLoco.Mode == SimMoveMode.Idle || masterLoco.Mode == SimMoveMode.Maintain)
            {
                myLoco.Stop();
            }
        }

        return UpdateSleepTime.None;
    }

    /// <summary>GPL aiMoveToPosition: order the member to the target at its current set's max speed
    /// (SetTargetPosition clamps the desired speed to the locomotor max each frame).</summary>
    private static void IssueMoveTo(SimLocomotorUpdate loco, in FixVector3 target)
    {
        var locomotor = loco.CurrentLocomotor;
        if (locomotor == null)
        {
            return;
        }
        loco.SetTargetPosition(target, locomotor.Template.SimMaxSpeed);
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferObjectId("Slaver", ref _slaverId);
        xfer.XferInt("FramesToWait", ref _framesToWait, Tolerance.Exact);          // counter: Exact
        xfer.XferUInt("CatchUpCrisisTimer", ref _catchUpCrisisTimer, Tolerance.Exact); // count: Exact
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================
[SimDataAudited]
public sealed class MobMemberSlavedUpdateModuleData : UpdateModuleData
{
    internal static MobMemberSlavedUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<MobMemberSlavedUpdateModuleData> FieldParseTable = new IniParseTable<MobMemberSlavedUpdateModuleData>
    {
        // Spatial radii -> Fix64 distances (S5 vocabulary): they are squared and compared
        // against a Fix64 center-to-center distance. GPL types them Int and promotes in sqr().
        { "MustCatchUpRadius", (parser, x) => x.MustCatchUpRadius = parser.ParseFix64() },
        { "NoNeedToCatchUpRadius", (parser, x) => x.NoNeedToCatchUpRadius = parser.ParseFix64() },
        // A 0..1 ratio -> Fix64.
        { "Squirrelliness", (parser, x) => x.Squirrelliness = parser.ParseFix64() },
        // A dimensionless count of consecutive out-of-range heavy ticks (GPL UnsignedInt),
        // NOT a millisecond duration - stays an integer (F3), not a ParseDurationLogicFrames.
        { "CatchUpCrisisBailTime", (parser, x) => x.CatchUpCrisisBailTime = parser.ParseInteger() },
    };

    /// <summary>Distance from the master I may reach before I must catch up. GPL default
    /// CATCH_UP_RADIUS_MAX = 50.</summary>
    public Fix64 MustCatchUpRadius { get; private set; } = Fix64.FromDecimalLiteral("50");

    /// <summary>Allowable wander distance from the master while guarding (GPL default
    /// CATCH_UP_RADIUS_MIN = 25). Consumed only by doCatchUpLogic's wander, which the live
    /// update path never reaches - parsed but unconsumed today (F-MMS-SELFTASK).</summary>
    public Fix64 NoNeedToCatchUpRadius { get; private set; } = Fix64.FromDecimalLiteral("25");

    /// <summary>Self-task eagerness ratio (clamped 0..1 by GPL onObjectCreated). Consumed only
    /// by the un-landed spawner self-task gate - parsed but unconsumed today (F-MMS-SELFTASK).</summary>
    public Fix64 Squirrelliness { get; private set; }

    /// <summary>Consecutive critically-far heavy ticks tolerated before self-destruct. GPL
    /// default is 999999 (effectively never) - a very large iteration count, not a duration.</summary>
    public int CatchUpCrisisBailTime { get; private set; } = 999999;

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new MobMemberSlavedUpdate(gameObject, gameEngine.SimContext, this);
    }
}
