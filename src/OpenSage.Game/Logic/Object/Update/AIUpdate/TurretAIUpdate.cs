// TurretAIUpdate - R12 port (Round-4 backlog; census: Update). Legacy (pre-SimCore) runtime
// module: it is owned directly by AIUpdate (moduleData.Turret / AltTurret), not created
// through the generic ModuleData.CreateModule dispatch, so it lives on the float/IGameEngine
// substrate like the rest of the legacy Weapon/AIUpdate machinery it reads
// (GameObject.CurrentWeapon, WeaponTarget, ModelConditionFlags) - this directory has not
// migrated into the SimCore Fix64 quarantine (SimCoreScopedDirs.txt has no Update/AIUpdate
// entry yet), so float math here is in policy, matching every other file it touches.
//
// State machine (api-freeze-v1 grep target - the [ParseOnly] deletion below is the actual
// porting deliverable): Disabled -> Idle (stalled while InitiallyDisabled) -> ScanningForTargets
// (idle-scan timer) -> Turning (target acquired; FiresWhileTurning gates the Attacking model
// condition while turning) -> Attacking (rotation complete) -> Recentering (target lost, or the
// object started moving - either way RecenterTime frames are waited before rotating back to
// NaturalTurretAngle) -> Idle. FoundTargetWhileScanning stays a false-returning stub: the
// idle-scan target search needs a scene/quadtree query seam this legacy AIUpdate surface does
// not have wired (its GPL-faithful body is left commented as a filed TODO, not deleted).

using System;
using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.Mathematics;
using OpenSage.Utilities;

namespace OpenSage.Logic.Object;

public class TurretAIUpdate : UpdateModule
{
    private readonly TurretAIUpdateModuleData _moduleData;

    private WeaponTarget _currentTarget;
    private LogicFrame _waitUntil;
    private TurretAIStates _turretAIstate;

    public enum TurretAIStates
    {
        Disabled,
        Idle,
        ScanningForTargets,
        Turning,
        Attacking,
        Recentering
    }

    /// <summary>Test/inspector-only view of the state machine (backed by <see cref="_turretAIstate"/>, which is persisted -- see <see cref="Load"/>).</summary>
    internal TurretAIStates State => _turretAIstate;

    /// <summary>Test/inspector-only view of the pending wake frame (backed by <see cref="_waitUntil"/>, which is persisted -- see <see cref="Load"/>).</summary>
    internal LogicFrame WaitUntil => _waitUntil;

    internal TurretAIUpdate(GameObject gameObject, IGameEngine gameEngine, TurretAIUpdateModuleData moduleData)
        : base(gameObject, gameEngine)
    {
        _moduleData = moduleData;

        GameObject.TurretYaw = MathUtility.ToRadians(_moduleData.NaturalTurretAngle);
        GameObject.TurretPitch = MathUtility.ToRadians(_moduleData.NaturalTurretPitch);

        _turretAIstate = _moduleData.InitiallyDisabled ? TurretAIStates.Disabled : TurretAIStates.ScanningForTargets;
    }

    public override UpdateSleepTime Update()
    {
        // TODO(Port): Use correct value.
        return UpdateSleepTime.None;
    }

