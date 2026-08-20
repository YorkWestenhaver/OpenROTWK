// NeutronMissileUpdate - R12 port (task packet neutron-missile-update). Behavioral
// reference (semantics only): generals-gpl GeneralsMD GameLogic/Module/NeutronMissileUpdate.h/
// .cpp (class NeutronMissileUpdate / NeutronMissileUpdateModuleData). Legacy (pre-SimCore)
// runtime module: it drives Drawable bone transforms (launch-bone attach, per-frame
// instance-matrix jitter), FX execution and RadiusDecal state, none of which have a Fix64
// counterpart yet (Update/AIUpdate is not in SimCoreScopedDirs.txt; see TurretAIUpdate's
// header for the same call), so this file stays on the float/IGameEngine substrate like
// MissileAIUpdate, BezierProjectileBehavior and TurretAIUpdate before it.
//
// Four-phase flight state machine (GPL MissileStateType, faithfully translated):
//   PreLaunch (inert; nothing to do) --ProjectileFireAtObjectOrPosition-->
//   Launch (doLaunch: on the first tick, snap to the launcher's weapon-launch bone plus the
//     GPL "missile on its raising launch platform" 90-degree local-X correction, unhide,
//     capture the launcher's velocity, arm the warhead, play LaunchFX; every tick, fall by
//     the captured velocity and play IgnitionFX) --always, same tick--> Attack (doAttack:
//     steer toward the target via calcTransform's axis-angle turn while noTurnDistLeft has
//     not been used up, forward-damped accel/vel integration, optional
//     TargetFromDirectlyAbove intermediate-position approach, optional special-acceleration
//     launch phase with lateral jitter) --collision/ground hit--> Dead (detonate: kill(),
//     clear the delivery decal, hide the object).
//
// Not reproduced (behavior-fact gaps, filed not invented):
//   - Exhaust particle-system attach (GPL's exhaustSysOverride ctor parameter, itself not an
//     INI field) has no OpenSage seam: TheParticleSystemManager->createAttachedParticleSystemID
//     has no attach-to-object call reachable from a plain UpdateModule.
//   - RadiusDecal rendering: OpenSage's RadiusDecalTemplate has no live "instance" API yet
//     (parse-only visuals). The module still tracks the delivery decal's active/cleared state
//     and position/radius faithfully (a real, test-visible part of the state machine) but does
//     not hand it to a renderer.
//   - ProjectileUpdateInterface / DieModuleInterface are GPL interface seams with no OpenSage
//     equivalent surface to implement against yet; the entry points they declare
//     (projectileLaunchAtObjectOrPosition, projectileFireAtObjectOrPosition,
//     projectileHandleCollision) are exposed here as plain public methods with matching
//     names/semantics so a future weapon-system/collision seam can call straight in.
//   - calcTransform's axis-angle rotation degenerates when the current and desired headings
//     are exactly parallel or anti-parallel (Cross Product is zero); GPL leaves this
//     numerically undefined (W3D's Normalize would itself produce a degenerate result). This
//     port adds an explicit fallback axis (world +Z) purely to stay deterministic - not an
//     invented behavior change, just a defined tie-break for an input GPL never resolves.

using System;
using System.Numerics;
using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.FX;
using OpenSage.Gui.InGame;
using OpenSage.Mathematics;

namespace OpenSage.Logic.Object;

public sealed class NeutronMissileUpdate : UpdateModule
{
    private const float StraightDownSlowFactor = 0.5f;

    private readonly NeutronMissileUpdateModuleData _moduleData;

    public enum MissileState
    {
        PreLaunch,
        Launch,
        Attack,
        Dead,
    }

    // ---- mutable state (the whole inventory; matches GPL's per-instance fields) ----
    private MissileState _state;
    private Vector3 _targetPos;
    private Vector3 _intermedPos;

    private ObjectId _launcherId;
    private WeaponSlot _attachWeaponSlot = WeaponSlot.Primary;
    private int _attachBarrelIndex;

    private Vector3 _accel;
    private Vector3 _vel;

    private LogicFrame _stateTimestamp;
    private bool _isLaunched;
    private bool _isArmed;
    private float _noTurnDistLeft;
    private bool _reachedIntermediatePos = true;
    private LogicFrame _frameAtLaunch;
    private float _heightAtLaunch;

