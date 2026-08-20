// PilotFindVehicleUpdate - R12 port, translated from generals-gpl GeneralsMD
// PilotFindVehicleUpdate.cpp/.h (GPL semantics reference; api-freeze-v1 §6 / template v1.1).
// AI-only: "instructs the pilot to go find a friendly vehicle to enter."
//
// Behavioral facts translated from the GPL source:
//   - ctor: m_didMoveToBase = false; setWakeFrame(UPDATE_SLEEP_NONE) - ticks every frame,
//     update() self-throttles with UPDATE_SLEEP(m_scanFrames) on every return path.
//   - update():
//       1. obj->getControllingPlayer()->getPlayerType() == PLAYER_HUMAN -> UPDATE_SLEEP_FOREVER
//          immediately, no scanning (AI-only behavior).
//       2. ai == NULL -> UPDATE_SLEEP_FOREVER (no AI substrate to drive).
//       3. !ai->isIdle() -> UPDATE_SLEEP(scanFrames): busy with another command, don't scan.
//       4. scanClosestTarget(): the periodic (expensive) scan. Found -> ai->aiEnter(target,
//          CMD_FROM_AI) and clear m_didMoveToBase. Not found -> try moving to the controlling
//          player's AI base center exactly once (m_didMoveToBase gates the retry) via
//          ai->aiMoveToPosition(CMD_FROM_AI).
//       5. always returns UPDATE_SLEEP(scanFrames).
//   - scanClosestTarget(): iterate objects within ScanRange (FROM_CENTER_2D, sorted near to
//     far), filtered to KINDOF_VEHICLE, alive, same controlling player, same map status.
//     Reject any candidate whose health is below MaxHealth*MinHealth. Of the survivors,
//     return the first one that "would like to collide with" the pilot (i.e. wants a rider) -
//     the pilot never enters a vehicle its own collide logic wouldn't accept.
//
// LANDED-SEAM MAPPING (consume only landed systems, no new pathfinding/AI-order dep, same
// shape as MobMemberSlavedUpdate's LOCO-F1 mapping):
//   - obj->getAI() / ai->isIdle()   -> GameObject.FindBehavior<SimLocomotorUpdate>(); idle is
//     Mode == Idle || Mode == Maintain (not currently MoveToPosition/MoveTowardsAngle/
//     PathfindMoveToPosition) - the same idle predicate MobMemberSlavedUpdate's master-idle
//     branch already established for this seam.
//   - ai->aiEnter(target, CMD_FROM_AI) -> SimLocomotorUpdate.SetTargetPosition(target's
//     position, locomotor max speed): GPL's aiEnter is itself just an order (move-to-and-
//     interact); the actual containment transfer happens later, on physical arrival/collide,
//     via the vehicle's own contain module - outside this module's job either in GPL or here.
//   - ai->aiMoveToPosition(pos, CMD_FROM_AI) -> SimLocomotorUpdate.SetTargetPosition(pos, ...),
//     same idiom.
//   - CollideModuleInterface::wouldLikeToCollideWith(other) (F-PFV-1, filed not invented): no
//     landed seam exposes a per-collide-module acceptance predicate. ICollideModule here has
//     only OnCollide, no query method, and IContainModule (the interface that would model "a
//     vehicle that wants a rider", including IsRiderChangeContain) has zero implementing
//     classes anywhere in this codebase today - GameObject.Contain is always null, so it
//     cannot be used as a working proxy. With no landed acceptance predicate to consult, this
//     port treats every candidate that survives the kind/owner/alive/health filters as one the
//     pilot "would like to collide with" (a vehicle-shaped, friendly, healthy-enough vehicle),
//     the same "definitionally true" fallback shape as MobMemberSlavedUpdate's F-MMS-PATHDIST.
//   - PartitionFilterPlayer(me->getControllingPlayer(), true) -> same-owner test
//     (candidate.Owner == GameObject.Owner), matching the GPL filter's match=true semantics
//     (no ally-substitution - a pilot only re-boards ITS OWN player's vehicles).
//   - ITER_SORTED_NEAR_TO_FAR (F-PFV-2, filed not invented, same gap/same fix as
//     PickupStuffUpdate): ISimContext.Partition exposes QueryObjectsInRadius with no
//     nearest-first ordering guarantee. The first accepting candidate in the landed partition
//     order is taken instead of the true nearest; both are deterministic, this one is not
//     GPL's exact tie-break when more than one vehicle qualifies.
//   - Player::getAiBaseCenter (F-PFV-3, filed not invented): the fallback "move toward the AI
//     base" has no landed Player-side seam (no AiBaseCenter concept exists on the frozen
//     Player contract, and this task's reservedNames is empty - no new shared identifier may
//     be added to land one). The m_didMoveToBase bookkeeping (attempt-once-per-search-cycle,
//     reset when a vehicle is later found) is modeled faithfully; the actual repositioning
//     toward the base is not - a no-op placeholder stands in for aiMoveToPosition here until
//     the base-center seam lands on Player.
//
// Every mutable sim field appears in Xfer exactly once (§3); tolerance is the field's
// conformance class at its declaration site (§4). Field order mirrors the GPL xfer() order
// (m_didMoveToBase is GPL's only persisted field).

