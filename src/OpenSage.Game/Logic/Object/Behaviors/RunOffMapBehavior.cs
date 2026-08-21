// RunOffMapBehavior - R13 port, translated from generals-gpl GeneralsMD
// ChinookAIUpdate.cpp's ChinookHeadOffMapState (GPL semantics reference; api-freeze-v1 §6 /
// template v1.1), generalized for BFME2's data-authored fork per
// research/modules-r13/specs/RunOffMapBehaviorModuleData.md.
//
// Behavioral facts translated from the GPL source:
//   - ChinookHeadOffMapState::onEnter (ChinookAIUpdate.cpp:158-168): issues
//     ai->aiMoveToPosition(ai->getOriginalPosition(), CMD_FROM_AI) unconditionally on state
//     entry - the Chinook always self-transitions into this state, so there is no external
//     trigger concept in the shared mechanical shape. RequiresSpecificTrigger is the
//     BFME2-added fork layered on top (spec §2.2): 100% of shipped AotR usages
//     (harad/evilmen/evilbeasts/angmar mumakil-family units) set it to Yes, with the object's
//     own INI comment naming the trigger source - "Triggers when DetachableRiderUpdate says
//     so!" - and DetachableRiderUpdate.OnRiderDied is now the landed caller (R13, closing
//     F-ROM-5 from the caller side; DetachableRiderUpdateModuleData.md §2.4). What remains
//     open is the *detection* half - who tells DetachableRiderUpdate the rider died - filed
//     as F-DRU-1, plus the shipped Rohirrim data's own missing RunOffMapWaypointName
//     (F-DRU-5, lands in this module's own F-ROM-1 sleep-forever path below).
//   - ChinookHeadOffMapState::update (ChinookAIUpdate.cpp:170-183): polls every frame; once
//     outside the map extent, silently TheGameLogic->destroyObject(owner) - no Die dispatch.
//     This is the DieOnMap=false branch (spec §2.4): never shipped in AotR data, but a legal
//     field value with a defined fallback owed to it.
//   - DieOnMap=true (the only branch shipped AotR data exercises, spec §2.4): a
//     naming+data-pattern inference, not a literal GPL/INI-comment citation - every shipped
//     user of this module (mumakil.ini, haradoliphaunt.ini, siegemumak.ini,
//     mumakilmatriarch.ini, undeadmammoth.ini, greatbeast.ini, obsolete.ini) pairs
//     DieOnMap=Yes with DelayedDeathBody/BurningDeathBehavior/FireWeaponWhenDeadBehavior death
//     machinery that only fires from a real GameObject.Kill() -> OnDie dispatch, never from
//     the silent DestroyObject GPL uses for the Chinook - so DieOnMap=true means "die (real
//     death sequence) on arrival", not "the same silent vanish, gated by a flag".
//
// LANDED-SEAM MAPPING (§0 of the spec corrects the audit rationale that cited the legacy,
// pre-SimCore AIUpdate family as precedent - that citation is wrong; the real precedent is
// the already-landed SimLocomotorUpdate composition three R12 modules already use for this
// exact GPL call shape):
//   - ai->aiMoveToPosition(pos, CMD_FROM_AI) -> SimLocomotorUpdate.SetTargetPosition(pos,
//     locomotor max speed), the same idiom PilotFindVehicleUpdate.IssueMoveTo,
//     MobMemberSlavedUpdate, and SpectreGunshipUpdate already use for the identical GPL shape.
//   - The waypoint name -> position resolution has no landed seam (F-ROM primitives, spec
//     §2.3): grown onto ISimContext as IGameLogic.TryGetWaypointPosition, a minimal additive
//     member per api-freeze-v1 §3-S8's "grow one member at a time" pattern.
//   - Arrival predicate: locomotor.Mode collapsing to SimMoveMode.Maintain, the same landed
//     arrival-collapse SimLocomotorUpdate.Update()'s MoveToPosition case performs and
//     PilotFindVehicleUpdate.IsIdle already reads (restricted here to "was moving, now
//     Maintain" so it can't fire before the move is even issued).
//   - Off-map predicate (DieOnMap=false branch only): SimPathfindGrid.WorldToCell's overflow
//     report (spec §2.4, flagged F-ROM-2 - the pathfind grid's extent is the nearest landed
//     Fix64 analog to GPL's TheTerrainLogic->getExtentIncludingBorder, not proven identical;
//     unverifiable against shipped behavior since every shipped instance uses DieOnMap=Yes).
//
// Residual gaps (spec §5, parked not invented, port-review authority):
//   F-ROM-1: unknown waypoint name -> sleeps forever, no invented fallback destination.
//   F-ROM-2: map-extent primitive (PathfindGrid vs. getExtentIncludingBorder) not proven
//     identical; unverifiable today (no shipped DieOnMap=false instance).
//   F-ROM-3: GameObject.IsOffMap is not wired by this port (pre-existing, never-set flag;
//     exposing a setter is a GameObject.cs change out of scope, same posture as EmpUpdate's
//     F-EMP-6).
//   F-ROM-5 (spec §5.5, closed from the caller side in R13): DetachableRiderUpdate.OnRiderDied
//     now calls Trigger() via FindBehavior (DetachableRiderUpdateModuleData.md §2.4). What
//     F-ROM-5 named as "no caller yet" re-scopes to F-DRU-1 (detection: who calls
//     OnRiderDied, and when - still blocked on the deliberately unfrozen Contain rider-slot
//     surface) and F-DRU-5 (the shipped Rohirrim RunOffMapBehavior block authors no
//     RunOffMapWaypointName, so triggering it lands in this module's own F-ROM-1 path below -
//     correct observed behavior of the shipped data, no default waypoint invented).
//
// Every mutable sim field appears in Xfer exactly once (§3 of the spec); declaration order is
// Xfer order (F9, ours to choose). No position field of its own: the goal position lives in
// SimLocomotorUpdate's own Xfer once SetTargetPosition is called - this module does not
// duplicate another module's already-walked state.

