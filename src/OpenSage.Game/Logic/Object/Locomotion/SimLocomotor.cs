// SimLocomotor - the deterministic per-frame steering/acceleration half of the S2
// locomotor system. Fresh code; behavioral reference (semantics only): generals-gpl
// GeneralsMD GameLogic/Object/Locomotor.cpp (class Locomotor). All math is Fix64 with
// LUT trig (F2: FixTrig.Sin/Cos/Atan2, never System.Math).
//
// WHAT A FRAME LOOKS LIKE for a moving object (order is conformance-critical):
//   locomotor pass (this class, called by SimLocomotorUpdate):
//     a. compute the goal-relative heading with FixTrig.Atan2 and rotate the object
//        (SimPhysics.Yaw, and Position too when TurnPivotOffset is nonzero) by at most
//        the turn rate;
//     b. compute a goal speed from desired speed, turn modulation, and braking state;
//     c. convert (goalSpeed - actualSpeed) into a motive force, clipped to
//        mass * min(|accel or braking|, |speedDelta|), along the (new) heading;
//        hand it to SimPhysics.ApplyMotiveForce (accel accumulation only);
//     d. handleBehaviorZ (z-behavior force or direct z placement);
//     e. when braking, the position cheat advances x/y directly toward the goal by the
//        current forward speed (clamped to remaining distance) - SimPhysics then skips
//        x/y integration for this frame (IsBraking).
//   physics pass: SimPhysics.Integrate (see SimPhysics.cs header for its 8 steps).
//
// Appearance coverage this round: TWO_LEGS (incl. wander), TREADS, FOUR_WHEELS /
// MOTORCYCLE (incl. backwards + three-point turns; the mid-turn impassable-terrain
// projection is pinned "always passable" until S5 pathfinding lands), HOVER, OTHER.
// CLIMBER falls back to LEGS, THRUST and WINGS fall back to OTHER (no BFME2-conformant
// consumer yet; recorded in the design note as out-of-round).
// Z behaviors: NO_Z_MOTIVE_FORCE, SEA_LEVEL (surface height - the water table is not a
// sim seam yet), FIXED_SURFACE_RELATIVE_HEIGHT, FIXED_ABSOLUTE_HEIGHT,
// SURFACE_RELATIVE_HEIGHT, ABSOLUTE_HEIGHT; RELATIVE_TO_GROUND_AND_BUILDINGS and
// SMOOTH_RELATIVE_TO_HIGHEST_LAYER degrade to SURFACE_RELATIVE_HEIGHT (no partition /
// layer seam yet).
//
// Frame-rate constants re-derived at the frozen 5 Hz (F6) from GPL's 30 fps forms,
// rounding pinned per constant in the design note:
//   MotiveFrames  = ceil(5/3)  = 2      DonutFrames = ceil(2.5s * 5) = 13
//   MinBrakingVel = 10/5       = 2      PathfindCellSize = 10 (map units, rate-free)

using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Rng;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object.Locomotion;

[SimState]
public sealed class SimLocomotor
{
    // GPL LocoFlag - bit positions preserved for our own save compatibility (values are
    // OUR contract, F9; they merely happen to match the reference's order).
    private enum LocoFlag
    {
        IsBraking = 0,
        AllowInvalidPosition = 1,
        MaintainPosIsValid = 2,
        PreciseZPos = 3,
        NoSlowDownAsApproachingDest = 4,
        OverWater = 5,
        UltraAccurate = 6,
        MovingBackwards = 7,
        DoingThreePointTurn = 8,
        Climbing = 9,
        IsCloseEnoughDist3D = 10,
        OffsetIncreasing = 11,
    }

    internal static readonly Fix64 PathfindCellSize = Fix64.FromDecimalLiteral("10");
    private static readonly Fix64 DonutDistance = Fix64.FromDecimalLiteral("40"); // 4 cells
    internal const uint DonutFrames = 13;                                          // ceil(2.5s * 5)
    private static readonly Fix64 MinBrakingVel = Fix64.FromDecimalLiteral("2");   // cell/fps
    private static readonly Fix64 MaxBrakingFactor = Fix64.FromDecimalLiteral("5");
    private static readonly Fix64 SlowDownFudge = Fix64.FromDecimalLiteral("1.05");
    private static readonly Fix64 TinyDistance = Fix64.FromDecimalLiteral("0.1");
    private static readonly Fix64 TinyAccel = Fix64.FromDecimalLiteral("0.001");
    private static readonly Fix64 QuarterPi = Fix64.FromRaw(Fix64.PiOver2.RawValue / 2);
    private static readonly Fix64 SmallTurn = Fix64.FromRaw(Fix64.Pi.RawValue / 20);
    private static readonly Fix64 FifteenDegrees = Fix64.FromRaw(Fix64.Pi.RawValue / 12);
    private static readonly Fix64 TurnModulationCutoff = Fix64.FromDecimalLiteral("0.05");
    private static readonly Fix64 PointSixty = Fix64.FromDecimalLiteral("0.6");
    private static readonly Fix64 PointSeventyFive = Fix64.FromDecimalLiteral("0.75");
    private static readonly Fix64 OnePointFive = Fix64.FromDecimalLiteral("1.5");
    private static readonly Fix64 OnePointOne = Fix64.FromDecimalLiteral("1.1");
    private static readonly Fix64 UltraAccurateExtraFriction = Fix64.Half;

    /// <summary>
    /// The damage state at (and past) which movement uses the damaged template values:
    /// GPL IS_CONDITION_BETTER(condition, TheGlobalData-&gt;m_movementPenaltyDamageState),
    /// whose GlobalData default is REALLYDAMAGED. Pinned as that default until GameData
    /// gains a quantized sim parse (design-note finding).
    /// </summary>
    internal const BodyDamageType MovementPenaltyDamageState = BodyDamageType.ReallyDamaged;

    private readonly LocomotorTemplate _template;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----
    private FixVector3 _maintainPos;
    private Fix64 _brakingFactor = Fix64.One;
    private Fix64 _maxLift = LocomotorTemplate.SimBigNumber;
    private Fix64 _maxSpeed = LocomotorTemplate.SimBigNumber;
    private Fix64 _maxAccel = LocomotorTemplate.SimBigNumber;
    private Fix64 _maxBraking = LocomotorTemplate.SimBigNumber;
    private Fix64 _maxTurnRate = LocomotorTemplate.SimBigNumber;
    private Fix64 _closeEnoughDist;
    private uint _flags;
    private Fix64 _preferredHeight;
    private Fix64 _preferredHeightDamping;
    private Fix64 _angleOffset;
    private Fix64 _offsetIncrement;
    private LogicFrame _donutTimer;

