// BridgeScaffoldBehavior - R12 port, translated from generals-gpl
// GameLogic/Module/BridgeScaffoldBehavior.h/.cpp (GPL semantics reference).
//
// Behavioral facts translated from the GPL source:
//   - a 5-state motion machine (ScaffoldTargetMotion: STILL, RISE, BUILD_ACROSS,
//     TEAR_DOWN_ACROSS, SINK) driving the object's OWN world position between three
//     externally-supplied points (createPos, riseToPos, buildPos), set once via
//     setPositions() and never touched again by this module.
//   - setMotion() picks the destination for the new state (RISE/TEAR_DOWN_ACROSS ->
//     riseToPos, BUILD_ACROSS -> buildPos, SINK -> createPos; STILL has no case in the GPL
//     switch, so m_targetPos is left exactly as it was - translated verbatim as a no-op
//     default arm, not "fixed" to clear it).
//   - reverseMotion() inverts the current state: STILL<->TEAR_DOWN_ACROSS,
//     RISE<->SINK, BUILD_ACROSS<->TEAR_DOWN_ACROSS (asymmetric: TEAR_DOWN_ACROSS always
//     reverses to BUILD_ACROSS, matching the GPL switch exactly - not a typo).
//   - update() (GPL always returns UPDATE_SLEEP_NONE, even for STILL - the module never
//     sleeps for the retail lifetime of the object): while STILL, do nothing and return
//     immediately. Otherwise, per motion type, move from a per-leg (start, end) pair at
//     verticalSpeed (RISE/SINK) or lateralSpeed (BUILD_ACROSS/TEAR_DOWN_ACROSS) toward
//     m_targetPos: the ease-in-toward-target speed scale is `(ourDistance / (legLength *
//     0.25)) * topSpeed`, clamped to [topSpeed * 0.08, topSpeed] and floored at 0.001 so
//     the motion can never fully stall - translated verbatim, not simplified. Overshoot is
//     detected by dotting the vector from the *new* position to the target against the
//     original direction vector: <= 0 means the step reached or passed the target, at which
//     point the position snaps exactly to m_targetPos and the state auto-advances
//     (RISE->BUILD_ACROSS->STILL, TEAR_DOWN_ACROSS->SINK). SINK's arrival additionally
//     self-destroys the object (GPL TheGameLogic->destroyObject(us)) - the GPL source then
//     STILL writes the final position via setPosition() after the destroy call (falls out
//     of the inner switch back into the shared setPosition() at the bottom of update());
//     replicated exactly via BridgeScaffoldTransformBridge.Push happening after
//     Context.GameLogic.DestroyObject below.
//
// Position/yaw crossing (D-7 boundary, api-freeze-v1 shape): every frame's target math runs
// entirely in Fix64; the GameObject's float transform is read and written exactly once per
// tick through BridgeScaffoldTransformBridge, the dedicated crossing file for this module
// (same pattern as FloodTransformBridge/SimTransformBridge). setPositions()'s three points
// arrive as Fix64 already - they are supplied programmatically by the (not-yet-ported)
// bridge-repair caller, never parsed from INI (the GPL ModuleData class carries no fields
// either - the retail INI block for BridgeScaffoldBehavior is empty).
//
// Every mutable sim field appears in Xfer exactly once (§3), field order mirrors the GPL
// xfer() order (m_targetMotion, m_createPos, m_riseToPos, m_buildPos, m_lateralSpeed,
// m_verticalSpeed, m_targetPos).

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

/// <summary>Motion state for a bridge scaffold object (GPL ScaffoldTargetMotion).</summary>
public enum ScaffoldTargetMotion
{
    Still,
    Rise,
    BuildAcross,
    TearDownAcross,
    Sink,
}

/// <summary>
/// Allows the object to surround the parent object like a scaffold.
/// </summary>
[SimState]
public sealed class BridgeScaffoldBehavior : UpdateModule
{
    /// <summary>GPL: totalDistance = legLength * 0.25f (ease-in only starts inside the final quarter of the leg).</summary>
    private static readonly Fix64 DistanceScale = Fix64.FromDecimalLiteral("0.25");

    /// <summary>GPL: minSpeed = topSpeed * 0.08f.</summary>
    private static readonly Fix64 MinSpeedFraction = Fix64.FromDecimalLiteral("0.08");