    // Delivery-decal life-cycle state (see header: no renderer seam yet).
    private bool _deliveryDecalActive;
    private Vector3 _deliveryDecalPosition;
    private float _deliveryDecalRadius;

    internal NeutronMissileUpdate(GameObject gameObject, IGameEngine gameEngine, NeutronMissileUpdateModuleData moduleData)
        : base(gameObject, gameEngine)
    {
        _moduleData = moduleData;
        _noTurnDistLeft = moduleData.DistanceToTravelBeforeTurning;
        _state = MissileState.PreLaunch;
        _stateTimestamp = GameEngine.GameLogic.CurrentFrame;
    }

    /// <summary>Test/inspector-only view of the state machine; not part of the save contract.</summary>
    internal MissileState State => _state;
    internal Vector3 Velocity => _vel;
    internal bool IsArmed => _isArmed;
    internal bool ReachedIntermediatePosition => _reachedIntermediatePos;
    internal bool DeliveryDecalActive => _deliveryDecalActive;
    internal ObjectId LauncherId => _launcherId;

    // ------------------------------------------------------------ ProjectileUpdateInterface seam

    public ObjectId ProjectileGetLauncherId() => _launcherId;

    public Vector3 GetVelocity() => _vel;

    public void ProjectileLaunchAtObjectOrPosition(
        GameObject victim,
        in Vector3? victimPos,
        GameObject launcher,
        WeaponSlot wslot,
        int specificBarrelToUse)
    {
        _launcherId = launcher != null ? launcher.Id : ObjectId.Invalid;
        _attachWeaponSlot = wslot;
        _attachBarrelIndex = specificBarrelToUse;

        _vel = Vector3.Zero;
        if (launcher != null)
        {
            var launcherPhysics = launcher.FindBehavior<PhysicsBehavior>();
            if (launcherPhysics != null)
            {
                _vel = launcherPhysics.Velocity;
            }
        }

        ProjectileFireAtObjectOrPosition(victim, victimPos);
    }

    public void ProjectileFireAtObjectOrPosition(GameObject victim, in Vector3? victimPos)
    {
        _state = MissileState.Launch;
        _stateTimestamp = GameEngine.GameLogic.CurrentFrame;

        // CalcTarget would add half the target's height, but here we are aiming at the
        // ground and need to stay aiming at the ground (GPL comment, kept verbatim).
        var basePos = victim != null ? victim.Translation : victimPos.GetValueOrDefault();
        _targetPos = basePos;
        _intermedPos = new Vector3(basePos.X, basePos.Y, basePos.Z + _moduleData.TargetFromDirectlyAbove);

        _deliveryDecalActive = true;
        _deliveryDecalPosition = _targetPos;
        _deliveryDecalRadius = _moduleData.DeliveryDecalRadius;
    }

    /// <summary>GPL projectileHandleCollision: detonates an armed missile on contact.</summary>
    public bool HandleCollision(GameObject other)
    {
        // Check if our warhead is "armed" - if not, we are inert.
        if (!_isArmed)
        {
            return true;
        }

        // Don't hit your own launcher, ever.
        if (other != null && _launcherId == other.Id)
        {
            return true;
        }

        // Collided with something... blow'd up!
        Detonate();

        // Mark ourself as "no collisions" (since we might still exist in slow-death mode).
        GameObject.SetObjectStatus(ObjectStatus.NoCollisions, true);
        return true;
    }

    private void Detonate()
    {
        _deliveryDecalActive = false;
        GameObject.Kill(deathType: DeathType.Detonated);
        _state = MissileState.Dead;
        GameObject.Hidden = true;
    }

    // -------------------------------------------------------------------------------- update