    // Geometry, quantized once by the module at creation (float-substrate crossing lives
    // in SimTransformBridge, never here).
    private Fix64 _boundingCircleRadius;
    private Fix64 _majorRadius;

    // Re-entrancy guard: GPL's Legs path calls locoUpdate_moveTowardsAngle, which for
    // minSpeed > 0 recurses into locoUpdate_moveTowardsPosition. A LEGS template with a
    // nonzero MinSpeed would recurse forever in the original too (no shipping data does
    // this); we guard instead of crashing. Not sim state: always false between frames.
    private bool _inMoveTowardsAngle;

    public SimLocomotor(LocomotorTemplate template, ISimRandom random, LogicFrame now)
        : this(template)
    {
        // GPL ctor: three logic-RNG draws, kept in the reference's order so the draw
        // count (conformance channel 5) matches a port of the same creation path.
        var piOver6 = Fix64.FromRaw(Fix64.Pi.RawValue / 6);
        _angleOffset = random.NextFix64(-piOver6, piOver6);

        var piOver40 = Fix64.FromRaw(Fix64.Pi.RawValue / 40);
        var wanderScale = random.NextFix64(
            Fix64.FromDecimalLiteral("0.8"), Fix64.FromDecimalLiteral("1.2"));
        var wanderLength = template.SimWanderLengthFactor;
        _offsetIncrement = wanderLength == Fix64.Zero
            ? Fix64.Zero
            : piOver40 * (wanderScale / wanderLength);

        SetFlag(LocoFlag.OffsetIncreasing, random.Next(0, 1) != 0);
        _donutTimer = now + new LogicFrameSpan(DonutFrames);
    }

    /// <summary>Load-path ctor: NO rng draws; every drawn value arrives via Xfer.</summary>
    internal SimLocomotor(LocomotorTemplate template)
    {
        _template = template;
        _closeEnoughDist = template.SimCloseEnoughDist;
        SetFlag(LocoFlag.IsCloseEnoughDist3D, template.CloseEnoughDist3D);
        _preferredHeight = template.SimPreferredHeight;
        _preferredHeightDamping = template.SimPreferredHeightDamping;
    }

    public LocomotorTemplate Template => _template;
    public Surfaces LegalSurfaces => _template.Surfaces;
    public LocomotorAppearance Appearance => _template.Appearance;
    public bool IsDownhillOnly => _template.DownhillOnly;
    public Fix64 MinSpeed => _template.SimMinSpeed;
    public Fix64 CloseEnoughDist => _closeEnoughDist;
    public bool IsCloseEnoughDist3D => GetFlag(LocoFlag.IsCloseEnoughDist3D);
    public bool IsUltraAccurate => GetFlag(LocoFlag.UltraAccurate);
    public bool IsMovingBackwards => GetFlag(LocoFlag.MovingBackwards);
    public bool IsBraking => GetFlag(LocoFlag.IsBraking);

    private bool GetFlag(LocoFlag f) => (_flags & (1u << (int)f)) != 0;

    private void SetFlag(LocoFlag f, bool value)
    {
        if (value)
        {
            _flags |= 1u << (int)f;
        }
        else
        {
            _flags &= ~(1u << (int)f);
        }
    }

    internal void SetGeometry(Fix64 boundingCircleRadius, Fix64 majorRadius)
    {
        _boundingCircleRadius = boundingCircleRadius;
        _majorRadius = majorRadius;
    }

    public void SetUltraAccurate(bool value) => SetFlag(LocoFlag.UltraAccurate, value);
    public void SetNoSlowDownAsApproachingDest(bool value) => SetFlag(LocoFlag.NoSlowDownAsApproachingDest, value);
    public void SetUsePreciseZPos(bool value) => SetFlag(LocoFlag.PreciseZPos, value);
    public void SetAllowInvalidPosition(bool value) => SetFlag(LocoFlag.AllowInvalidPosition, value);
    public void SetCloseEnoughDist(Fix64 dist) => _closeEnoughDist = dist;
    public void SetCloseEnoughDist3D(bool value) => SetFlag(LocoFlag.IsCloseEnoughDist3D, value);
    public void SetMaxSpeed(Fix64 speed) => _maxSpeed = speed;
    public void SetMaxAcceleration(Fix64 accel) => _maxAccel = accel;
    public void SetMaxBraking(Fix64 braking) => _maxBraking = braking;
    public void SetMaxTurnRate(Fix64 turn) => _maxTurnRate = turn;
    public void SetMaxLift(Fix64 lift) => _maxLift = lift;
    public void SetPreferredHeight(Fix64 height) => _preferredHeight = height;

    /// <summary>GPL startMove: reset the donut timer.</summary>
    public void StartMove(LogicFrame now) => _donutTimer = now + new LogicFrameSpan(DonutFrames);

    // ------------------------------------------------------------------ condition maxes

    private static bool ConditionBetterThanPenalty(BodyDamageType condition) =>
        condition < MovementPenaltyDamageState;

    public Fix64 GetMaxSpeedForCondition(BodyDamageType condition)
    {
        var speed = ConditionBetterThanPenalty(condition)
            ? _template.SimMaxSpeed
            : _template.SimMaxSpeedDamaged;
        return FixMath.Min(speed, _maxSpeed);
    }

    public Fix64 GetMaxTurnRate(BodyDamageType condition)
    {
        var turn = ConditionBetterThanPenalty(condition)
            ? _template.SimMaxTurnRate
            : _template.SimMaxTurnRateDamaged;
        turn = FixMath.Min(turn, _maxTurnRate);
        if (GetFlag(LocoFlag.UltraAccurate))
        {
            turn *= Fix64.Two;  // GPL TURN_FACTOR = 2
        }
        return turn;
    }

    public Fix64 GetMaxAcceleration(BodyDamageType condition)
    {
        var accel = ConditionBetterThanPenalty(condition)
            ? _template.SimAcceleration
            : _template.SimAccelerationDamaged;
        return FixMath.Min(accel, _maxAccel);
    }

    public Fix64 GetBraking() => FixMath.Min(_template.SimBraking, _maxBraking);

