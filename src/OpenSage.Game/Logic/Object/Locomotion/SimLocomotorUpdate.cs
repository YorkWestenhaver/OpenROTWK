// SimLocomotorUpdate - the S2 system's driver UpdateModule: owns the object's
// SimLocomotorSet, its current SimLocomotor, its SimPhysics integrator state, and the
// current movement goal, and runs the frozen per-frame order
//     locomotor pass  ->  physics integration  ->  display push
// inside one module Update() (GPL splits this across AIUpdate -> Locomotor and
// PhysicsBehavior, two modules whose module order guarantees the same sequence; AIUpdate
// is deliberately unfrozen, so until it ports THIS module is the blessed owner of the
// movement frame - a recorded deviation, not an invention: every formula inside the
// passes is the GPL locomotor/physics math).
//
// Goal state is driven by public setters (SetTargetPosition / SetTargetAngle / Stop),
// which the test scenarios and, later, the order pipe / AIUpdate call. Arrival handling
// (GPL AIUpdate's job): within CloseEnoughDist of the goal (3D when the locomotor says
// so) the goal collapses to Maintain.
//
// The module registers under INI name "SimLocomotorUpdate" (interim vocabulary: BFME2
// data drives locomotors through AIUpdate variants; when AIUpdate ports it absorbs this
// driver and the name retires - design-note finding LOCO-F1).

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object.Locomotion;

public enum SimMoveMode
{
    Idle = 0,
    MoveToPosition = 1,
    MoveTowardsAngle = 2,
    Maintain = 3,

    // S5 pathfinding (additive): move along a pathfinder-produced path (GPL
    // POSITION_ON_PATH). While the path request is queued the unit does not move
    // (GPL "can't move till we get our path").
    PathfindMoveToPosition = 4,
}

[SimState]
public sealed class SimLocomotorUpdate : UpdateModule, OpenSage.Logic.Object.Pathfind.ISimPathfindClient
{
    private readonly SimLocomotorUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----
    private readonly SimPhysics _physics;
    private readonly SimLocomotorSet _locomotorSet = new();
    private LocomotorSetType _currentSetType = LocomotorSetType.Invalid;
    private int _currentLocomotorIndex = -1;
    private SimMoveMode _mode = SimMoveMode.Idle;
    private FixVector3 _goalPosition;
    private Fix64 _goalAngle;
    private Fix64 _desiredSpeed;
    private bool _blocked;
    private bool _transformInitialized;

    // ---- S5 pathfinding state (additive region, Pathfind* name-reserved) ----
    private OpenSage.Logic.Object.Pathfind.SimPath _pathfindPath;
    private FixVector3 _pathfindDestination;
    private bool _pathfindWaitingForPath;
    private LogicFrame _pathfindPathTimestamp;
    private bool _pathfindHasPathTimestamp;
    private LogicFrame _pathfindRequeueFrame;
    private bool _pathfindHasRequeueFrame;

    public SimLocomotorUpdate(GameObject gameObject, ISimContext context, SimLocomotorUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _physics = new SimPhysics(data);

        // The spawn transform/geometry are not final at module-ctor time (the engine
        // places the object AFTER modules construct), so the one-time float-substrate
        // ingestion (F4 wire boundary; SimTransformBridge) happens lazily at the first
        // Update via EnsureTransformInitialized.
        SetLocomotorSet(LocomotorSetType.Normal);

        SetWakeFrame(UpdateSleepTime.None);
    }

    public SimPhysics Physics => _physics;

    /// <summary>
    /// Whether the one-time transform ingestion (LOCO-F8: lazy at the first Update) has
    /// happened - before that, <see cref="Physics"/> position/yaw are default. Added by the
    /// S6 horde system (additive): cross-object readers (flank test, slot anchor) must not
    /// consume an uninitialized mirror.
    /// </summary>
    public bool TransformInitialized => _transformInitialized;

    public SimLocomotorSet LocomotorSet => _locomotorSet;
    public SimMoveMode Mode => _mode;
    public LocomotorSetType CurrentSetType => _currentSetType;

    public SimLocomotor CurrentLocomotor =>
        _currentLocomotorIndex >= 0 && _currentLocomotorIndex < _locomotorSet.Locomotors.Count
            ? _locomotorSet.Locomotors[_currentLocomotorIndex]
            : null;