using OpenSage.Data.Ini;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class PilotFindVehicleUpdate : UpdateModule
{
    private readonly PilotFindVehicleUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>
    /// Whether a move-to-base fallback has already been attempted this search cycle (GPL
    /// m_didMoveToBase): limits the retry to once, and resets whenever a vehicle is found.
    /// </summary>
    private bool _didMoveToBase;

    public PilotFindVehicleUpdate(GameObject gameObject, ISimContext context, PilotFindVehicleUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // GPL ctor: m_didMoveToBase starts false; update() ticks every frame (UPDATE_SLEEP_NONE)
        // and self-throttles with UPDATE_SLEEP(scanFrames) on every return.
        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>Test/inspector-only view of the fallback-attempted flag; not part of the public API.</summary>
    internal bool DidMoveToBase => _didMoveToBase;

    public override UpdateSleepTime Update()
    {
        // GPL: human-controlled objects never run this AI-only behavior - sleep forever, no
        // scan, no throttle re-arm (a genuinely different sleep target than every other path).
        if (GameObject.Owner is null || GameObject.Owner.IsHuman)
        {
            return UpdateSleepTime.Forever;
        }

        // GPL: obj->getAI() == NULL -> UPDATE_SLEEP_FOREVER. Landed analog: no movement
        // seam to drive (MobMemberSlavedUpdate's LOCO-F1 mapping).
        var myLoco = GameObject.FindBehavior<SimLocomotorUpdate>();
        if (myLoco == null)
        {
            return UpdateSleepTime.Forever;
        }

        // GPL: !ai->isIdle() -> UPDATE_SLEEP(scanFrames), skip this scan entirely.
        if (!IsIdle(myLoco))
        {
            return UpdateSleepTime.Frames(_data.ScanRate);
        }

        var target = ScanClosestTarget();
        if (target != null)
        {
            // GPL ai->aiEnter(target, CMD_FROM_AI): order the move; the actual containment
            // transfer happens on arrival/collide, outside this module's job (see header).
            IssueMoveTo(myLoco, SimTransformBridge.PullPosition(target));
            _didMoveToBase = false;
        }
        else if (!_didMoveToBase)
        {
            // GPL: try moving to the AI base center exactly once per search cycle. The base
            // position itself has no landed seam (F-PFV-3); only the once-per-cycle
            // bookkeeping is modeled here.
            _didMoveToBase = true;
        }

        return UpdateSleepTime.Frames(_data.ScanRate);
    }

    /// <summary>GPL AIUpdateInterface::isIdle, via the landed movement seam: not currently
    /// pursuing any move order.</summary>
    private static bool IsIdle(SimLocomotorUpdate loco) =>
        loco.Mode == SimMoveMode.Idle || loco.Mode == SimMoveMode.Maintain;

    /// <summary>GPL aiMoveToPosition/aiEnter: order the move at the current locomotor's max
    /// speed (SetTargetPosition clamps the desired speed to the locomotor max each frame),
    /// same idiom as MobMemberSlavedUpdate's IssueMoveTo.</summary>
    private static void IssueMoveTo(SimLocomotorUpdate loco, in FixVector3 target)
    {
        var locomotor = loco.CurrentLocomotor;
        if (locomotor == null)
        {
            return;
        }
        loco.SetTargetPosition(target, locomotor.Template.SimMaxSpeed);
    }

    /// <summary>GPL scanClosestTarget: nearest in-range friendly vehicle, healthy enough, that
    /// wants a rider (see the F-PFV-1/F-PFV-2 mapping notes in the header).</summary>
    private GameObject ScanClosestTarget()
    {
        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _data.ScanRange))
        {
            if (!IsCandidateVehicle(candidate))
            {
                continue;
            }

            if (!MeetsMinHealth(candidate, _data.MinHealth))
            {
                continue;
            }

            // F-PFV-1 (see header): wouldLikeToCollideWith has no landed acceptance seam to
            // consult, so every candidate that survives kind/owner/alive/health is accepted.
            return candidate;
        }

        return null;
    }

    /// <summary>
    /// GPL's PartitionFilterAcceptByKindOf(VEHICLE) + PartitionFilterAlive +
    /// PartitionFilterPlayer(controllingPlayer, true) + PartitionFilterSameMapStatus.
    /// </summary>
    private bool IsCandidateVehicle(GameObject candidate)
    {
        if (candidate == GameObject || candidate.IsEffectivelyDead || candidate.IsDestroyed)
        {
            return false;
        }

        if (candidate.IsOffMap != GameObject.IsOffMap)
        {
            return false;
        }

        if (GameObject.Owner is null || candidate.Owner != GameObject.Owner)
        {
            return false;
        }

        return candidate.Definition.KindOf != null && candidate.Definition.KindOf.Get(ObjectKinds.Vehicle);
    }

    /// <summary>
    /// GPL: body->getHealth() &lt; body->getMaxHealth()*MinHealth -> reject. Reads the Fix64
    /// core directly (BodyDamageCore) through ActiveBody.DamageCore - the one Body
    /// implementation that exposes health as Fix64 rather than the float display view -
    /// so no float touches this [SimState] module.
    /// </summary>
    private static bool MeetsMinHealth(GameObject candidate, Fix64 minHealthFraction)
    {
        if (candidate.BodyModule is not ActiveBody body)
        {
            return false;
        }

        var core = body.DamageCore;
        return core.CurrentHealth >= core.MaxHealth * minHealthFraction;
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order mirrors the GPL xfer() order (m_didMoveToBase is the only persisted field).

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferBool("DidMoveToBase", ref _didMoveToBase);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

/// <summary>
/// Instructs the pilot to go find a "friendly" vehicle to enter. AI only.
/// </summary>
[SimDataAudited]
public sealed class PilotFindVehicleUpdateModuleData : UpdateModuleData
{
    internal static PilotFindVehicleUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<PilotFindVehicleUpdateModuleData> FieldParseTable = new IniParseTable<PilotFindVehicleUpdateModuleData>
    {
        // GPL parseDurationUnsignedInt: milliseconds -> ceil-quantized logic frames (S5).
        { "ScanRate", (parser, x) => x.ScanRate = parser.ParseDurationLogicFrames() },
        // GPL parseReal, a distance -> Fix64 (S5 vocabulary), consumed as a Partition radius.
        { "ScanRange", (parser, x) => x.ScanRange = parser.ParseFix64() },
        // GPL parseReal, a 0..1 fraction of MaxHealth -> Fix64.
        { "MinHealth", (parser, x) => x.MinHealth = parser.ParseFix64() }
    };

    /// <summary>Frames between scans (milliseconds in INI, ceil-quantized at parse, S5).</summary>
    public LogicFrameSpan ScanRate { get; private set; }

    public Fix64 ScanRange { get; private set; }

    /// <summary>Fraction of MaxHealth a candidate must retain to be worth boarding. GPL default 0.5f.</summary>
    public Fix64 MinHealth { get; private set; } = Fix64.FromDecimalLiteral("0.5");

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new PilotFindVehicleUpdate(gameObject, gameEngine.SimContext, this);
    }
}