using OpenSage.Data.Ini;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class RunOffMapBehavior : UpdateModule
{
    private readonly RunOffMapBehaviorModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>
    /// Whether the move is allowed to be issued: true immediately for
    /// !RequiresSpecificTrigger (the literal Chinook shape - GPL's onEnter has no external
    /// gate), or set by <see cref="Trigger"/> for RequiresSpecificTrigger objects (BFME2's
    /// fork, spec §2.2).
    /// </summary>
    private bool _triggered;

    /// <summary>Whether SetTargetPosition has already been issued (resolved and issued
    /// exactly once, spec §2.3 - GPL resolves the destination once on state entry, not every
    /// tick).</summary>
    private bool _moveIssued;

    /// <summary>Whether the terminal action (Kill or DestroyObject) has already fired -
    /// guards against repeats the same way EmpUpdate's _dieFrame = LogicFrame.MaxValue does
    /// (spec §2.4).</summary>
    private bool _terminated;

    public RunOffMapBehavior(GameObject gameObject, ISimContext context, RunOffMapBehaviorModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // GPL: the Chinook always self-enters HEAD_OFF_MAP unconditionally - no external
        // trigger concept exists in the shared mechanical shape. RequiresSpecificTrigger is
        // BFME2's own gate on top of it (spec §2.2): starts pre-triggered unless the data
        // asks for an external Trigger() call.
        _triggered = !data.RequiresSpecificTrigger;

        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>
    /// External trigger (GPL: fired by DetachableRiderUpdate, per this object's own INI
    /// comment). The landed caller is DetachableRiderUpdate.OnRiderDied (R13, both modules
    /// live on the same GameObject via FindBehavior, no order-pipeline hop needed) - see
    /// research/modules-r13/specs/DetachableRiderUpdateModuleData.md §2.4. No-op if this
    /// module does not require a trigger, or has already been triggered.
    /// </summary>
    public void Trigger()
    {
        if (_triggered)
        {
            return;
        }

        _triggered = true;
        SetWakeFrame(UpdateSleepTime.None);
    }

    public override UpdateSleepTime Update()
    {
        if (_terminated)
        {
            return UpdateSleepTime.Forever;
        }

        if (!_triggered)
        {
            return UpdateSleepTime.Forever;
        }

        var locomotor = GameObject.FindBehavior<SimLocomotorUpdate>();

        if (!_moveIssued)
        {
            if (!Context.GameLogic.TryGetWaypointPosition(_data.RunOffMapWaypointName, out var target))
            {
                // F-ROM-1 (spec §5): no waypoint of this name on the map. GPL has no defined
                // fallback (Chinook's own call can't fail - getOriginalPosition always
                // resolves). Sleep forever rather than move nowhere; filed, not invented.
                return UpdateSleepTime.Forever;
            }

            if (locomotor?.CurrentLocomotor == null)
            {
                // No movement seam to drive (PilotFindVehicleUpdate's identical guard,
                // PilotFindVehicleUpdate.cs:111-115).
                return UpdateSleepTime.Forever;
            }

            locomotor.SetTargetPosition(target, locomotor.CurrentLocomotor.Template.SimMaxSpeed);
            _moveIssued = true;
            return UpdateSleepTime.None;
        }

        if (locomotor == null)
        {
            return UpdateSleepTime.Forever;
        }

        if (_data.DieOnMap)
        {
            // Terminal condition: arrival at the waypoint (spec §2.4). The landed
            // arrival-collapse SimLocomotorUpdate.Update()'s MoveToPosition case already
            // performs; restricted to "was moving, now Maintain" so it can't fire before the
            // move is even issued (guaranteed here since we only reach this branch after
            // _moveIssued is true).
            if (locomotor.Mode == SimMoveMode.Maintain)
            {
                _terminated = true;
                GameObject.Kill();
                return UpdateSleepTime.Forever;
            }
        }
        else
        {
            // Terminal condition: leaving the map (the literal Chinook mechanism, spec §2.4).
            // F-ROM-2: SimPathfindGrid's cell extent is the nearest landed Fix64 analog to
            // GPL's TheTerrainLogic->getExtentIncludingBorder, not proven identical.
            var outside = Context.GameLogic.PathfindGrid.WorldToCell(locomotor.Physics.Position, out _, out _);
            if (outside)
            {
                _terminated = true;
                Context.GameLogic.DestroyObject(GameObject);
                return UpdateSleepTime.Forever;
            }
        }

        return UpdateSleepTime.None;
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Declaration order is Xfer order (F9). No position field of its own: the goal position
    // lives in SimLocomotorUpdate's own Xfer (GoalPosition) once SetTargetPosition is called.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferBool("Triggered", ref _triggered);
        xfer.XferBool("MoveIssued", ref _moveIssued);
        xfer.XferBool("Terminated", ref _terminated);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

/// <summary>
/// Drives the object off the map (or to its death on the map) along a named waypoint, the
/// BFME2 generalization of ChinookAIUpdate's HEAD_OFF_MAP self-exit state. Update-category
/// because the module needs a per-frame tick to drive movement and poll for
/// arrival/off-map (spec §2.1), the same reason every ported AI-adjacent module in this
/// family (AutoHealBehavior, PilotFindVehicleUpdate, AutoAbilityBehavior) is UpdateModule,
/// never a bare BehaviorModule.
/// </summary>
[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class RunOffMapBehaviorModuleData : UpdateModuleData
{
    internal static RunOffMapBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<RunOffMapBehaviorModuleData> FieldParseTable = new IniParseTable<RunOffMapBehaviorModuleData>
    {
        { "RequiresSpecificTrigger", (parser, x) => x.RequiresSpecificTrigger = parser.ParseBoolean() },
        { "RunOffMapWaypointName", (parser, x) => x.RunOffMapWaypointName = parser.ParseIdentifier() },
        { "DieOnMap", (parser, x) => x.DieOnMap = parser.ParseBoolean() }
    };

    public bool RequiresSpecificTrigger { get; private set; }

    public string RunOffMapWaypointName { get; private set; }
    public bool DieOnMap { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new RunOffMapBehavior(gameObject, gameEngine.SimContext, this);
    }
}