    /// <summary>GPL: floor so speed can never get "so incredibly small" the motion never finishes.</summary>
    private static readonly Fix64 MinSpeedFloor = Fix64.FromDecimalLiteral("0.001");

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----
    private ScaffoldTargetMotion _targetMotion;
    private FixVector3 _createPos;
    private FixVector3 _riseToPos;
    private FixVector3 _buildPos;
    private Fix64 _lateralSpeed;
    private Fix64 _verticalSpeed;
    private FixVector3 _targetPos;

    public BridgeScaffoldBehavior(GameObject gameObject, ISimContext context, BridgeScaffoldBehaviorModuleData data)
        : base(gameObject, context)
    {
        _targetMotion = ScaffoldTargetMotion.Still;
        _createPos = FixVector3.Zero;
        _riseToPos = FixVector3.Zero;
        _buildPos = FixVector3.Zero;
        _targetPos = FixVector3.Zero;
        _lateralSpeed = Fix64.One;
        _verticalSpeed = Fix64.One;

        // GPL update() always returns UPDATE_SLEEP_NONE, even while STILL: this module
        // never sleeps for the retail lifetime of the object.
        SetWakeFrame(UpdateSleepTime.None);
    }

    // ---- public surface (the retail BridgeScaffoldBehaviorInterface) ----

    public ScaffoldTargetMotion CurrentMotion => _targetMotion;

    /// <summary>Sets all three target positions this scaffold cares about (GPL setPositions).</summary>
    public void SetPositions(in FixVector3 createPos, in FixVector3 riseToPos, in FixVector3 buildPos)
    {
        _createPos = createPos;
        _riseToPos = riseToPos;
        _buildPos = buildPos;
    }

    /// <summary>Sets us moving to the right target position for the requested motion type (GPL setMotion).</summary>
    public void SetMotion(ScaffoldTargetMotion targetMotion)
    {
        _targetMotion = targetMotion;

        switch (_targetMotion)
        {
            case ScaffoldTargetMotion.Rise:
            case ScaffoldTargetMotion.TearDownAcross:
                _targetPos = _riseToPos;
                break;

            case ScaffoldTargetMotion.BuildAcross:
                _targetPos = _buildPos;
                break;

            case ScaffoldTargetMotion.Sink:
                _targetPos = _createPos;
                break;

                // STILL: the GPL switch has no case for it, so m_targetPos is left exactly
                // as it was - no default arm here either.
        }
    }

    /// <summary>Whatever our current state of motion is, reverse it (GPL reverseMotion).</summary>
    public void ReverseMotion()
    {
        switch (_targetMotion)
        {
            case ScaffoldTargetMotion.Still:
                SetMotion(ScaffoldTargetMotion.TearDownAcross);
                break;

            case ScaffoldTargetMotion.Rise:
                SetMotion(ScaffoldTargetMotion.Sink);
                break;

            case ScaffoldTargetMotion.BuildAcross:
                SetMotion(ScaffoldTargetMotion.TearDownAcross);
                break;

            case ScaffoldTargetMotion.TearDownAcross:
                SetMotion(ScaffoldTargetMotion.BuildAcross);
                break;

            case ScaffoldTargetMotion.Sink:
                SetMotion(ScaffoldTargetMotion.Rise);
                break;
        }
    }

    public void SetLateralSpeed(Fix64 lateralSpeed) => _lateralSpeed = lateralSpeed;

    public void SetVerticalSpeed(Fix64 verticalSpeed) => _verticalSpeed = verticalSpeed;

    // ---- per-frame ----