    public Fix64 GetMaxLift(BodyDamageType condition)
    {
        var lift = ConditionBetterThanPenalty(condition)
            ? _template.SimLift
            : _template.SimLiftDamaged;
        return FixMath.Min(lift, _maxLift);
    }

    /// <summary>GPL calcMinTurnRadius: minSpeed / maxTurnRate (huge-but-finite when turnless).</summary>
    public Fix64 CalcMinTurnRadius(BodyDamageType condition, out Fix64 timeToTravelThatDist)
    {
        var minSpeed = MinSpeed;
        var maxTurnRate = GetMaxTurnRate(condition);
        var radius = maxTurnRate > Fix64.Zero
            ? minSpeed / maxTurnRate
            : LocomotorTemplate.SimBigNumber;
        timeToTravelThatDist = minSpeed > Fix64.Zero ? radius / minSpeed : Fix64.Zero;
        return radius;
    }

    /// <summary>
    /// GPL calcSlowDownDist: 1.05 * (curSpeed - desiredSpeed)^2 / (2 |braking|),
    /// zero when already at/below the desired speed.
    /// </summary>
    internal static Fix64 CalcSlowDownDist(Fix64 curSpeed, Fix64 desiredSpeed, Fix64 maxBraking)
    {
        var delta = curSpeed - desiredSpeed;
        if (delta <= Fix64.Zero)
        {
            return Fix64.Zero;
        }
        var dist = delta * delta / Fix64.Abs(maxBraking) * Fix64.Half;
        return dist * SlowDownFudge;
    }

    // ------------------------------------------------------------------ rotation

    /// <summary>
    /// GPL rotateObjAroundLocoPivot. Zero pivot offset (or braking): clamp the yaw step to
    /// the turn rate. Nonzero offset: the same clamped step applied as a rotation of the
    /// whole transform AROUND the pivot point ahead of/behind the center, which both turns
    /// the heading and orbits the position.
    /// </summary>
    public PhysicsTurningType RotateTowardsPosition(
        SimPhysics phys, BodyDamageType condition, in FixVector3 goalPos, out Fix64 relAngle)
    {
        return RotateObjAroundLocoPivot(phys, goalPos, GetMaxTurnRate(condition), out relAngle);
    }

    public PhysicsTurningType RotateObjAroundLocoPivot(
        SimPhysics phys, in FixVector3 goalPos, Fix64 maxTurnRate, out Fix64 relAngle)
    {
        relAngle = Fix64.Zero;
        var angle = phys.Yaw;
        var offset = _template.SimTurnPivotOffset;
        if (GetFlag(LocoFlag.IsBraking))
        {
            // When braking we do exact movement towards the goal; pivoting moves the
            // object and can make us miss it.
            offset = Fix64.Zero;
        }

        if (offset != Fix64.Zero)
        {
            var turnPointOffset = offset * _boundingCircleRadius;
            var dir = phys.UnitDirection2D();
            var turnPos = new FixVector3(
                phys.Position.X + dir.X * turnPointOffset,
                phys.Position.Y + dir.Y * turnPointOffset,
                phys.Position.Z);
            var dx = goalPos.X - turnPos.X;
            var dy = goalPos.Y - turnPos.Y;
            if (Fix64.Abs(dx) < TinyDistance && Fix64.Abs(dy) < TinyDistance)
            {
                // Too close: rounding twitch guard.
                return PhysicsTurningType.None;
            }
            var desiredAngle = FixTrig.Atan2(dy, dx);
            var amount = SimAngle.Diff(desiredAngle, angle);
            relAngle = amount;
            var turn = PhysicsTurningType.None;
            if (amount > maxTurnRate)
            {
                amount = maxTurnRate;
                turn = PhysicsTurningType.Positive;
            }
            else if (amount < -maxTurnRate)
            {
                amount = -maxTurnRate;
                turn = PhysicsTurningType.Negative;
            }

            // Rotate the transform around turnPos by 'amount' (the GPL
            // translate/rotateZ/untranslate matrix product, done directly in 2D).
            var cos = FixTrig.Cos(amount);
            var sin = FixTrig.Sin(amount);
            var relX = phys.Position.X - turnPos.X;
            var relY = phys.Position.Y - turnPos.Y;
            phys.Position = new FixVector3(
                turnPos.X + relX * cos - relY * sin,
                turnPos.Y + relX * sin + relY * cos,
                phys.Position.Z);
            phys.Yaw = SimAngle.Normalize(angle + amount);
            return turn;
        }
        else
        {
            var dx = goalPos.X - phys.Position.X;
            var dy = goalPos.Y - phys.Position.Y;
            var desiredAngle = FixTrig.Atan2(dy, dx);
            var amount = SimAngle.Diff(desiredAngle, angle);
            relAngle = amount;
            var turn = PhysicsTurningType.None;
            if (amount > maxTurnRate)
            {
                amount = maxTurnRate;
                turn = PhysicsTurningType.Positive;
            }
            else if (amount < -maxTurnRate)
            {
                amount = -maxTurnRate;
                turn = PhysicsTurningType.Negative;
            }
            phys.Yaw = SimAngle.Normalize(angle + amount);
            return turn;
        }
    }

    // ------------------------------------------------------------------ the update entries