    public override UpdateSleepTime Update()
    {
        // GPL: m_deliveryDecal.update() - no live renderer to tick yet (see header).

        if (!_reachedIntermediatePos)
        {
            var distSqr = Vector3.DistanceSquared(GameObject.Translation, _intermedPos);
            var boundSqr = GameObject.Geometry.BoundingSphereRadius * GameObject.Geometry.BoundingSphereRadius;
            if (distSqr <= boundSqr)
            {
                _reachedIntermediatePos = true;
                GameObject.SetTranslation(_intermedPos);
                var velLength = _vel.Length();
                _vel = new Vector3(0f, 0f, -velLength * StraightDownSlowFactor);
            }
        }

        var oldPos = GameObject.Translation;
        var oldPosValid = _state == MissileState.Attack; // not valid till *after* we've launched

        switch (_state)
        {
            case MissileState.PreLaunch:
                // nothing... just ignore it.
                break;

            case MissileState.Launch:
                DoLaunch();
                break;

            case MissileState.Attack:
                DoAttack();
                break;

            case MissileState.Dead:
                // do nothing
                break;
        }

        if (_noTurnDistLeft > 0f && oldPosValid)
        {
            var newPos = GameObject.Translation;
            var distThisTurn = Vector3.Distance(oldPos, newPos);
            _noTurnDistLeft -= distThisTurn;
        }

        // Gated on oldPosValid (state was already Attack going INTO this Update), not the
        // post-switch state: the frame doLaunch() first lands us at the launch bone's
        // position is not itself a ground hit - the missile hasn't started flying yet, it has
        // only just been placed there (matches the existing oldPosValid gate just above, which
        // draws the same before/after-launch distinction for the no-turn distance budget).
        //
        // Height check is strictly-below (< 0), not GameObject.IsAboveTerrain's own boundary
        // (height > 0, i.e. its negation triggers at height == 0 too): a missile skimming
        // exactly along the ground plane - e.g. a level, non-TargetFromDirectlyAbove flight
        // path with no vertical component at all - has not collided with anything, only one
        // that has actually dipped below terrain has.
        if (oldPosValid && GameObject.HeightAboveTerrain < 0f)
        {
            // The normal always points straight down (GPL comment, kept verbatim).
            HandleCollision(null);
        }

        // TODO(Port): Use correct value.
        return UpdateSleepTime.None;
    }

    /// <summary>Implement LAUNCH state (GPL doLaunch).</summary>
    private void DoLaunch()
    {
        var data = _moduleData;

        if (!_isLaunched)
        {
            var launcher = GameEngine.GameLogic.GetObjectById(_launcherId);

            // If our launch vehicle is gone, destroy ourselves.
            if (launcher == null)
            {
                _launcherId = ObjectId.Invalid;
                GameEngine.GameLogic.DestroyObject(GameObject);
                return;
            }

            var launchBoneTransform = launcher.Drawable?.GetWeaponLaunchBoneTransform(_attachWeaponSlot, _attachBarrelIndex);
            var attachTransform = launchBoneTransform ?? Matrix4x4.Identity;

            var worldPos = attachTransform.Translation;
            Matrix4x4.Decompose(attachTransform, out _, out var boneRotation, out _);

            // The missile on the raising-up launch platform is actually 45 degrees from the
            // missile that is flying around the world; rotate it "on end and in place" so we
            // don't see any decal 'pop' to the new angle (GPL comment, kept verbatim). This is
            // a local-frame rotation around the bone's own X axis, so it right-multiplies.
            var worldRotation = Quaternion.Normalize(
                boneRotation * Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f));

            GameObject.Hidden = false;
            GameObject.SetRotation(worldRotation);
            GameObject.SetTranslation(worldPos);

            GameObject.ExperienceTracker.ExperienceSink = _launcherId;

            _isLaunched = true;

            if (data.TargetFromDirectlyAbove != 0f)
            {
                _reachedIntermediatePos = false;
            }

            data.LaunchFX?.Value?.Execute(new FXListExecutionContext(GameObject.Rotation, GameObject.Translation, GameEngine));
            _heightAtLaunch = GameObject.Translation.Z;
            _frameAtLaunch = GameEngine.GameLogic.CurrentFrame;
        }

        // fall
        GameObject.SetTranslation(GameObject.Translation + _vel);

        data.IgnitionFX?.Value?.Execute(new FXListExecutionContext(GameObject.Rotation, GameObject.Translation, GameEngine));

        _state = MissileState.Attack;
        _stateTimestamp = GameEngine.GameLogic.CurrentFrame;