    /// <summary>
    /// Rebuilds the locomotor set from the object definition for a set type (GPL
    /// AIUpdate chooseLocomotorSet + LocomotorSet::addLocomotor). Falls back to
    /// SET_NORMAL when the definition has no entry for the requested type. Live path -
    /// each added locomotor draws its 3-draw RNG stagger from the context stream.
    /// </summary>
    public bool SetLocomotorSet(LocomotorSetType type)
    {
        var definitionSets = GameObject.Definition.LocomotorSets;
        if (!definitionSets.TryGetValue(type, out var setTemplate))
        {
            if (type == LocomotorSetType.Normal || !definitionSets.TryGetValue(LocomotorSetType.Normal, out setTemplate))
            {
                return false;
            }
            type = LocomotorSetType.Normal;
        }

        _locomotorSet.Clear();
        foreach (var reference in setTemplate.Locomotors)
        {
            var template = reference.Value;
            if (template == null)
            {
                continue;
            }
            _locomotorSet.AddLocomotor(template, Context.GameLogicRandom, Context.CurrentFrame);
        }
        _currentSetType = type;
        ChooseLocomotor(Surfaces.Ground | Surfaces.Water | Surfaces.Cliff | Surfaces.Air | Surfaces.Rubble);
        return true;
    }

    /// <summary>GPL LocomotorSet::findLocomotor - first declared match on the surface mask.</summary>
    public void ChooseLocomotor(Surfaces surfaces)
    {
        var locomotor = _locomotorSet.FindLocomotor(surfaces);
        _currentLocomotorIndex = locomotor != null ? _locomotorSet.IndexOf(locomotor) : -1;
        if (locomotor != null && _transformInitialized)
        {
            var (bounding, major) = SimTransformBridge.PullGeometry(GameObject);
            locomotor.SetGeometry(bounding, major);
            locomotor.SetPhysicsOptions(_physics);
        }
    }

    private void EnsureTransformInitialized()
    {
        if (_transformInitialized)
        {
            return;
        }
        _transformInitialized = true;
        _physics.Position = SimTransformBridge.PullPosition(GameObject);
        _physics.Yaw = SimTransformBridge.PullYaw(GameObject);
        var locomotor = CurrentLocomotor;
        if (locomotor != null)
        {
            var (bounding, major) = SimTransformBridge.PullGeometry(GameObject);
            locomotor.SetGeometry(bounding, major);
            locomotor.SetPhysicsOptions(_physics);
        }
    }

    /// <summary>Orders the object to move; desiredSpeed clamps to the locomotor max each frame.</summary>
    public void SetTargetPosition(in FixVector3 position, Fix64 desiredSpeed)
    {
        _mode = SimMoveMode.MoveToPosition;
        _goalPosition = position;
        _desiredSpeed = desiredSpeed;
        _blocked = false;
        CurrentLocomotor?.StartMove(Context.CurrentFrame);
        SetWakeFrame(UpdateSleepTime.None);
    }