    /// <summary>
    /// GPL locoUpdate_moveTowardsPosition. <paramref name="surfaceZ"/> is the ground/water
    /// surface height at the current position (Fix64-valued terrain seam).
    /// </summary>
    public void MoveTowardsPosition(
        SimPhysics phys,
        BodyDamageType condition,
        in FixVector3 goalPos,
        Fix64 onPathDistToGoal,
        Fix64 desiredSpeed,
        ref bool blocked,
        LogicFrame now,
        Fix64 surfaceZ,
        bool isProjectile = false)
    {
        SetFlag(LocoFlag.MaintainPosIsValid, false);

        var maxSpeed = GetMaxSpeedForCondition(condition);
        if (desiredSpeed > maxSpeed)
        {
            desiredSpeed = maxSpeed;
        }

        var distToStopAtMaxSpeed = maxSpeed / GetBraking() * maxSpeed * Fix64.Half;
        if (onPathDistToGoal > PathfindCellSize && onPathDistToGoal > distToStopAtMaxSpeed)
        {
            SetFlag(LocoFlag.IsBraking, false);
            _brakingFactor = Fix64.One;
        }

        // (GPL: invalid-position fixing needs the pathfinder; pinned out until S5.)

        // If the actual distance is farther than the path distance, use it so we get there.
        var dx = goalPos.X - phys.Position.X;
        var dy = goalPos.Y - phys.Position.Y;
        var dz = goalPos.Z - phys.Position.Z;
        var dist = Fix64.Sqrt(dx * dx + dy * dy);
        if (dist > onPathDistToGoal)
        {
            if (!isProjectile && dist > Fix64.Two * onPathDistToGoal)
            {
                SetFlag(LocoFlag.IsBraking, true);
            }
            onPathDistToGoal = dist;
        }

        var treatAsAirborne = phys.Position.Z - surfaceZ > -(Fix64.FromRaw(9L << 32) * phys.Gravity);

        // Zero motive force: flags the object as locomotor-driven even when no accel lands.
        phys.ApplyMotiveForce(FixVector3.Zero, now);

        if (blocked)
        {
            if (desiredSpeed > phys.VelocityMagnitude())
            {
                blocked = false;
            }
            if (treatAsAirborne && (_template.Surfaces & Surfaces.Air) != 0)
            {
                blocked = false;   // airborne flyers don't collide
            }
        }

        if (blocked)
        {
            phys.ScrubVelocity2D(desiredSpeed);
            var turnRate = GetMaxTurnRate(condition);
            if (_template.SimWanderWidthFactor == Fix64.Zero)
            {
                blocked = PhysicsTurningType.None !=
                    RotateObjAroundLocoPivot(phys, goalPos, turnRate, out _);
            }
            HandleBehaviorZ(phys, condition, goalPos, surfaceZ, now);
            return;
        }

        if (_template.Appearance == LocomotorAppearance.Wings)
        {
            SetFlag(LocoFlag.IsBraking, false);
        }

        var wasBraking = phys.IsBraking;

        phys.Turning = PhysicsTurningType.None;
        if (_template.AllowAirborneMotiveForce || !treatAsAirborne)
        {
            switch (_template.Appearance)
            {
                case LocomotorAppearance.TwoLegs:
                case LocomotorAppearance.Climber:      // CLIMB out-of-round; LEGS shape
                    MoveTowardsPositionLegs(phys, condition, goalPos, onPathDistToGoal, desiredSpeed, now, surfaceZ);
                    break;
                case LocomotorAppearance.FourWheels:
                case LocomotorAppearance.Motorcycle:
                    MoveTowardsPositionWheels(phys, condition, goalPos, onPathDistToGoal, desiredSpeed, now);
                    break;
                case LocomotorAppearance.Treads:
                    MoveTowardsPositionTreads(phys, condition, goalPos, onPathDistToGoal, desiredSpeed, now);
                    break;
                case LocomotorAppearance.Hover:
                    // GPL: hover 2D component == OTHER (the over-water model condition is
                    // client-side and out of the sim).
                    MoveTowardsPositionOther(phys, condition, goalPos, onPathDistToGoal, desiredSpeed, now);
                    break;
                default:
                    // OTHER, plus THRUST/WINGS fallback (out-of-round appearances).
                    MoveTowardsPositionOther(phys, condition, goalPos, onPathDistToGoal, desiredSpeed, now);
                    break;
            }
        }

        HandleBehaviorZ(phys, condition, goalPos, surfaceZ, now);
        phys.IsBraking = GetFlag(LocoFlag.IsBraking);

        if (wasBraking)
        {
            // Objects that are braking don't follow normal physics: they end up at their
            // destination exactly (the braking position cheat).
            var pos = phys.Position;
            if (isProjectile)
            {
                SetFlag(LocoFlag.IsBraking, true);
                phys.IsBraking = true;
                var dist3 = Fix64.Sqrt(dx * dx + dy * dy + dz * dz);
                var vel = phys.VelocityMagnitude();
                if (vel < MinBrakingVel)
                {
                    vel = MinBrakingVel;
                }
                if (vel > dist3)
                {
                    vel = dist3;
                }
                if (dist3 > TinyAccel)
                {
                    var inv = Fix64.One / dist3;
                    pos = new FixVector3(
                        pos.X + dx * inv * vel,
                        pos.Y + dy * inv * vel,
                        pos.Z + dz * inv * vel);
                }
            }
            else
            {
                if (dist > TinyAccel)
                {
                    var vel = Fix64.Abs(phys.ForwardSpeed2D());
                    if (vel < MinBrakingVel)
                    {
                        vel = MinBrakingVel;
                    }
                    if (vel > dist)
                    {
                        vel = dist;
                    }
                    var inv = Fix64.One / dist;
                    pos = new FixVector3(
                        pos.X + dx * inv * vel,
                        pos.Y + dy * inv * vel,
                        pos.Z);
                }
            }
            phys.Position = pos;
        }
    }

    /// <summary>GPL locoUpdate_moveTowardsAngle.</summary>
    public void MoveTowardsAngle(
        SimPhysics phys, BodyDamageType condition, Fix64 goalAngle, LogicFrame now, Fix64 surfaceZ)
    {
        SetFlag(LocoFlag.MaintainPosIsValid, false);

        var minSpeed = MinSpeed;
        if (minSpeed > Fix64.Zero && !_inMoveTowardsAngle)
        {
            _inMoveTowardsAngle = true;
            try
            {
                // Can't stay in one place: move in the desired direction at min speed.
                var desiredPos = new FixVector3(
                    phys.Position.X + FixTrig.Cos(goalAngle) * minSpeed * Fix64.Two,
                    phys.Position.Y + FixTrig.Sin(goalAngle) * minSpeed * Fix64.Two,
                    phys.Position.Z);
                var blocked = false;
                MoveTowardsPosition(
                    phys, condition, desiredPos, LocomotorTemplate.SimBigNumber, minSpeed,
                    ref blocked, now, surfaceZ);
            }
            finally
            {
                _inMoveTowardsAngle = false;
            }
        }
        else
        {
            var thousand = Fix64.FromRaw(1000L << 32);
            var desiredPos = new FixVector3(
                phys.Position.X + FixTrig.Cos(goalAngle) * thousand,
                phys.Position.Y + FixTrig.Sin(goalAngle) * thousand,
                phys.Position.Z);
            var rotating = RotateTowardsPosition(phys, condition, desiredPos, out _);
            phys.Turning = rotating;
            HandleBehaviorZ(phys, condition, phys.Position, surfaceZ, now);
        }
    }