        // arm the missile's "warhead"
        _isArmed = true;
    }

    /// <summary>
    /// GPL calcTransform: turns as much as maxTurnRate allows toward <paramref name="targetPos"/>
    /// and returns the resulting new forward-facing rotation.
    /// </summary>
    private static void CalcTransform(GameObject obj, in Vector3 targetPos, float maxTurnRate, out Quaternion newRotation)
    {
        var objPos = obj.Translation;
        var objDir = obj.LookDirection;

        var toTarget = targetPos - objPos;
        var otherDir = toTarget != Vector3.Zero ? Vector3.Normalize(toTarget) : objDir;

        var c = Vector3.Dot(objDir, otherDir);
        c = Math.Clamp(c, -1f, 1f);
        var angle = MathF.Acos(c);

        Vector3 newDir;
        if (MathF.Abs(angle) < maxTurnRate)
        {
            // close enough -- point exactly in the right dir
            newDir = otherDir;
        }
        else
        {
            // turn as much as we can, around the axis perpendicular to both vectors
            angle = maxTurnRate;

            var axis = Vector3.Cross(objDir, otherDir);
            axis = axis.LengthSquared() > 1e-12f ? Vector3.Normalize(axis) : Vector3.UnitZ;

            var rot = Matrix4x4.CreateFromAxisAngle(axis, angle);
            newDir = Vector3.Normalize(Vector3.TransformNormal(objDir, rot));
        }

        newRotation = QuaternionUtility.CreateRotation(Vector3.UnitX, newDir);
    }

    /// <summary>Implement ATTACK state (GPL doAttack).</summary>
    private void DoAttack()
    {
        var data = _moduleData;
        var speed = data.RelativeSpeed;

        if (data.TargetFromDirectlyAbove != 0f && _reachedIntermediatePos)
        {
            speed *= StraightDownSlowFactor;
        }

        Quaternion newRotation;
        if (_noTurnDistLeft > 0f)
        {
            // still in the no-turning-time: keep the current orientation.
            newRotation = GameObject.Rotation;
        }
        else
        {
            var aimPos = _reachedIntermediatePos ? _targetPos : _intermedPos;
            CalcTransform(GameObject, aimPos, data.MaxTurnRate, out newRotation);
        }

        // get true forward direction of missile
        var trueDir = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, newRotation));

        // Move forward along forward direction
        var damping = data.ForwardDamping;
        _accel = new Vector3(
            speed * trueDir.X - damping * _vel.X,
            speed * trueDir.Y - damping * _vel.Y,
            speed * trueDir.Z - damping * _vel.Z);

        _vel += _accel;

        var pos = GameObject.Translation;
        var now = GameEngine.GameLogic.CurrentFrame;

        if (data.SpecialSpeedTime.Value > 0 && now <= _frameAtLaunch + data.SpecialSpeedTime)
        {
            GameObject.Drawable.InstanceMatrix = Matrix4x4.Identity;

            var elapsed = (now - _frameAtLaunch).Value;
            if (elapsed < data.SpecialSpeedTime.Value)
            {
                var timeFrac = (float)elapsed / data.SpecialSpeedTime.Value;
                var accelFactor = data.SpecialAccelFactor;
                if (accelFactor < 0.01f)
                {
                    accelFactor = 0.01f;
                }

                var newPos = pos;
                var scaled = accelFactor * timeFrac;
                newPos.Z = _heightAtLaunch + (scaled * scaled / accelFactor) * data.SpecialSpeedHeight;

                _vel = newPos - pos;

                if (data.SpecialJitterDistance > 0f)
                {
                    var amplitude = (1f - timeFrac) * data.SpecialJitterDistance;
                    var jitterLocal = new Vector3(
                        0f,
                        GameEngine.GameLogic.Random.NextSingle(-1f, 1f) * amplitude,
                        GameEngine.GameLogic.Random.NextSingle(-1f, 1f) * amplitude);
                    var jitterWorld = Vector3.Transform(jitterLocal, newRotation);
                    GameObject.Drawable.InstanceMatrix = Matrix4x4.CreateTranslation(jitterWorld);
                }
            }
        }

        pos += _vel;

        GameObject.SetRotation(newRotation);
        GameObject.SetTranslation(pos);
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        reader.PersistEnum(ref _state);
        reader.PersistVector3(ref _targetPos);
        reader.PersistVector3(ref _intermedPos);
        reader.PersistObjectId(ref _launcherId);
        reader.PersistEnum(ref _attachWeaponSlot);
        reader.PersistInt32(ref _attachBarrelIndex);
        reader.PersistVector3(ref _accel);
        reader.PersistVector3(ref _vel);

        var stateTimestamp = _stateTimestamp.Value;
        reader.PersistFrame(ref stateTimestamp);
        _stateTimestamp = new LogicFrame(stateTimestamp);

        reader.PersistBoolean(ref _isLaunched);
        reader.PersistBoolean(ref _isArmed);
        reader.PersistSingle(ref _noTurnDistLeft);
        reader.PersistBoolean(ref _reachedIntermediatePos);

        var frameAtLaunch = _frameAtLaunch.Value;
        reader.PersistFrame(ref frameAtLaunch);
        _frameAtLaunch = new LogicFrame(frameAtLaunch);

        reader.PersistSingle(ref _heightAtLaunch);

        reader.PersistBoolean(ref _deliveryDecalActive);
        reader.PersistVector3(ref _deliveryDecalPosition);
        reader.PersistSingle(ref _deliveryDecalRadius);
    }
}