    public void SetTargetAngle(Fix64 angle)
    {
        _mode = SimMoveMode.MoveTowardsAngle;
        _goalAngle = angle;
        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>Stop and hold the current position (GPL maintain-position shape).</summary>
    public void Stop()
    {
        _mode = SimMoveMode.Maintain;
        SetWakeFrame(UpdateSleepTime.None);
    }

    // ==================================================================
    // S5 pathfinding (additive region, Pathfind* name-reserved): the GPL
    // requestPath -> queue -> doPathfind -> POSITION_ON_PATH follow seam,
    // living here until AIUpdate ports (PATH-F6, same shape as LOCO-F1).
    // ==================================================================

    public OpenSage.Logic.Object.Pathfind.SimPath PathfindPath => _pathfindPath;
    public bool PathfindWaitingForPath => _pathfindWaitingForPath;

    /// <summary>
    /// GPL AIUpdate::requestPath: store the destination, mark waiting, and either queue
    /// now or - when the last path was computed within the last 3 frames (the repath
    /// spin guard) - defer the queueing by 1 second (5 frames at the frozen 5 Hz).
    /// </summary>
    public void SetPathfindTargetPosition(in FixVector3 destination, Fix64 desiredSpeed)
    {
        _mode = SimMoveMode.PathfindMoveToPosition;
        _pathfindDestination = destination;
        _desiredSpeed = desiredSpeed;
        _blocked = false;
        _pathfindWaitingForPath = true;

        var now = Context.CurrentFrame;
        if (_pathfindHasPathTimestamp &&
            _pathfindPathTimestamp + new LogicFrameSpan(3) > now)
        {
            // Requesting a path very quickly - wait a second (GPL requestPath guard).
            _pathfindRequeueFrame = now + new LogicFrameSpan(5);
            _pathfindHasRequeueFrame = true;
        }
        else
        {
            Context.GameLogic.PathfindQueueForPath(GameObject.Id);
        }

        CurrentLocomotor?.StartMove(now);
        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>
    /// GPL AIUpdate::doPathfind - called by the pathfinder DURING queue processing (the
    /// only site allowed to run a find). Computes the path from the current sim position
    /// to the requested destination and stamps the path timestamp.
    /// </summary>
    public void DoPathfind(OpenSage.Logic.Object.Pathfind.SimPathfinder pathfinder)
    {
        if (!_pathfindWaitingForPath)
        {
            return;
        }
        _pathfindWaitingForPath = false;
        EnsureTransformInitialized();

        PathfindGetRadiusAndCenter(out var radius, out var centerInCell);
        _pathfindPath = pathfinder.FindPath(
            _locomotorSet.ValidSurfaces,
            _physics.Position,
            _pathfindDestination,
            radius,
            centerInCell,
            ignoreObstacleId: 0);
        _pathfindPathTimestamp = Context.CurrentFrame;
        _pathfindHasPathTimestamp = true;
    }

    /// <summary>
    /// GPL Pathfinder::getRadiusAndCenter: pathfind footprint radius from the bounding
    /// circle - diameters in (10,20) snap to 20; iRadius = floor(diam/10 + 0.3); zero
    /// bumps to 1; odd radii center in the cell; halve; clamp to 2 (clamped is centered).
    /// </summary>
    private void PathfindGetRadiusAndCenter(out int radius, out bool centerInCell)
    {
        var (bounding, _) = SimTransformBridge.PullGeometry(GameObject);
        var diameter = bounding + bounding;
        var cell = Fix64.FromDecimalLiteral("10");
        var twoCells = Fix64.FromDecimalLiteral("20");
        if (diameter > cell && diameter < twoCells)
        {
            diameter = twoCells;
        }
        var scaled = diameter / cell + Fix64.FromDecimalLiteral("0.3");
        var iRadius = (int)(Fix64.Floor(scaled).RawValue >> 32);
        centerInCell = false;
        if (iRadius == 0)
        {
            iRadius++;
        }
        if ((iRadius & 1) != 0)
        {
            centerInCell = true;
        }
        iRadius /= 2;
        if (iRadius > 2)
        {
            iRadius = 2;
            centerInCell = true;
        }
        radius = iRadius;
    }

    /// <summary>
    /// The POSITION_ON_PATH frame body (GPL doLocomotor): waiting means standing still;
    /// otherwise project onto the path (computePointOnPath) and feed the locomotor the
    /// goal point + remaining on-path distance. Returns false when there is nothing to
    /// do this frame (waiting / no path).
    /// </summary>
    private bool PathfindFollowPath(LogicFrame now, Fix64 surfaceZ, BodyDamageType condition)
    {
        if (_pathfindHasRequeueFrame && now >= _pathfindRequeueFrame)
        {
            _pathfindHasRequeueFrame = false;
            Context.GameLogic.PathfindQueueForPath(GameObject.Id);
        }
        if (_pathfindWaitingForPath || _pathfindPath == null || _pathfindPath.Count == 0)
        {
            return false; // GPL: can't move till we get our path
        }

        // Arrival (GPL AIUpdate close-enough vs the path end / requested destination).
        var locomotor = CurrentLocomotor;
        var goal = _pathfindPath.LastPosition;
        var dx = goal.X - _physics.Position.X;
        var dy = goal.Y - _physics.Position.Y;
        var closeEnough = locomotor.CloseEnoughDist;
        if (dx * dx + dy * dy <= closeEnough * closeEnough)
        {
            _pathfindPath = null;
            _mode = SimMoveMode.Maintain;
            return false;
        }

        _pathfindPath.ComputePointOnPath(
            Context.GameLogic.PathfindGrid, _locomotorSet.ValidSurfaces, 0, _physics.Position,
            out var goalPos, out var onPathDist);

        var blocked = _blocked;
        locomotor.MoveTowardsPosition(
            _physics, condition, goalPos, onPathDist, _desiredSpeed,
            ref blocked, now, surfaceZ);
        _blocked = blocked;
        return true;
    }


    private BodyDamageType DamageCondition => GameObject.BodyModule.DamageState;

    public override UpdateSleepTime Update()
    {
        var locomotor = CurrentLocomotor;
        if (locomotor == null)
        {
            return UpdateSleepTime.Forever;
        }

        EnsureTransformInitialized();

        var now = Context.CurrentFrame;
        var surfaceZ = Context.Terrain.GetGroundHeight(_physics.Position);
        var condition = DamageCondition;

        // GPL locomotorWorksWhenDead gate (AIUpdate skips dead objects' locomotion).
        if (GameObject.IsEffectivelyDead && !locomotor.Template.LocomotorWorksWhenDead)
        {
            _mode = SimMoveMode.Idle;
        }

        var requiresConstantCalling = true;
        switch (_mode)
        {
            case SimMoveMode.MoveToPosition:
                {
                    // Arrival (GPL AIUpdate close-enough): collapse to Maintain.
                    var dx = _goalPosition.X - _physics.Position.X;
                    var dy = _goalPosition.Y - _physics.Position.Y;
                    var dz = _goalPosition.Z - _physics.Position.Z;
                    var distSq = locomotor.IsCloseEnoughDist3D
                        ? dx * dx + dy * dy + dz * dz
                        : dx * dx + dy * dy;
                    var closeEnough = locomotor.CloseEnoughDist;
                    if (distSq <= closeEnough * closeEnough)
                    {
                        _mode = SimMoveMode.Maintain;
                        goto case SimMoveMode.Maintain;
                    }

                    // Straight-line path: onPathDistToGoal = 2D distance (the pathfinder that
                    // would supply a path distance is S5).
                    var onPathDist = Fix64.Sqrt(dx * dx + dy * dy);
                    var blocked = _blocked;
                    locomotor.MoveTowardsPosition(
                        _physics, condition, _goalPosition, onPathDist, _desiredSpeed,
                        ref blocked, now, surfaceZ);
                    _blocked = blocked;
                    break;
                }

            case SimMoveMode.MoveTowardsAngle:
                locomotor.MoveTowardsAngle(_physics, condition, _goalAngle, now, surfaceZ);
                break;

            // S5 pathfinding (additive): the POSITION_ON_PATH body. Falls through to
            // Maintain when PathfindFollowPath collapses the mode on arrival.
            case SimMoveMode.PathfindMoveToPosition:
                {
                    if (!PathfindFollowPath(now, surfaceZ, condition) &&
                        _mode == SimMoveMode.Maintain)
                    {
                        goto case SimMoveMode.Maintain;
                    }
                    break;
                }

            case SimMoveMode.Maintain:
                requiresConstantCalling =
                    locomotor.MaintainCurrentPosition(_physics, condition, now, surfaceZ);
                break;

            case SimMoveMode.Idle:
            default:
                requiresConstantCalling = false;
                break;
        }

        // Physics integration (the SimPhysics 8-step order), then the display mirror.
        var airborne = _physics.Position.Z > surfaceZ;
        _physics.Integrate(now, surfaceZ, airborne);
        SimTransformBridge.Push(GameObject, _physics.Position, _physics.Yaw);

        if (!requiresConstantCalling &&
            (_mode == SimMoveMode.Idle || _mode == SimMoveMode.Maintain) &&
            _physics.IsAtRest(now))
        {
            return UpdateSleepTime.Forever;
        }
        return UpdateSleepTime.None;
    }

    // ---- the single walk (F8 Objects channel; field order = declaration order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _physics.Xfer(xfer);

        // Membership is a function of (definition, set type): xfer the type, rebuild on
        // load WITHOUT rng draws, then walk per-locomotor state (SimLocomotorSet header).
        var setType = _currentSetType;
        xfer.XferEnum("CurrentSetType", ref setType);
        if (xfer.Mode == XferMode.Load && setType != _currentSetType)
        {
            RebuildSetForLoad(setType);
        }
        _locomotorSet.Xfer(xfer);

        xfer.XferInt("CurrentLocomotorIndex", ref _currentLocomotorIndex);
        xfer.XferEnum("Mode", ref _mode);
        xfer.XferFixVector3("GoalPosition", ref _goalPosition, Tolerance.Band);
        xfer.XferFix64("GoalAngle", ref _goalAngle, Tolerance.Band);
        xfer.XferFix64("DesiredSpeed", ref _desiredSpeed);
        xfer.XferBool("Blocked", ref _blocked);
        xfer.XferBool("TransformInitialized", ref _transformInitialized);

        // ---- S5 pathfinding state (additive tail) ----
        xfer.XferFixVector3("PathfindDestination", ref _pathfindDestination, Tolerance.Band);
        xfer.XferBool("PathfindWaitingForPath", ref _pathfindWaitingForPath);
        xfer.XferFrame("PathfindPathTimestamp", ref _pathfindPathTimestamp);
        xfer.XferBool("PathfindHasPathTimestamp", ref _pathfindHasPathTimestamp);
        xfer.XferFrame("PathfindRequeueFrame", ref _pathfindRequeueFrame);
        xfer.XferBool("PathfindHasRequeueFrame", ref _pathfindHasRequeueFrame);
        var hasPath = _pathfindPath != null;
        xfer.XferBool("PathfindHasPath", ref hasPath);
        if (hasPath)
        {
            _pathfindPath ??= new OpenSage.Logic.Object.Pathfind.SimPath();
            _pathfindPath.Xfer(xfer);
        }
        else if (xfer.Mode == XferMode.Load)
        {
            _pathfindPath = null;
        }
    }

    private void RebuildSetForLoad(LocomotorSetType type)
    {
        _locomotorSet.Clear();
        if (GameObject.Definition.LocomotorSets.TryGetValue(type, out var setTemplate))
        {
            foreach (var reference in setTemplate.Locomotors)
            {
                var template = reference.Value;
                if (template != null)
                {
                    _locomotorSet.AddLocomotorForLoad(template);
                }
            }
        }
        _currentSetType = type;
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// The physics vocabulary (GPL PhysicsBehaviorModuleData) lives here while this driver
// owns the integrator; it moves to a dedicated physics module when one ports.
// ============================================================================
[SimDataAudited]
public sealed class SimLocomotorUpdateModuleData : UpdateModuleData
{
    internal static SimLocomotorUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<SimLocomotorUpdateModuleData> FieldParseTable =
        new IniParseTable<SimLocomotorUpdateModuleData>
        {
            { "Mass", (parser, x) => x.Mass = parser.ParseFix64() },
            { "ForwardFriction", (parser, x) => x.ForwardFriction = parser.ParseFix64FrictionPerLogicFrame() },
            { "LateralFriction", (parser, x) => x.LateralFriction = parser.ParseFix64FrictionPerLogicFrame() },
            { "AerodynamicFriction", (parser, x) => x.AerodynamicFriction = parser.ParseFix64FrictionPerLogicFrame() },
            { "Gravity", (parser, x) => x.GravityPerFrame = parser.ParseFix64AccelerationPerLogicFrame() },
        };

    /// <summary>GPL DEFAULT_MASS = 1.</summary>
    public Fix64 Mass { get; private set; } = Fix64.One;

    /// <summary>
    /// Per-frame friction coefficients. GPL defaults are 0.15/frame at 30 fps; the
    /// linear per-second equivalent (4.5/s) lands at 0.9/frame at the frozen 5 Hz.
    /// The BFME2 binary's own constants are an open conformance question (design note).
    /// </summary>
    public Fix64 ForwardFriction { get; private set; } = Fix64.FromDecimalLiteral("0.9");

    public Fix64 LateralFriction { get; private set; } = Fix64.FromDecimalLiteral("0.9");

    /// <summary>GPL DEFAULT_AERO_FRICTION = 0.</summary>
    public Fix64 AerodynamicFriction { get; private set; }

    /// <summary>
    /// Gravity in dist/frame^2 (negative = down). GPL GlobalData default is -1/frame^2
    /// at 30 fps = -900 dist/s^2 = -36/frame^2 at the frozen 5 Hz. INI field "Gravity"
    /// is in dist/s^2 and is divided by fps^2 at parse.
    /// </summary>
    public Fix64 GravityPerFrame { get; private set; } = Fix64.FromDecimalLiteral("-36");

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new SimLocomotorUpdate(gameObject, gameEngine.SimContext, this);
    }
}