    internal void Update(BitArray<AutoAcquireEnemiesType> autoAcquireEnemiesWhenIdle)
    {
        var target = GameObject.CurrentWeapon?.CurrentTarget;
        float targetYaw;

        var currentFrame = GameEngine.GameLogic.CurrentFrame;

        if (GameObject.ModelConditionFlags.Get(ModelConditionFlag.Moving))
        {
            _turretAIstate = TurretAIStates.Recentering;
            _waitUntil = currentFrame + _moduleData.RecenterTime;
            GameObject.CurrentWeapon?.SetTarget(null);
        }

        switch (_turretAIstate)
        {
            case TurretAIStates.Disabled:
                break; // TODO: how does it get enabled?

            case TurretAIStates.Idle:
                if (target != null)
                {
                    _turretAIstate = TurretAIStates.Turning;
                    _currentTarget = target;
                }
                else if (currentFrame >= _waitUntil && (autoAcquireEnemiesWhenIdle?.Get(AutoAcquireEnemiesType.Yes) ?? true))
                {
                    _turretAIstate = TurretAIStates.ScanningForTargets;
                }
                break;

            case TurretAIStates.ScanningForTargets:
                if (target == null)
                {
                    if (!FoundTargetWhileScanning(autoAcquireEnemiesWhenIdle))
                    {
                        var scanInterval = GameEngine.GameLogic.Random.NextLogicFrameSpan(
                            _moduleData.MinIdleScanInterval,
                            _moduleData.MaxIdleScanInterval);
                        _waitUntil = currentFrame + scanInterval;
                        _turretAIstate = TurretAIStates.Idle;
                        break;
                    }
                }

                if (!_moduleData.FiresWhileTurning)
                {
                    GameObject.ModelConditionFlags.Set(ModelConditionFlag.Attacking, false);
                }

                _turretAIstate = TurretAIStates.Turning;
                break;

            case TurretAIStates.Turning:
                if (target == null)
                {
                    _waitUntil = currentFrame + _moduleData.RecenterTime;
                    _turretAIstate = TurretAIStates.Recentering;
                    break;
                }

                var directionToTarget = (target.TargetPosition - GameObject.Translation).Vector2XY();
                targetYaw = MathUtility.GetYawFromDirection(directionToTarget) - GameObject.Yaw;

                if (Rotate(targetYaw))
                {
                    break;
                }

                if (!_moduleData.FiresWhileTurning)
                {
                    GameObject.ModelConditionFlags.Set(ModelConditionFlag.Attacking, true);
                }

                _turretAIstate = TurretAIStates.Attacking;
                break;

            case TurretAIStates.Attacking:
                if (target == null)
                {
                    _waitUntil = currentFrame + _moduleData.RecenterTime;
                    _turretAIstate = TurretAIStates.Recentering;
                }
                else if (target != _currentTarget)
                {
                    _turretAIstate = TurretAIStates.Turning;
                    _currentTarget = target;
                }
                break;

            case TurretAIStates.Recentering:
                if (currentFrame >= _waitUntil)
                {
                    targetYaw = MathUtility.ToRadians(_moduleData.NaturalTurretAngle);
                    if (!Rotate(targetYaw))
                    {
                        _turretAIstate = TurretAIStates.Idle;
                    }
                }
                break;
        }
    }

    private bool Rotate(float targetYaw)
    {
        var deltaYaw = MathUtility.CalculateAngleDelta(GameObject.TurretYaw, targetYaw);

        // GPL friend_turnTowardsAngle (TurretAI.cpp:392-429): only snap once the remaining
        // angle is smaller than a single frame's turn-rate step, otherwise advance by exactly
        // TurretTurnRate. This keeps per-frame overshoot bounded by the configured turn rate
        // instead of instantaneously snapping the last stretch of any turn.
        if (MathF.Abs(deltaYaw) > _moduleData.TurretTurnRate)
        {
            GameObject.TurretYaw -= MathF.Sign(deltaYaw) * _moduleData.TurretTurnRate;
            return true;
        }
        GameObject.TurretYaw -= deltaYaw;
        return false;
    }

