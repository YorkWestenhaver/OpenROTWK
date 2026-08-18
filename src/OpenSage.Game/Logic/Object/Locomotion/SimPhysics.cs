// SimPhysics - the deterministic per-object movement integrator of the S2 locomotor
// system. Fresh code; behavioral reference (semantics only): generals-gpl GeneralsMD
// GameLogic/Object/Update/PhysicsUpdate.cpp (PhysicsBehavior). Everything here is Fix64;
// the position and heading held by this object are SIM-AUTHORITATIVE for a moving object
// (the GameObject float transform becomes a display mirror pushed through
// SimTransformBridge each frame).
//
// THE INTEGRATION ORDER (conformance-critical; GPL PhysicsBehavior::update()):
//   1. prevAccel := accel                       (snapshot of last frame's applied accel)
//   2. accel.z += gravity                       (applyGravitationalForces)
//   3. friction                                 (applyFrictionalForces):
//        grounded (or ApplyFrictionWhenAirborne): kill LATERAL velocity component with
//          lateralFriction; when NOT motive also kill FORWARD component with
//          forwardFriction; both routed through ApplyForce (mass cancels: a = -f*v).
//        airborne: accel += vel * (-aeroFriction) componentwise, all three axes.
//   4. vel += accel                             (semi-implicit Euler, accel first)
//   5. per-axis tiny clamp: |vel.c| < 0.001 -> 0
//   6. position += vel                          - UNLESS the locomotor set the braking
//        status this frame, in which case only z integrates and the braking cheat in
//        SimLocomotor.MoveTowardsPosition has already advanced x/y exactly.
//   7. ground clamp: if newZ <= groundZ: vel.z += (groundZ - newZ), then vel.z = min(vel.z, 0),
//        z := groundZ. Else if stick-to-ground: z := groundZ (no vel change).
//   8. accel := 0                               (accumulator reset for next frame)
// Steps not carried over from GPL (out of scope this round, see the design note):
// pitch/roll/yaw spring dynamics (client-visual), bounce forces + falling damage (needs
// S1 damage pipeline), stun handling, overlap bookkeeping, kill-when-resting.
//
// "Motive" (GPL m_motiveForceExpires): an object counts as driven by its locomotor for
// MotiveFrames after the last ApplyMotiveForce; while motive, externally applied forces
// are filtered to their lateral component only (applyForce's isMotive branch), and
// forward friction is suppressed.

using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object.Locomotion;

[SimState]
public sealed class SimPhysics
{
    // GPL MOTIVE_FRAMES = LOGICFRAMES_PER_SECOND / 3, i.e. one third of a second.
    // At the frozen 5 Hz that is ceil(5/3) = 2 frames (rounding pinned: ceil, so the
    // motive window never collapses to zero).
    internal const uint MotiveFrames = 2;

    private static readonly Fix64 TinyVelocity = Fix64.FromDecimalLiteral("0.001");
    private static readonly Fix64 MaxFriction = Fix64.FromDecimalLiteral("0.99");
    private static readonly Fix64 MinNonAeroFriction = Fix64.FromDecimalLiteral("0.01");

    private readonly SimLocomotorUpdateModuleData _data;

    // ---- mutable sim state (every field appears in Xfer exactly once) ----
    private FixVector3 _position;
    private Fix64 _yaw;
    private FixVector3 _accel;
    private FixVector3 _prevAccel;
    private FixVector3 _vel;
    private LogicFrame _motiveForceExpires;
    private Fix64 _extraFriction;
    private PhysicsTurningType _turning;
    private bool _isBraking;              // mirror of GPL OBJECT_STATUS_BRAKING, set by the locomotor
    private bool _stickToGround;
    private bool _allowAirborneFriction;  // GPL APPLY_FRICTION2D_WHEN_AIRBORNE

    public SimPhysics(SimLocomotorUpdateModuleData data)
    {
        _data = data;
    }

    public FixVector3 Position { get => _position; internal set => _position = value; }
    public Fix64 Yaw { get => _yaw; internal set => _yaw = value; }
    public FixVector3 Velocity => _vel;
    public FixVector3 PreviousAcceleration => _prevAccel;
    public PhysicsTurningType Turning { get => _turning; internal set => _turning = value; }
    public bool IsBraking { get => _isBraking; internal set => _isBraking = value; }
    public bool StickToGround { get => _stickToGround; internal set => _stickToGround = value; }
    public bool AllowAirborneFriction { get => _allowAirborneFriction; internal set => _allowAirborneFriction = value; }
    public Fix64 ExtraFriction { get => _extraFriction; internal set => _extraFriction = value; }