    /// <summary>
    /// GPL locoUpdate_maintainCurrentPosition. Returns true when constant calling is
    /// still required (hovering); false when the object can rest.
    /// </summary>
    public bool MaintainCurrentPosition(
        SimPhysics phys, BodyDamageType condition, LogicFrame now, Fix64 surfaceZ)
    {
        if (!GetFlag(LocoFlag.MaintainPosIsValid))
        {
            _maintainPos = phys.Position;
            SetFlag(LocoFlag.MaintainPosIsValid, true);
        }

        _donutTimer = now + new LogicFrameSpan(DonutFrames);
        SetFlag(LocoFlag.IsBraking, false);
        phys.IsBraking = false;

        bool requiresConstantCalling;
        switch (_template.Appearance)
        {
            case LocomotorAppearance.Hover:
                MaintainCurrentPositionHover(phys, condition, now);
                requiresConstantCalling = true;
                break;
            case LocomotorAppearance.TwoLegs:
            case LocomotorAppearance.Climber:
            case LocomotorAppearance.FourWheels:
            case LocomotorAppearance.Motorcycle:
            case LocomotorAppearance.Treads:
                MaintainCurrentPositionOther(phys, now);
                requiresConstantCalling = false;
                break;
            default:
                // OTHER (+ THRUST/WINGS fallback, out-of-round).
                MaintainCurrentPositionOther(phys, now);
                requiresConstantCalling = true;
                break;
        }

        if (HandleBehaviorZ(phys, condition, _maintainPos, surfaceZ, now))
        {
            requiresConstantCalling = true;
        }
        return requiresConstantCalling;
    }

    // ------------------------------------------------------------------ appearance bodies

    private void ApplyGoalSpeedForce(
        SimPhysics phys, Fix64 goalSpeed, Fix64 actualSpeed, Fix64 maxAcceleration,
        Fix64 brakingScale, in FixVector3 dir, LogicFrame now)
    {
        // Shared "maintain goal speed" tail of every appearance body: clip the accel (or
        // braking) force to exactly what reaches goalSpeed, along dir.
        var speedDelta = goalSpeed - actualSpeed;
        if (speedDelta == Fix64.Zero)
        {
            return;
        }
        var mass = phys.Mass;
        var acceleration = speedDelta > Fix64.Zero
            ? maxAcceleration
            : -(brakingScale * GetBraking());
        var accelForce = mass * acceleration;
        var maxForceNeeded = mass * speedDelta;
        if (Fix64.Abs(accelForce) > Fix64.Abs(maxForceNeeded))
        {
            accelForce = maxForceNeeded;
        }
        phys.ApplyMotiveForce(
            new FixVector3(accelForce * dir.X, accelForce * dir.Y, Fix64.Zero), now);
    }

    private void MoveTowardsPositionLegs(
        SimPhysics phys, BodyDamageType condition, in FixVector3 goalPos,
        Fix64 onPathDistToGoal, Fix64 desiredSpeed, LogicFrame now, Fix64 surfaceZ)
    {
        if (IsDownhillOnly && phys.Position.Z < goalPos.Z)
        {
            return;   // pinewood derby: gravity only
        }

        var maxAcceleration = GetMaxAcceleration(condition);
        var maxSpeed = GetMaxSpeedForCondition(condition);
        if (desiredSpeed > maxSpeed)
        {
            desiredSpeed = maxSpeed;
        }

        var actualSpeed = phys.ForwardSpeed2D();
        var angle = phys.Yaw;
        var desiredAngle = FixTrig.Atan2(
            goalPos.Y - phys.Position.Y, goalPos.X - phys.Position.X);

        if (_template.SimWanderWidthFactor != Fix64.Zero)
        {
            // Wander: oscillate the desired angle around the goal direction.
            var angleLimit = Fix64.FromRaw(Fix64.Pi.RawValue / 8) * _template.SimWanderWidthFactor;
            if (GetFlag(LocoFlag.OffsetIncreasing))
            {
                _angleOffset += _offsetIncrement * actualSpeed;
                if (_angleOffset > angleLimit)
                {
                    SetFlag(LocoFlag.OffsetIncreasing, false);
                }
            }
            else
            {
                _angleOffset -= _offsetIncrement * actualSpeed;
                if (_angleOffset < -angleLimit)
                {
                    SetFlag(LocoFlag.OffsetIncreasing, true);
                }
            }
            desiredAngle = SimAngle.Normalize(desiredAngle + _angleOffset);
        }

        var relAngle = SimAngle.Diff(desiredAngle, angle);
        MoveTowardsAngle(phys, condition, desiredAngle, now, surfaceZ);

        // Modulate speed by how much we still have to turn.
        var angleCoeff = Fix64.Abs(relAngle) / QuarterPi;
        if (angleCoeff > Fix64.One)
        {
            angleCoeff = Fix64.One;
        }
        var goalSpeed = (Fix64.One - angleCoeff) * desiredSpeed;

        var slowDownDist = CalcSlowDownDist(actualSpeed, _template.SimMinSpeed, GetBraking());
        if (onPathDistToGoal < slowDownDist && !GetFlag(LocoFlag.NoSlowDownAsApproachingDest))
        {
            goalSpeed = _template.SimMinSpeed;
        }

        ApplyGoalSpeedForce(
            phys, goalSpeed, actualSpeed, maxAcceleration, Fix64.One,
            phys.UnitDirection2D(), now);
    }