    public override UpdateSleepTime Update()
    {
        // Do nothing if we're not in motion (GPL: early-out before touching position).
        if (_targetMotion == ScaffoldTargetMotion.Still)
        {
            return UpdateSleepTime.None;
        }

        var ourPos = BridgeScaffoldTransformBridge.PullPosition(GameObject);
        var yaw = BridgeScaffoldTransformBridge.PullYaw(GameObject);

        // Direction vector from our position to the target position, and its normalized form.
        var dirV = _targetPos - ourPos;
        var v = dirV.NormalizedOrZero();

        // Depending on our motion type, we move at different speeds between different
        // (start, end) legs.
        Fix64 topSpeed;
        FixVector3 legStart;
        FixVector3 legEnd;
        switch (_targetMotion)
        {
            case ScaffoldTargetMotion.Rise:
                topSpeed = _verticalSpeed;
                legStart = _createPos;
                legEnd = _riseToPos;
                break;

            case ScaffoldTargetMotion.Sink:
                topSpeed = _verticalSpeed;
                legStart = _riseToPos;
                legEnd = _createPos;
                break;

            case ScaffoldTargetMotion.BuildAcross:
                topSpeed = _lateralSpeed;
                legStart = _riseToPos;
                legEnd = _buildPos;
                break;

            case ScaffoldTargetMotion.TearDownAcross:
            default:
                topSpeed = _lateralSpeed;
                legStart = _buildPos;
                legEnd = _riseToPos;
                break;
        }

        // Adjust speed so it's slower near the end of the motion.
        var totalDistance = (legEnd - legStart).Length() * DistanceScale;
        var ourDistance = (legEnd - ourPos).Length();

        // GPL divides `ourDistance / totalDistance` unconditionally; a zero-length leg only
        // arises from a degenerate SetPositions call (start == end for this motion's target),
        // never hit by normal use. Fix64 division throws on a zero divisor rather than
        // producing the float NaN/Inf the GPL code would silently carry, so that
        // never-exercised leg is guarded here rather than left to throw.
        var speed = totalDistance > Fix64.Zero
            ? (ourDistance / totalDistance) * topSpeed
            : topSpeed;

        var minSpeed = topSpeed * MinSpeedFraction;
        if (speed < minSpeed)
        {
            speed = minSpeed;
        }
        if (speed > topSpeed)
        {
            speed = topSpeed;
        }

        // Make sure speed can't get so incredibly small that we never finish our movement no
        // matter what the speed and distance are.
        if (speed < MinSpeedFloor)
        {
            speed = MinSpeedFloor;
        }

        var newPos = ourPos + v * speed;

        // Will this new position push us beyond our target destination? Dot the vector from
        // the new position to the destination against the vector from our present position
        // to the destination: <= 0 means the step reached or overshot the target.
        var tooFarVector = _targetPos - newPos;
        if (FixVector3.Dot(tooFarVector, dirV) <= Fix64.Zero)
        {
            newPos = _targetPos;

            // We have reached our target position; switch motion to the next state in the
            // chain (which may be to stay still and not move anymore).
            switch (_targetMotion)
            {
                case ScaffoldTargetMotion.Rise:
                    SetMotion(ScaffoldTargetMotion.BuildAcross);
                    break;

                case ScaffoldTargetMotion.BuildAcross:
                    SetMotion(ScaffoldTargetMotion.Still);
                    break;

                case ScaffoldTargetMotion.TearDownAcross:
                    SetMotion(ScaffoldTargetMotion.Sink);
                    break;

                case ScaffoldTargetMotion.Sink:
                    // We are done with a sinking motion; destroy the scaffold object as our
                    // job is done. GPL still writes the final position below even though the
                    // object is already marked destroyed - replicated by falling through to
                    // the unconditional Push after this switch, exactly as the GPL control
                    // flow does.
                    Context.GameLogic.DestroyObject(GameObject);
                    break;
            }
        }

        // Set the new position (GPL: us->setPosition(&newPos), unconditional).
        BridgeScaffoldTransformBridge.Push(GameObject, newPos, yaw);

        // Do not sleep.
        return UpdateSleepTime.None;
    }

    // ---- the single walk (field order mirrors the GPL xfer() order) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferEnum("TargetMotion", ref _targetMotion);
        xfer.XferFixVector3("CreatePos", ref _createPos, Tolerance.Band);
        xfer.XferFixVector3("RiseToPos", ref _riseToPos, Tolerance.Band);
        xfer.XferFixVector3("BuildPos", ref _buildPos, Tolerance.Band);
        xfer.XferFix64("LateralSpeed", ref _lateralSpeed);
        xfer.XferFix64("VerticalSpeed", ref _verticalSpeed);
        xfer.XferFixVector3("TargetPos", ref _targetPos, Tolerance.Band);
    }
}

[SimDataAudited]
public sealed class BridgeScaffoldBehaviorModuleData : UpdateModuleData
{
    internal static BridgeScaffoldBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<BridgeScaffoldBehaviorModuleData> FieldParseTable = new IniParseTable<BridgeScaffoldBehaviorModuleData>();

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new BridgeScaffoldBehavior(gameObject, gameEngine.SimContext, this);
    }
}