// ============================================================================
// PARSE SIDE
// ============================================================================
public sealed class NeutronMissileUpdateModuleData : UpdateModuleData
{
    internal static NeutronMissileUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<NeutronMissileUpdateModuleData> FieldParseTable = new IniParseTable<NeutronMissileUpdateModuleData>
    {
        { "DistanceToTravelBeforeTurning", (parser, x) => x.DistanceToTravelBeforeTurning = parser.ParseFloat() },
        { "MaxTurnRate", (parser, x) => x.MaxTurnRate = parser.ParseAngularVelocityToLogicFrames() },
        { "ForwardDamping", (parser, x) => x.ForwardDamping = parser.ParseFloat() },
        { "RelativeSpeed", (parser, x) => x.RelativeSpeed = parser.ParseFloat() },
        { "LaunchFX", (parser, x) => x.LaunchFX = parser.ParseFXListReference() },
        { "IgnitionFX", (parser, x) => x.IgnitionFX = parser.ParseFXListReference() },
        { "TargetFromDirectlyAbove", (parser, x) => x.TargetFromDirectlyAbove = parser.ParseFloat() },
        { "SpecialAccelFactor", (parser, x) => x.SpecialAccelFactor = parser.ParseFloat() },
        { "SpecialSpeedTime", (parser, x) => x.SpecialSpeedTime = parser.ParseTimeMillisecondsToLogicFrames() },
        { "SpecialSpeedHeight", (parser, x) => x.SpecialSpeedHeight = parser.ParseFloat() },
        { "SpecialJitterDistance", (parser, x) => x.SpecialJitterDistance = parser.ParseFloat() },
        { "DeliveryDecalRadius", (parser, x) => x.DeliveryDecalRadius = parser.ParseFloat() },
        { "DeliveryDecal", (parser, x) => x.DeliveryDecal = RadiusDecalTemplate.Parse(parser) },
    };

    public float DistanceToTravelBeforeTurning { get; private set; }

    /// <summary>Radians per logic frame. GPL default: 999 degrees/second (effectively unlimited).</summary>
    public float MaxTurnRate { get; private set; } = 999f * MathUtility.DegreesToRadiansRatio / 30f;

    public float ForwardDamping { get; private set; }
    public float RelativeSpeed { get; private set; } = 1.0f;
    public LazyAssetReference<FXList> LaunchFX { get; private set; }
    public LazyAssetReference<FXList> IgnitionFX { get; private set; }
    public float TargetFromDirectlyAbove { get; private set; }
    public float SpecialAccelFactor { get; private set; } = 1.0f;
    public LogicFrameSpan SpecialSpeedTime { get; private set; }
    public float SpecialSpeedHeight { get; private set; }
    public float SpecialJitterDistance { get; private set; }
    public float DeliveryDecalRadius { get; private set; }
    public RadiusDecalTemplate DeliveryDecal { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new NeutronMissileUpdate(gameObject, gameEngine, this);
    }
}