    public Fix64 Mass => _data.Mass;
    public Fix64 Gravity => _data.GravityPerFrame;

    /// <summary>Unit 2D heading vector (cos yaw, sin yaw, 0) from the LUT (F2).</summary>
    public FixVector3 UnitDirection2D() =>
        new(FixTrig.Cos(_yaw), FixTrig.Sin(_yaw), Fix64.Zero);

    public bool IsMotive(LogicFrame now) => _motiveForceExpires > now;

    /// <summary>|velocity| in 3D (GPL getVelocityMagnitude, uncached - a pure function here).</summary>
    public Fix64 VelocityMagnitude() => _vel.Length();

    /// <summary>
    /// Signed forward speed, GPL getForwardSpeed2D's EXACT (odd) formula: with
    /// vx = vel.x*dir.x, vy = vel.y*dir.y, the result is sign(vx+vy) * sqrt(vx^2+vy^2) -
    /// NOT the plain projection. Reproduced verbatim because callers were tuned to it.
    /// </summary>
    public Fix64 ForwardSpeed2D(in FixVector3 dir)
    {
        var vx = _vel.X * dir.X;
        var vy = _vel.Y * dir.Y;
        var dot = vx + vy;
        var speed = Fix64.Sqrt(vx * vx + vy * vy);
        return dot >= Fix64.Zero ? speed : -speed;
    }

    public Fix64 ForwardSpeed2D() => ForwardSpeed2D(UnitDirection2D());

    /// <summary>GPL applyForce: F/m accumulated; while motive only the lateral component lands.</summary>
    public void ApplyForce(in FixVector3 force, LogicFrame now)
    {
        var modForce = force;
        if (IsMotive(now))
        {
            var dir = UnitDirection2D();
            // Only accept the lateral component: project onto the left normal (-dir.y, dir.x).
            var lateralDot = force.X * -dir.Y + force.Y * dir.X;
            modForce = new FixVector3(lateralDot * -dir.Y, lateralDot * dir.X, force.Z);
        }

        var mass = Mass;
        _accel = new FixVector3(
            _accel.X + modForce.X / mass,
            _accel.Y + modForce.Y / mass,
            _accel.Z + modForce.Z / mass);
    }

    /// <summary>GPL applyMotiveForce: accepted unquestioningly, then re-arms the motive window.</summary>
    public void ApplyMotiveForce(in FixVector3 force, LogicFrame now)
    {
        _motiveForceExpires = new LogicFrame(0);
        ApplyForce(force, now);
        _motiveForceExpires = now + new LogicFrameSpan(MotiveFrames);
    }

    /// <summary>GPL scrubVelocity2D: cap the 2D speed at <paramref name="desiredVelocity"/>.</summary>
    public void ScrubVelocity2D(Fix64 desiredVelocity)
    {
        if (desiredVelocity < TinyVelocity)
        {
            _vel = new FixVector3(Fix64.Zero, Fix64.Zero, _vel.Z);
            return;
        }
        var cur = Fix64.Sqrt(_vel.X * _vel.X + _vel.Y * _vel.Y);
        if (desiredVelocity > cur)
        {
            return;
        }
        var scale = desiredVelocity / cur;
        _vel = new FixVector3(_vel.X * scale, _vel.Y * scale, _vel.Z);
    }

    private Fix64 ClampFriction(Fix64 f)
    {
        if (f < MinNonAeroFriction)
        {
            f = MinNonAeroFriction;
        }
        if (f > MaxFriction)
        {
            f = MaxFriction;
        }
        return f;
    }

    internal Fix64 LateralFriction => ClampFriction(_data.LateralFriction + _extraFriction);
    internal Fix64 ForwardFriction => ClampFriction(_data.ForwardFriction + _extraFriction);

    internal Fix64 AerodynamicFriction
    {
        get
        {
            // MIN_AERO_FRICTION = 0 (aero may legitimately be zero).
            var f = _data.AerodynamicFriction + _extraFriction;
            if (f < Fix64.Zero)
            {
                f = Fix64.Zero;
            }
            if (f > MaxFriction)
            {
                f = MaxFriction;
            }
            return f;
        }
    }