    private bool FoundTargetWhileScanning(BitArray<AutoAcquireEnemiesType> autoAcquireEnemiesWhenIdle)
    {
        return false;

        //var attacksBuildings = autoAcquireEnemiesWhenIdle?.Get(AutoAcquireEnemiesType.AttackBuildings) ?? true;
        //var scanRange = GameObject.CurrentWeapon.Template.AttackRange;

        //var restrictedByScanAngle = _moduleData.MinIdleScanAngle != 0 && _moduleData.MaxIdleScanAngle != 0;
        //var scanAngleOffset = context.GameEngine.Random.NextDouble() *
        //                (_moduleData.MaxIdleScanAngle - _moduleData.MinIdleScanAngle) +
        //                _moduleData.MinIdleScanAngle;

        //var nearbyObjects = context.GameEngine.Scene3D.Quadtree.FindNearby(GameObject, GameObject.Transform, scanRange);
        //foreach (var obj in nearbyObjects)
        //{
        //    if (obj.Definition.KindOf.Get(ObjectKinds.Structure) && !attacksBuildings)
        //    {
        //        continue;
        //    }

        //    if (restrictedByScanAngle)
        //    {
        //        // TODO: test with GLAVehicleTechnicalChassisOne
        //        var deltaTranslation = obj.Translation - GameObject.Translation;
        //        var direction = deltaTranslation.Vector2XY();
        //        var angleToObject = MathUtility.GetYawFromDirection(direction);
        //        var angleDelta = MathUtility.CalculateAngleDelta(angleToObject, GameObject.EulerAngles.Z + MathUtility.ToRadians(_moduleData.NaturalTurretAngle));

        //        if (angleDelta < -scanAngleOffset || scanAngleOffset < angleDelta)
        //        {
        //            continue;
        //        }
        //    }

        //    GameObject.CurrentWeapon.SetTarget(new WeaponTarget(obj));
        //    return true;
        //}

        //return false;
    }

    internal override void Load(StatePersister reader)
    {
        // Version 1-2 persisted seven fields shaped after retail's TurretAI::xfer
        // (TurretAI.cpp:343-378), a different (state-machine) class than this file's own
        // _currentTarget/_waitUntil/_turretAIstate state -- none of the module's real mutable
        // state was ever round-tripped, so a save/load lost the live turret state entirely.
        // The module was [ParseOnly] (never instantiated) until this R12 landing, so no real
        // save data in that shape exists to stay compatible with; version 3 persists the
        // module's actual state instead.
        reader.PersistVersion(3);

        reader.PersistEnum(ref _turretAIstate);
        reader.PersistLogicFrame(ref _waitUntil);

        // _currentTarget only ever needs to survive a round trip as an object reference here:
        // every live caller feeds this state machine an object-type WeaponTarget (the current
        // weapon's CurrentTarget, acquired via targeting/scripted-attack orders). A
        // position-type WeaponTarget has no persistable identity of its own, so one round-trips
        // as "no target" (matching the Attacking/Turning "target == null" path) rather than
        // being silently kept stale.
        var targetObjectId = _currentTarget?.TargetObjectId ?? ObjectId.Invalid;
        reader.PersistObjectId(ref targetObjectId);
        _currentTarget = targetObjectId.IsValid
            ? new WeaponTarget(GameEngine.GameLogic, targetObjectId)
            : null;
    }
}