    private void MoveTowardsPositionTreads(
        SimPhysics phys, BodyDamageType condition, in FixVector3 goalPos,
        Fix64 onPathDistToGoal, Fix64 desiredSpeed, LogicFrame now)
    {
        var maxSpeed = GetMaxSpeedForCondition(condition);
        if (desiredSpeed > maxSpeed)
        {
            desiredSpeed = maxSpeed;
        }
        var maxAcceleration = GetMaxAcceleration(condition);

        var rotating = RotateTowardsPosition(phys, condition, goalPos, out var relAngle);
        phys.Turning = rotating;

        // The more we have to turn, the slower we go.
        var angleCoeff = Fix64.Abs(relAngle) / QuarterPi;
        if (angleCoeff > Fix64.One)
        {
            angleCoeff = Fix64.One;
        }

        var dx = phys.Position.X - goalPos.X;
        var dy = phys.Position.Y - goalPos.Y;

        var goalSpeed = (Fix64.One - angleCoeff) * desiredSpeed;

        var actualSpeed = phys.ForwardSpeed2D();
        var slowDownTime = actualSpeed / GetBraking();
        var slowDownDist = actualSpeed / OnePointFive * slowDownTime;

        var twoCells = Fix64.Two * PathfindCellSize;
        if (dx * dx + dy * dy < twoCells * twoCells && angleCoeff > TurnModulationCutoff)
        {
            goalSpeed = actualSpeed * PointSixty;
        }

        if (onPathDistToGoal < slowDownDist &&
            !GetFlag(LocoFlag.IsBraking) &&
            !GetFlag(LocoFlag.NoSlowDownAsApproachingDest))
        {
            SetFlag(LocoFlag.IsBraking, true);
            _brakingFactor = OnePointOne;
        }

        if (onPathDistToGoal > PathfindCellSize && onPathDistToGoal > Fix64.Two * slowDownDist)
        {
            SetFlag(LocoFlag.IsBraking, false);
        }

        var brakingScale = Fix64.One;
        if (GetFlag(LocoFlag.IsBraking))
        {
            _brakingFactor = slowDownDist / onPathDistToGoal;
            _brakingFactor *= _brakingFactor;
            if (_brakingFactor > MaxBrakingFactor)
            {
                _brakingFactor = MaxBrakingFactor;
            }
            if (slowDownDist > onPathDistToGoal)
            {
                goalSpeed = actualSpeed - GetBraking();
                if (goalSpeed < Fix64.Zero)
                {
                    goalSpeed = Fix64.Zero;
                }
            }
            else if (slowDownDist > onPathDistToGoal * PointSeventyFive)
            {
                goalSpeed = actualSpeed - GetBraking() * Fix64.Half;
                if (goalSpeed < Fix64.Zero)
                {
                    goalSpeed = Fix64.Zero;
                }
            }
            else
            {
                goalSpeed = actualSpeed;
            }
            brakingScale = _brakingFactor;
        }

        ApplyGoalSpeedForce(
            phys, goalSpeed, actualSpeed, maxAcceleration, brakingScale,
            phys.UnitDirection2D(), now);
    }

    private void MoveTowardsPositionWheels(
        SimPhysics phys, BodyDamageType condition, in FixVector3 goalPos,
        Fix64 onPathDistToGoal, Fix64 desiredSpeed, LogicFrame now)
    {
        var maxSpeed = GetMaxSpeedForCondition(condition);
        var maxTurnRate = GetMaxTurnRate(condition);
        var maxAcceleration = GetMaxAcceleration(condition);
        if (desiredSpeed > maxSpeed)
        {
            desiredSpeed = maxSpeed;
        }

        var turnSpeed = _template.SimMinTurnSpeed;
        var angle = phys.Yaw;
        var desiredAngle = FixTrig.Atan2(
            goalPos.Y - phys.Position.Y, goalPos.X - phys.Position.X);
        var relAngle = SimAngle.Diff(desiredAngle, angle);

        var moveBackwards = false;

        // Wheeled vehicles can only turn while moving.
        var quarterMax = maxSpeed / Fix64.FromRaw(4L << 32);
        if (turnSpeed < quarterMax)
        {
            turnSpeed = quarterMax;
        }

        var actualSpeed = phys.ForwardSpeed2D();
        var do3PointTurn = false;
        if (actualSpeed == Fix64.Zero)
        {
            SetFlag(LocoFlag.MovingBackwards, false);
            if (_template.CanMoveBackwards && Fix64.Abs(relAngle) > Fix64.PiOver2)
            {
                SetFlag(LocoFlag.MovingBackwards, true);
                SetFlag(LocoFlag.DoingThreePointTurn,
                    onPathDistToGoal > Fix64.FromRaw(5L << 32) * _majorRadius);
            }
        }
        if (GetFlag(LocoFlag.MovingBackwards))
        {
            if (Fix64.Abs(relAngle) < Fix64.PiOver2)
            {
                SetFlag(LocoFlag.MovingBackwards, false);
            }
            else
            {
                moveBackwards = true;
                SetFlag(LocoFlag.DoingThreePointTurn,
                    onPathDistToGoal > Fix64.FromRaw(5L << 32) * _majorRadius);
                do3PointTurn = GetFlag(LocoFlag.DoingThreePointTurn);
                if (!do3PointTurn)
                {
                    desiredAngle = SimAngle.Diff(desiredAngle, Fix64.Pi);
                    relAngle = SimAngle.Diff(desiredAngle, angle);
                }
            }
        }

        if (Fix64.Abs(relAngle) > SmallTurn && desiredSpeed > turnSpeed)
        {
            desiredSpeed = turnSpeed;
        }

        var goalSpeed = desiredSpeed;
        if (moveBackwards)
        {
            actualSpeed = -actualSpeed;
        }

        var slowDownTime = actualSpeed / GetBraking() + Fix64.One;
        var slowDownDist = actualSpeed / OnePointFive * slowDownTime + actualSpeed;
        var effectiveSlowDownDist = slowDownDist;
        if (effectiveSlowDownDist < PathfindCellSize)
        {
            effectiveSlowDownDist = PathfindCellSize;
        }

        // (GPL projects the turn arc ~1/2 s ahead and steers off impassable terrain; the
        // pathfinder query is pinned "always passable" until S5, so that block is inert.)

        if (onPathDistToGoal < effectiveSlowDownDist &&
            !GetFlag(LocoFlag.IsBraking) &&
            !GetFlag(LocoFlag.NoSlowDownAsApproachingDest))
        {
            SetFlag(LocoFlag.IsBraking, true);
            _brakingFactor = OnePointOne;
        }

        if (onPathDistToGoal > PathfindCellSize && onPathDistToGoal > Fix64.Two * slowDownDist)
        {
            SetFlag(LocoFlag.IsBraking, false);
        }

        if (onPathDistToGoal > DonutDistance)
        {
            _donutTimer = now + new LogicFrameSpan(DonutFrames);
        }
        else if (_donutTimer < now)
        {
            SetFlag(LocoFlag.IsBraking, true);
        }

        if (GetFlag(LocoFlag.IsBraking))
        {
            _brakingFactor = slowDownDist / onPathDistToGoal;
            _brakingFactor *= _brakingFactor;
            if (_brakingFactor > MaxBrakingFactor)
            {
                _brakingFactor = MaxBrakingFactor;
            }
            // GPL immediately overrides the factor for wheels:
            _brakingFactor = Fix64.One;
            if (slowDownDist > onPathDistToGoal)
            {
                goalSpeed = actualSpeed - GetBraking();
                if (goalSpeed < Fix64.Zero)
                {
                    goalSpeed = Fix64.Zero;
                }
            }
            else if (slowDownDist > onPathDistToGoal * PointSeventyFive)
            {
                goalSpeed = actualSpeed - GetBraking() * Fix64.Half;
                if (goalSpeed < Fix64.Zero)
                {
                    goalSpeed = Fix64.Zero;
                }
            }
            else
            {
                goalSpeed = actualSpeed;
            }
        }

        // Wheeled turn amount scales with speed.
        var turnFactor = actualSpeed / turnSpeed;
        if (turnFactor < Fix64.Zero)
        {
            turnFactor = -turnFactor;
        }
        if (turnFactor > Fix64.One)
        {
            turnFactor = Fix64.One;
        }
        var turnAmount = turnFactor * maxTurnRate;

        PhysicsTurningType rotating;
        if (moveBackwards && !do3PointTurn)
        {
            var backwardPos = new FixVector3(
                phys.Position.X - (goalPos.X - phys.Position.X),
                phys.Position.Y - (goalPos.Y - phys.Position.Y),
                phys.Position.Z);
            rotating = RotateObjAroundLocoPivot(phys, backwardPos, turnAmount, out _);
        }
        else
        {
            rotating = RotateObjAroundLocoPivot(phys, goalPos, turnAmount, out _);
        }
        phys.Turning = rotating;

        // Maintain goal speed (backwards flips the delta sign and the accel/brake roles).
        var speedDelta = moveBackwards ? actualSpeed - goalSpeed : goalSpeed - actualSpeed;
        if (speedDelta != Fix64.Zero)
        {
            var mass = phys.Mass;
            Fix64 acceleration;
            if (moveBackwards)
            {
                acceleration = speedDelta < Fix64.Zero
                    ? -maxAcceleration
                    : _brakingFactor * GetBraking();
            }
            else
            {
                acceleration = speedDelta > Fix64.Zero
                    ? maxAcceleration
                    : -(_brakingFactor * GetBraking());
            }
            var accelForce = mass * acceleration;
            var maxForceNeeded = mass * speedDelta;
            if (Fix64.Abs(accelForce) > Fix64.Abs(maxForceNeeded))
            {
                accelForce = maxForceNeeded;
            }
            var dir = phys.UnitDirection2D();
            phys.ApplyMotiveForce(
                new FixVector3(accelForce * dir.X, accelForce * dir.Y, Fix64.Zero), now);
        }
    }