    /// <summary>The per-frame integration; see the file header for the exact order.</summary>
    public void Integrate(LogicFrame now, Fix64 groundZ, bool airborne)
    {
        _prevAccel = _accel;

        // 2. gravity
        _accel = new FixVector3(_accel.X, _accel.Y, _accel.Z + Gravity);

        // 3. friction
        if (_allowAirborneFriction || !airborne)
        {
            if (_vel.X != Fix64.Zero || _vel.Y != Fix64.Zero)
            {
                var dir = UnitDirection2D();
                var mass = Mass;

                var lateralDot = _vel.X * -dir.Y + _vel.Y * dir.X;
                var lateralVelX = lateralDot * -dir.Y;
                var lateralVelY = lateralDot * dir.X;
                var lf = mass * LateralFriction;

                var fricX = -(lf * lateralVelX);
                var fricY = -(lf * lateralVelY);

                if (!IsMotive(now))
                {
                    var forwardDot = _vel.X * dir.X + _vel.Y * dir.Y;
                    var ff = mass * ForwardFriction;
                    fricX += -(ff * (forwardDot * dir.X));
                    fricY += -(ff * (forwardDot * dir.Y));
                }

                ApplyForce(new FixVector3(fricX, fricY, Fix64.Zero), now);
            }
        }
        else
        {
            var aero = -AerodynamicFriction;
            _accel = new FixVector3(
                _accel.X + _vel.X * aero,
                _accel.Y + _vel.Y * aero,
                _accel.Z + _vel.Z * aero);
        }

        // 4. integrate acceleration into velocity
        _vel += _accel;

        // 5. tiny clamp, per axis
        var vx = Fix64.Abs(_vel.X) < TinyVelocity ? Fix64.Zero : _vel.X;
        var vy = Fix64.Abs(_vel.Y) < TinyVelocity ? Fix64.Zero : _vel.Y;
        var vz = Fix64.Abs(_vel.Z) < TinyVelocity ? Fix64.Zero : _vel.Z;
        _vel = new FixVector3(vx, vy, vz);

        // 6. integrate velocity into position (braking cheat owns x/y when braking)
        var newPos = _isBraking
            ? new FixVector3(_position.X, _position.Y, _position.Z + _vel.Z)
            : _position + _vel;

        // 7. ground clamp
        if (newPos.Z <= groundZ)
        {
            var dz = groundZ - newPos.Z;
            var newVz = _vel.Z + dz;
            if (newVz > Fix64.Zero)
            {
                newVz = Fix64.Zero;
            }
            _vel = new FixVector3(_vel.X, _vel.Y, newVz);
            newPos = new FixVector3(newPos.X, newPos.Y, groundZ);
        }
        else if (_stickToGround)
        {
            newPos = new FixVector3(newPos.X, newPos.Y, groundZ);
        }
        _position = newPos;

        // 8. reset the accumulator
        _accel = FixVector3.Zero;
    }

    /// <summary>True when the integrator has nothing left to do (sleep gate).</summary>
    public bool IsAtRest(LogicFrame now) =>
        _vel == FixVector3.Zero && _accel == FixVector3.Zero && !IsMotive(now);

    // ---- the single walk (F8 Objects channel; field order = declaration order, F9) ----
    public void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferFixVector3("Position", ref _position, Tolerance.Band);
        xfer.XferFix64("Yaw", ref _yaw, Tolerance.Band);
        xfer.XferFixVector3("Accel", ref _accel, Tolerance.Band);
        xfer.XferFixVector3("PrevAccel", ref _prevAccel, Tolerance.Band);
        xfer.XferFixVector3("Vel", ref _vel, Tolerance.Band);
        xfer.XferFrame("MotiveForceExpires", ref _motiveForceExpires);
        xfer.XferFix64("ExtraFriction", ref _extraFriction);
        xfer.XferEnum("Turning", ref _turning);
        xfer.XferBool("IsBraking", ref _isBraking);
        xfer.XferBool("StickToGround", ref _stickToGround);
        xfer.XferBool("AllowAirborneFriction", ref _allowAirborneFriction);
    }
}