public sealed class TurretAIUpdateModuleData : UpdateModuleData
{
    internal static TurretAIUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<TurretAIUpdateModuleData> FieldParseTable = new IniParseTable<TurretAIUpdateModuleData>
    {
        { "InitiallyDisabled", (parser, x) => x.InitiallyDisabled = parser.ParseBoolean() },
        { "TurretTurnRate", (parser, x) => x.TurretTurnRate = parser.ParseAngularVelocityToLogicFrames() },
        { "TurretPitchRate", (parser, x) => x.TurretPitchRate = parser.ParseInteger() },
        { "AllowsPitch", (parser, x) => x.AllowsPitch = parser.ParseBoolean() },
        { "FiresWhileTurning", (parser, x) => x.FiresWhileTurning = parser.ParseBoolean() },
        { "NaturalTurretPitch", (parser, x) => x.NaturalTurretPitch = parser.ParseInteger() },
        { "NaturalTurretAngle", (parser, x) => x.NaturalTurretAngle = parser.ParseInteger() },
        { "GroundUnitPitch", (parser, x) => x.GroundUnitPitch = parser.ParseInteger() },
        { "MinPhysicalPitch", (parser, x) => x.MinPhysicalPitch = parser.ParseInteger() },
        { "FirePitch", (parser, x) => x.FirePitch = parser.ParseInteger() },
        { "MinIdleScanAngle", (parser, x) => x.MinIdleScanAngle = parser.ParseInteger() },
        { "MaxIdleScanAngle", (parser, x) => x.MaxIdleScanAngle = parser.ParseInteger() },
        { "MinIdleScanInterval", (parser, x) => x.MinIdleScanInterval = parser.ParseTimeMillisecondsToLogicFrames() },
        { "MaxIdleScanInterval", (parser, x) => x.MaxIdleScanInterval = parser.ParseTimeMillisecondsToLogicFrames() },
        { "RecenterTime", (parser, x) => x.RecenterTime = parser.ParseTimeMillisecondsToLogicFrames() },
        { "ControlledWeaponSlots", (parser, x) => x.ControlledWeaponSlots = parser.ParseEnumBitArray<WeaponSlot>() },

        { "TurretFireAngleSweep", (parser, x) => x.TurretFireAngleSweeps.Add(parser.ParseEnum<WeaponSlot>(), parser.ParseInteger()) },
        { "TurretSweepSpeedModifier", (parser, x) => x.TurretSweepSpeedModifiers.Add(parser.ParseEnum<WeaponSlot>(), parser.ParseFloat()) },

        { "TurretMaxDeflectionCW", (parser, x) => x.TurretMaxDeflectionCW = parser.ParseInteger() },
        { "TurretMaxDeflectionACW", (parser, x) => x.TurretMaxDeflectionACW = parser.ParseInteger() },
    };

    public bool InitiallyDisabled { get; private set; }

    /// <summary>
    /// Turn rate, in radians per logic frame.
    /// </summary>
    public float TurretTurnRate { get; private set; }

    public int TurretPitchRate { get; private set; }

    public bool AllowsPitch { get; private set; }

    public bool FiresWhileTurning { get; private set; }

    public int NaturalTurretPitch { get; private set; }

    public int NaturalTurretAngle { get; private set; }

    public int GroundUnitPitch { get; private set; }

    /// <summary>
    /// If allows pitch, the lowest I can dip down to shoot.defaults to 0 (horizontal)
    /// </summary>
    public int MinPhysicalPitch { get; private set; }

    /// <summary>
    /// Instead of aiming pitchwise at the target, it will aim here
    /// </summary>
    public int FirePitch { get; private set; }

    /// <summary>
    /// Minimum offset, in degrees, from <see cref="NaturalTurretAngle"/>.
    /// </summary>
    public int MinIdleScanAngle { get; private set; }

    /// <summary>
    /// Maximum offset, in degrees, from <see cref="NaturalTurretAngle"/>.
    /// </summary>
    public int MaxIdleScanAngle { get; private set; }

    public LogicFrameSpan MinIdleScanInterval { get; private set; }

    public LogicFrameSpan MaxIdleScanInterval { get; private set; }

    /// <summary>
    /// Time to wait when idling before recentering.
    /// </summary>
    public LogicFrameSpan RecenterTime { get; private set; }

    public BitArray<WeaponSlot> ControlledWeaponSlots { get; private set; }
    public Dictionary<WeaponSlot, int> TurretFireAngleSweeps { get; } = new Dictionary<WeaponSlot, int>();

    /// <summary>
    /// Sweep slower than you turn
    /// /// </summary>
    public Dictionary<WeaponSlot, float> TurretSweepSpeedModifiers { get; } = new Dictionary<WeaponSlot, float>();

    [AddedIn(SageGame.Bfme2Rotwk)]
    public int TurretMaxDeflectionCW { get; private set; }

    [AddedIn(SageGame.Bfme2Rotwk)]
    public int TurretMaxDeflectionACW { get; private set; }

    internal TurretAIUpdate CreateTurretAIUpdate(GameObject gameObject, IGameEngine gameEngine)
    {
        return new TurretAIUpdate(gameObject, gameEngine, this);
    }
}

public sealed class TurretAITargetChooserData : BaseAITargetChooserData
{

}