    private void MoveTowardsPositionOther(
        SimPhysics phys, BodyDamageType condition, in FixVector3 goalPos,
        Fix64 onPathDistToGoal, Fix64 desiredSpeed, LogicFrame now)
    {
        var maxAcceleration = GetMaxAcceleration(condition);
        var maxSpeed = GetMaxSpeedForCondition(condition);
        if (desiredSpeed > maxSpeed)
        {
            desiredSpeed = maxSpeed;
        }

        var goalSpeed = desiredSpeed;
        var actualSpeed = phys.ForwardSpeed2D();

        var dirToApplyForce = phys.UnitDirection2D();
        var slideThreshold = goalSpeed * _template.SimUltraAccurateSlideIntoPlaceFactor;
        if (GetFlag(LocoFlag.UltraAccurate) &&
            Fix64.Abs(goalPos.Y - phys.Position.Y) <= slideThreshold &&
            Fix64.Abs(goalPos.X - phys.Position.X) <= slideThreshold)
        {
            // Don't turn; just slide in the right direction.
            phys.Turning = PhysicsTurningType.None;
            dirToApplyForce = new FixVector3(
                goalPos.X - phys.Position.X,
                goalPos.Y - phys.Position.Y,
                Fix64.Zero).NormalizedOrZero();
        }
        else
        {
            var rotating = RotateTowardsPosition(phys, condition, goalPos, out _);
            phys.Turning = rotating;
        }

        if (!GetFlag(LocoFlag.NoSlowDownAsApproachingDest))
        {
            var slowDownDist = CalcSlowDownDist(actualSpeed, _template.SimMinSpeed, GetBraking());
            if (onPathDistToGoal < slowDownDist)
            {
                goalSpeed = _template.SimMinSpeed;
            }
        }

        ApplyGoalSpeedForce(
            phys, goalSpeed, actualSpeed, maxAcceleration, Fix64.One, dirToApplyForce, now);
    }

    private void MaintainCurrentPositionHover(
        SimPhysics phys, BodyDamageType condition, LogicFrame now)
    {
        phys.Turning = PhysicsTurningType.None;
        if (!phys.IsMotive(now))
        {
            return;    // no need to stop something that isn't moving
        }

        var maxAcceleration = GetMaxAcceleration(condition);
        var actualSpeed = phys.ForwardSpeed2D();

        // GPL: minSpeed = max(1e-10, template.minSpeed); at Q31.32 the epsilon quantizes
        // to zero, so the stop condition is |speedDelta| > minSpeed with minSpeed >= 0.
        var minSpeed = _template.SimMinSpeed;
        var speedDelta = minSpeed - actualSpeed;
        if (Fix64.Abs(speedDelta) > minSpeed)
        {
            ApplyGoalSpeedForce(
                phys, minSpeed, actualSpeed, maxAcceleration, Fix64.One,
                phys.UnitDirection2D(), now);
        }
    }

    private void MaintainCurrentPositionOther(SimPhysics phys, LogicFrame now)
    {
        phys.Turning = PhysicsTurningType.None;
        if (phys.IsMotive(now))
        {
            phys.ScrubVelocity2D(Fix64.Zero);   // stop
        }
    }

    // ------------------------------------------------------------------ z behavior

    /// <summary>
    /// GPL handleBehaviorZ. Returns true when the behavior needs calling every frame.
    /// <paramref name="surfaceZ"/> is the surface height at the current position.
    /// </summary>
    public bool HandleBehaviorZ(
        SimPhysics phys, BodyDamageType condition, in FixVector3 goalPos, Fix64 surfaceZ,
        LogicFrame now)
    {
        switch (_template.BehaviorZ)
        {
            case LocomotorBehaviorZ.NoZMotiveForce:
                return false;

            case LocomotorBehaviorZ.SeaLevel:
                // Surface height stands in for the water table until a water seam exists.
                phys.Position = new FixVector3(phys.Position.X, phys.Position.Y, surfaceZ);
                return true;

            case LocomotorBehaviorZ.FixedSurfaceRelativeHeight:
                phys.Position = new FixVector3(
                    phys.Position.X, phys.Position.Y, _preferredHeight + surfaceZ);
                return true;

            case LocomotorBehaviorZ.FixedAbsoluteHeight:
                phys.Position = new FixVector3(
                    phys.Position.X, phys.Position.Y, _preferredHeight);
                return true;

            case LocomotorBehaviorZ.SurfaceRelativeHeight:
            case LocomotorBehaviorZ.AbsoluteHeight:
            case LocomotorBehaviorZ.RelativeToGroundAndBuildings:      // degraded: no partition seam yet
            case LocomotorBehaviorZ.RelativeToHighestLayer:            // degraded: no layer seam yet
            {
                if (_preferredHeight == Fix64.Zero && !GetFlag(LocoFlag.PreciseZPos))
                {
                    return true;
                }
                var surfaceRel = _template.BehaviorZ != LocomotorBehaviorZ.AbsoluteHeight;
                var preferredHeight = _preferredHeight + (surfaceRel ? surfaceZ : Fix64.Zero);
                if (GetFlag(LocoFlag.PreciseZPos))
                {
                    preferredHeight = goalPos.Z;
                }

                var delta = (preferredHeight - phys.Position.Z) * _preferredHeightDamping;
                preferredHeight = phys.Position.Z + delta;

                var liftToUse = CalcLiftToUse(
                    phys, condition, phys.Position.Z, preferredHeight);
                if (liftToUse != Fix64.Zero)
                {
                    phys.ApplyMotiveForce(
                        new FixVector3(Fix64.Zero, Fix64.Zero, liftToUse * phys.Mass), now);
                }
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>GPL calcLiftToUseAtPt: solve x = x0 + vt + at^2/2 for the lift accel.</summary>
    internal Fix64 CalcLiftToUse(
        SimPhysics phys, BodyDamageType condition, Fix64 curZ, Fix64 preferredHeight)
    {
        var maxGrossLift = GetMaxLift(condition);
        var maxNetLift = maxGrossLift + phys.Gravity;   // gravity is negative
        if (maxNetLift < Fix64.Zero)
        {
            maxNetLift = Fix64.Zero;
        }
        var curVelZ = phys.Velocity.Z;
        Fix64 maxAccel;
        if (GetFlag(LocoFlag.UltraAccurate))
        {
            maxAccel = curVelZ < Fix64.Zero ? Fix64.Two * maxNetLift : -(Fix64.Two * maxNetLift);
        }
        else
        {
            maxAccel = curVelZ < Fix64.Zero ? maxNetLift : phys.Gravity;
        }

        Fix64 desiredAccel;
        if (Fix64.Abs(maxAccel) > TinyAccel)
        {
            var deltaZ = preferredHeight - curZ;
            var brakeDist = curVelZ * curVelZ / Fix64.Abs(maxAccel);
            if (Fix64.Abs(brakeDist) > Fix64.Abs(deltaZ))
            {
                desiredAccel = maxAccel;
            }
            else if (Fix64.Abs(curVelZ) > _template.SimSpeedLimitZ)
            {
                desiredAccel = _template.SimSpeedLimitZ - curVelZ;
            }
            else
            {
                // a = 2(dz - v) assuming t = 1 frame.
                desiredAccel = Fix64.Two * (deltaZ - curVelZ);
            }
        }
        else
        {
            desiredAccel = Fix64.Zero;
        }

        var liftToUse = desiredAccel - phys.Gravity;
        if (GetFlag(LocoFlag.UltraAccurate))
        {
            var threeGross = Fix64.FromRaw(3L << 32) * maxGrossLift;
            if (liftToUse > threeGross)
            {
                liftToUse = threeGross;
            }
            else if (liftToUse < -maxGrossLift)
            {
                liftToUse = -maxGrossLift;
            }
        }
        else
        {
            if (liftToUse > maxGrossLift)
            {
                liftToUse = maxGrossLift;
            }
            else if (liftToUse < Fix64.Zero)
            {
                liftToUse = Fix64.Zero;
            }
        }
        return liftToUse;
    }

    /// <summary>
    /// GPL setPhysicsOptions: push friction/stick options onto the physics state
    /// (ultra-accurate mode cranks friction for precision).
    /// </summary>
    public void SetPhysicsOptions(SimPhysics phys)
    {
        var extra = GetFlag(LocoFlag.UltraAccurate) ? UltraAccurateExtraFriction : Fix64.Zero;
        phys.ExtraFriction = _template.SimExtra2DFriction + extra;
        phys.AllowAirborneFriction = _template.Apply2DFrictionWhenAirborne;
        phys.StickToGround = _template.StickToGround;
    }

    // ---- the single walk (F8 Objects channel; field order = declaration order, F9) ----
    public void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferFrame("DonutTimer", ref _donutTimer);
        xfer.XferFixVector3("MaintainPos", ref _maintainPos, Tolerance.Band);
        xfer.XferFix64("BrakingFactor", ref _brakingFactor, Tolerance.Band);
        xfer.XferFix64("MaxLift", ref _maxLift);
        xfer.XferFix64("MaxSpeed", ref _maxSpeed);
        xfer.XferFix64("MaxAccel", ref _maxAccel);
        xfer.XferFix64("MaxBraking", ref _maxBraking);
        xfer.XferFix64("MaxTurnRate", ref _maxTurnRate);
        xfer.XferFix64("CloseEnoughDist", ref _closeEnoughDist);
        xfer.XferUInt("Flags", ref _flags);
        xfer.XferFix64("PreferredHeight", ref _preferredHeight);
        xfer.XferFix64("PreferredHeightDamping", ref _preferredHeightDamping);
        xfer.XferFix64("AngleOffset", ref _angleOffset, Tolerance.Band);
        xfer.XferFix64("OffsetIncrement", ref _offsetIncrement, Tolerance.Band);
        xfer.XferFix64("BoundingCircleRadius", ref _boundingCircleRadius);
        xfer.XferFix64("MajorRadius", ref _majorRadius);
    }
}
