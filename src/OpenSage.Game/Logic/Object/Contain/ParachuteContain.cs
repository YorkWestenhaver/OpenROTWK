// ParachuteContain - R12 port. GPL reference:
// Generals/Code/GameEngine/Source/GameLogic/Object/Contain/ParachuteContain.cpp and its header
// (Steven Johnson, March 2002); GeneralsMD carries the same file with FreeFallDamagePercent /
// KillWhenLandingInWaterSlop added. Manages the aerial descent and landing of a single contained
// rider: opens at altitude, sways the parachute/rider via a spring-damper on pitch/roll, toggles
// collisions, hands the parachute to the AI locomotor for landing, and ejects/kills the rider on
// ground or water impact.
//
// This module predates OpenSage's SimCore migration (Contain/ is not yet in
// SimCoreScopedDirs.txt) and stays on the legacy float/GameObject substrate, matching every
// other landed Contain module (TransportContain, HordeContain, etc).
//
// Deliberate simplifications where GPL logic lives outside this file or OpenSage lacks the
// supporting infra (each is a real gap, not an invented behavior):
//   - "findPositionAround" (PartitionManager.cpp, a different GPL file) is approximated with a
//     deterministic ring search over PartitionCellManager.QueryObjects - same 100-unit radius,
//     same "first clear spot wins" contract, not the original's exact spiral.
//   - PhysicsBehavior does not yet expose setAllowToFall/setIsInFreeFall (GPL's post-eject
//     "let the rider actually fall" hook), so onDie/RemoveRider skip that call; the model
//     condition / AI transitions that make the rider walk or drop are still wired.
//   - The onRemoving rally-point routing (ProducerID -> ExitInterface chain) and the
//     PathfindCell CELL_CLIFF/CELL_WATER/CELL_IMPASSABLE eject-kill are not ported (no pathfind
//     cell classification exists yet); the water-slop kill and off-map kill are.
//   - Locomotor::setCloseEnoughDist / setUltraAccurate (fired once an override destination is
//     set, GPL update()) have no runtime setters on Locomotor.cs; that file is shared far
//     beyond this module and this task's identifier budget doesn't cover adding them, so the
//     knobs themselves are not toggled. SetOverrideDestination's landing target is still fully
//     wired (Open() -> ai.SetTargetPoint(destination)).

using System;
using System.Numerics;
using OpenSage.Audio;
using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.Logic;
using OpenSage.Mathematics;

namespace OpenSage.Logic.Object;

public sealed class ParachuteContain : UpdateModule, IContainModule, IDieModule, ICollideModule
{
    /// <summary>GPL ParachuteContain::update: "damp the swaying a bunch when we get close".</summary>
    private const float AltitudeDampStart = 20.0f;

    private readonly ParachuteContainModuleData _data;

    private ObjectId _riderId = ObjectId.Invalid;

    private bool _opened;
    private bool _needToUpdateParaBones = true;
    private bool _needToUpdateRiderBones = true;

    private float _pitch;
    private float _roll;
    private float _pitchRate;
    private float _rollRate;

    private float? _startZ;

    private bool _isLandingOverrideSet;
    private Vector3 _landingOverride;

    private Vector3 _riderAttachBone;
    private Vector3 _paraAttachBone;
    private Vector3 _paraSwayBone;

    private Vector3 _riderAttachOffset;
    private Vector3 _riderSwayOffset;
    private Vector3 _paraAttachOffset;
    private Vector3 _paraSwayOffset;

    internal ParachuteContain(GameObject gameObject, IGameEngine gameEngine, ParachuteContainModuleData moduleData)
        : base(gameObject, gameEngine)
    {
        _data = moduleData;

        _pitchRate = gameEngine.GameLogic.Random.NextSingle(-moduleData.PitchRateMax, moduleData.PitchRateMax);
        _rollRate = gameEngine.GameLogic.Random.NextSingle(-moduleData.RollRateMax, moduleData.RollRateMax);

        gameObject.SetObjectStatus(ObjectStatus.Parachuting, true);
    }

    // ---- test / public surface -------------------------------------------------------------

    public GameObject Rider => _riderId.IsValid ? GameEngine.GameLogic.GetObjectById(_riderId) : null;

    internal bool IsOpened => _opened;
    internal float Pitch => _pitch;
    internal float Roll => _roll;
    internal float PitchRate => _pitchRate;
    internal float RollRate => _rollRate;
    internal bool IsLandingOverrideSet => _isLandingOverrideSet;
    internal Vector3 LandingOverride => _landingOverride;
    internal Vector3 RiderAttachOffset => _riderAttachOffset;
    internal Vector3 RiderSwayOffset => _riderSwayOffset;
    internal Vector3 ParaSwayOffset => _paraSwayOffset;

    /// <summary>GPL ParachuteContain::isValidContainerFor.</summary>
    public bool IsValidContainerFor(GameObject rider)
    {
        if (rider == null || Rider != null)
        {
            return false;
        }

        if (_data.AllowInsideKindOf != null &&
            _data.AllowInsideKindOf.AnyBitSet &&
            !_data.AllowInsideKindOf.Intersects(rider.Definition.KindOf))
        {
            return false;
        }

        var transportSlotCount = rider.Definition.TransportSlotCount;
        if (transportSlotCount == 0 &&
            !rider.IsKindOf(ObjectKinds.Infantry) &&
            !rider.IsKindOf(ObjectKinds.Parachutable))
        {
            return false;
        }

        return true;
    }

    /// <summary>GPL ParachuteContain::onContaining.</summary>
    public void AddRider(GameObject rider)
    {
        _riderId = rider.Id;
        rider.AddToContainer(GameObject.Id);

        rider.SetDisabled(DisabledType.Held);
        rider.SetObjectStatus(ObjectStatus.Parachuting, true);

        // clearAndSetModelConditionState(PARACHUTING, FREEFALL): the rider starts falling free,
        // and only switches to the parachuting pose once this module opens the chute.
        rider.ModelConditionFlags.Set(ModelConditionFlag.Parachuting, false);
        rider.ModelConditionFlags.Set(ModelConditionFlag.FreeFall, true);
        _needToUpdateRiderBones = true;

        PositionRider(rider);
    }

    /// <summary>GPL ParachuteContain::onRemoving.</summary>
    public void RemoveRider()
    {
        var rider = Rider;
        if (rider == null)
        {
            return;
        }

        rider.ClearDisabled(DisabledType.Held);
        rider.SetObjectStatus(ObjectStatus.Parachuting, false);

        // "it is just ephemeral at this point" - the chute stops colliding once its passenger
        // has left it.
        GameObject.SetObjectStatus(ObjectStatus.NoCollisions, true);

        PositionRider(rider);

        rider.ModelConditionFlags.Set(ModelConditionFlag.FreeFall, false);
        rider.ModelConditionFlags.Set(ModelConditionFlag.Parachuting, false);
        _needToUpdateRiderBones = true;

        // GPL routes through a producer rally point when one exists, else aiIdle (skirmish AI
        // gets aiHunt instead). The producer -> ExitInterface chain isn't ported, so this always
        // takes the "no rally point" branch.
        rider.AIUpdate?.AIIdle(CommandSourceType.FromAI);

        // "if we land in the water, we die."
        if (GameEngine.Game.TerrainLogic.IsUnderwater(rider.Translation.X, rider.Translation.Y, out var waterZ) &&
            rider.Translation.Z <= waterZ + _data.KillWhenLandingInWaterSlop &&
            rider.Layer == PathfindLayerType.Ground)
        {
            rider.AttemptDamage(new DamageInfoInput
            {
                DamageType = DamageType.Water,
                DeathType = DeathType.Flooded,
                Amount = DamageConstants.HugeDamageAmount,
            });
        }

        // GPL also kills riders who land off the pathfind map, on a cliff, in water, or on
        // impassable ground (PathfindCell lookup - not ported). IsOffMap is the one part of that
        // check OpenSage already tracks.
        if (rider.IsOffMap)
        {
            rider.Kill();
        }

        _riderId = ObjectId.Invalid;
        rider.RemoveFromContainer();
    }

    /// <summary>GPL ParachuteContain::setOverrideDestination.</summary>
    public void SetOverrideDestination(in Vector3 destination)
    {
        _landingOverride = destination;
        _isLandingOverrideSet = true;
    }

    // ---- per-frame ---------------------------------------------------------------------------

    public override UpdateSleepTime Update()
    {
        var parachute = GameObject;

        if (parachute.IsDisabledByType(DisabledType.Held))
        {
            return UpdateSleepTime.None;
        }

        var rider = Rider;

        if (_startZ == null)
        {
            var groundHeight = GameEngine.Game.TerrainLogic.GetGroundHeight(parachute.Translation.X, parachute.Translation.Y);
            var startZ = parachute.Translation.Z;
            if (startZ - groundHeight < 2f * _data.ParachuteOpenDist)
            {
                // Ejected too close to the ground to open normally - fudge the start height up
                // so the chute still gets a chance to open before landing.
                startZ = groundHeight + 2f * _data.ParachuteOpenDist;
            }
            _startZ = startZ;
        }

        if (!_opened)
        {
            if (MathF.Abs(_startZ.Value - parachute.Translation.Z) >= _data.ParachuteOpenDist)
            {
                Open(rider);
            }
            else if (rider != null)
            {
                rider.ModelConditionFlags.Set(ModelConditionFlag.Parachuting, false);
                rider.ModelConditionFlags.Set(ModelConditionFlag.FreeFall, true);
            }
        }

        parachute.Hidden = !_opened;

        if (!_opened || rider == null)
        {
            // Unopened, or empty, chutes don't collide with anything - simplifies ejections,
            // paradrops, and landings.
            parachute.SetObjectStatus(ObjectStatus.NoCollisions, true);
            rider?.SetObjectStatus(ObjectStatus.NoCollisions, true);
        }
        else
        {
            parachute.SetObjectStatus(ObjectStatus.NoCollisions, false);
            rider.SetObjectStatus(ObjectStatus.NoCollisions, false);
        }

        var ai = parachute.AIUpdate;
        if (ai != null && !parachute.IsEffectivelyDead)
        {
            ai.SetLocomotor(_opened ? LocomotorSetType.Normal : LocomotorSetType.FreeFall);

            var locomotor = ai.CurrentLocomotor;
            if (locomotor != null)
            {
                var altitudeDamping = 0f;
                if (rider != null && rider.HeightAboveTerrain <= AltitudeDampStart)
                {
                    altitudeDamping = _data.LowAltitudeDamping;
                }

                if (_opened)
                {
                    var template = locomotor.LocomotorTemplate;
                    var pitchDamping = template.PitchDamping + altitudeDamping;
                    var rollDamping = template.RollDamping + altitudeDamping;

                    // spring/damper
                    _pitchRate += (-template.PitchStiffness * _pitch) + (-pitchDamping * _pitchRate);
                    _rollRate += (-template.RollStiffness * _roll) + (-rollDamping * _rollRate);

                    _pitch += _pitchRate;
                    _roll += _rollRate;

                    // GPL also calls locomotor->setCloseEnoughDist(10.0) / setUltraAccurate(TRUE)
                    // here once an override destination is set. Locomotor.cs (a shared file well
                    // outside this module's own surface) doesn't expose runtime setters for
                    // either knob yet - infra gap, not ported; SetOverrideDestination's landing
                    // target itself is still fully wired below (ai.SetTargetPoint).
                }

                UpdateBonePositions();
                UpdateOffsetsFromBones();

                parachute.Drawable.InstanceMatrix = CalcSwayMatrix(_paraSwayOffset);

                PositionContainedObjectsRelativeToContainer();
            }
        }

        // allow landing on bridges - TODO(Port): GetHighestLayerForDestination is stubbed to
        // Ground until bridge layers are ported (matches TerrainLogic's existing TODO).
        var layer = GameEngine.Game.TerrainLogic.GetHighestLayerForDestination(parachute.Translation);
        parachute.Layer = layer;
        if (rider != null)
        {
            rider.Layer = layer;
        }

        // If we've lost our passenger for whatever reason, die early. Otherwise we sit around
        // forever.
        if (Rider == null)
        {
            parachute.Kill();
        }

        // the collide system doesn't always collide us with the ground if we fall into water,
        // so force the issue.
        if (!parachute.IsEffectivelyDead &&
            parachute.Layer == PathfindLayerType.Ground &&
            GameEngine.Game.TerrainLogic.IsUnderwater(parachute.Translation.X, parachute.Translation.Y, out var waterZ) &&
            (parachute.Translation.Z - waterZ) < _data.KillWhenLandingInWaterSlop)
        {
            parachute.Kill();
        }

        return UpdateSleepTime.None;
    }

    private void Open(GameObject rider)
    {
        _opened = true;
        GameObject.ModelConditionFlags.Set(ModelConditionFlag.FreeFall, false);
        GameObject.ModelConditionFlags.Set(ModelConditionFlag.Parachuting, true);
        _needToUpdateParaBones = true;

        if (rider != null)
        {
            rider.ModelConditionFlags.Set(ModelConditionFlag.FreeFall, false);
            rider.ModelConditionFlags.Set(ModelConditionFlag.Parachuting, true);
            _needToUpdateRiderBones = true;

            var sound = _data.ParachuteOpenSound?.Value;
            if (sound != null)
            {
                GameEngine.AudioSystem?.PlayAudioEvent(rider, sound);
            }
        }

        // When a parachute opens, it looks for a good place to land - explicitly set via
        // SetOverrideDestination, otherwise any clear spot nearby.
        var ai = GameObject.AIUpdate;
        if (ai == null)
        {
            return;
        }

        Vector3 target;
        if (_isLandingOverrideSet)
        {
            target = _landingOverride;
        }
        else
        {
            target = FindClearLandingSpot(GameObject.Translation);
        }

        ai.SetTargetPoint(target);
    }

    private void UpdateBonePositions()
    {
        if (_needToUpdateParaBones)
        {
            _needToUpdateParaBones = false;

            var (_, cogBone) = GameObject.Drawable.FindBone("PARA_COG");
            _paraSwayBone = cogBone?.Transform.Translation ?? Vector3.Zero;

            // GPL spells this bone "PARA_ATTCH" (a retail typo); the task spec and BFME2 art use
            // the corrected "PARA_ATTACH", which is what actually ships on parachute models.
            var (_, attachBone) = GameObject.Drawable.FindBone("PARA_ATTACH");
            _paraAttachBone = attachBone?.Transform.Translation ?? Vector3.Zero;
        }

        if (_needToUpdateRiderBones)
        {
            _needToUpdateRiderBones = false;

            var rider = Rider;
            if (rider != null)
            {
                var (_, manBone) = rider.Drawable.FindBone("PARA_MAN");
                _riderAttachBone = manBone != null
                    ? manBone.Transform.Translation
                    : new Vector3(0f, 0f, rider.Geometry.MaxZ);
            }
        }
    }

    private void UpdateOffsetsFromBones()
    {
        _paraSwayOffset = Vector3.Transform(_paraSwayBone, GameObject.Rotation);
        _paraAttachOffset = Vector3.Transform(_paraAttachBone, GameObject.Rotation);

        var rider = Rider;
        if (rider != null)
        {
            var riderAttachWorldOffset = Vector3.Transform(_riderAttachBone, rider.Rotation);
            _riderAttachOffset = _paraAttachOffset - riderAttachWorldOffset;
            _riderSwayOffset = _paraSwayOffset - _riderAttachOffset;
        }
    }

    private void PositionContainedObjectsRelativeToContainer()
    {
        var rider = Rider;
        if (rider != null)
        {
            PositionRider(rider);
        }
    }

    private void PositionRider(GameObject rider)
    {
        UpdateBonePositions();
        UpdateOffsetsFromBones();

        var pos = GameObject.Translation + _riderAttachOffset;
        rider.UpdateTransform(pos, GameObject.Rotation);

        var alt = rider.HeightAboveTerrain;
        if (alt < 0f)
        {
            // don't let him go below ground.
            pos.Z -= alt;
            rider.UpdateTransform(pos, GameObject.Rotation);
        }

        if (rider.IsDisabledByType(DisabledType.Held))
        {
            rider.Drawable.InstanceMatrix = CalcSwayMatrix(_riderSwayOffset);
        }
        else
        {
            rider.Drawable.InstanceMatrix = Matrix4x4.Identity;
        }
    }

    /// <summary>
    /// GPL ParachuteContain::calcSwayMtx: translate to the bone offset, roll (X) then pitch (Y)
    /// about that pivot, translate back. System.Numerics composes row-vector transforms
    /// left-to-right (v' = v * M), so the pivot translate goes first here.
    /// </summary>
    private Matrix4x4 CalcSwayMatrix(in Vector3 offset)
    {
        return Matrix4x4.CreateTranslation(-offset)
            * Matrix4x4.CreateRotationX(_roll)
            * Matrix4x4.CreateRotationY(_pitch)
            * Matrix4x4.CreateTranslation(offset);
    }

    /// <summary>
    /// Stand-in for ThePartitionManager->findPositionAround (PartitionManager.cpp - a different
    /// GPL file, not ported here): a deterministic ring search for a spot with no other object's
    /// collision circle overlapping ours, within the same 0..100 unit radius GPL's
    /// FindPositionOptions uses. Falls back to <paramref name="center"/> when nothing is clear.
    /// </summary>
    private Vector3 FindClearLandingSpot(Vector3 center)
    {
        const float maxRadius = 100f;
        const float ringStep = 10f;
        const int samplesPerRing = 8;

        var partitionManager = GameEngine.Game.PartitionCellManager;
        var myRadius = GameObject.Geometry.MajorRadius;

        for (var radius = ringStep; radius <= maxRadius; radius += ringStep)
        {
            for (var i = 0; i < samplesPerRing; i++)
            {
                var angle = (MathF.PI * 2f / samplesPerRing) * i;
                var candidate = center + new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, 0f);

                var blocked = false;
                foreach (var obstacle in partitionManager.QueryObjects(
                    GameObject, candidate, myRadius + radius, new PartitionQueries.TrueQuery()))
                {
                    if (obstacle.Id == _riderId)
                    {
                        continue;
                    }
                    if (Vector3.DistanceSquared(candidate, obstacle.Translation) <
                        MathUtility.Square(myRadius + obstacle.Geometry.MajorRadius))
                    {
                        blocked = true;
                        break;
                    }
                }

                if (!blocked)
                {
                    var groundZ = GameEngine.Game.TerrainLogic.GetGroundHeight(candidate.X, candidate.Y);
                    return candidate with { Z = groundZ };
                }
            }
        }

        return center;
    }

    // ---- die / collide reactions --------------------------------------------------------------

    /// <summary>
    /// GPL ParachuteContain::onDie: if the chute is destroyed while airborne, the rider falls
    /// screaming to his death - ejected immediately and hit with FreeFallDamagePercent of his
    /// max health.
    /// </summary>
    void IDieModule.OnDie(in DamageInfoInput damageInput)
    {
        if (!GameObject.IsSignificantlyAboveTerrain)
        {
            return;
        }

        var rider = Rider;
        if (rider == null)
        {
            return;
        }

        RemoveRider();

        if ((float)_data.FreeFallDamagePercent > 0f)
        {
            rider.AttemptDamage(new DamageInfoInput(GameObject)
            {
                DamageType = DamageType.Falling,
                DeathType = DeathType.Splatted,
                Amount = rider.BodyModule.MaxHealth * _data.FreeFallDamagePercent,
            });
        }

        // GPL also forces the rider's PhysicsBehavior into free-fall here
        // (setAllowToFall/setIsInFreeFall + a zero applyForce to wake it up).
        // PhysicsBehavior doesn't expose those hooks yet - infra gap, not ported.
    }

    /// <summary>GPL ParachuteContain::onCollide: other == null means "collide with ground".</summary>
    public void OnCollide(GameObject other, in Vector3 location, in Vector3 normal)
    {
        if (other != null)
        {
            return;
        }

        if (GameObject.ContainedBy != null)
        {
            // still inside a transport plane - ignore.
            return;
        }

        RemoveRider();
        GameObject.Kill();
    }

    // ---- IContainModule --------------------------------------------------------------------

    public bool IsGarrisonable => false;
    public bool IsImmuneToClearBuildingAttacks => false;
    public bool IsRiderChangeContain => false;
    public uint ContainCount => Rider != null ? 1u : 0u;

    // TODO(Port): mass isn't tracked for parachute riders yet.
    public float ContainedItemsMass => 0f;

    public ReadOnlySpan<GameObject> ContainedItems
    {
        get
        {
            var rider = Rider;
            return rider != null ? new[] { rider } : Array.Empty<GameObject>();
        }
    }

    public void OrderAllPassengersToIdle(CommandSourceType commandType)
    {
        Rider?.AIUpdate?.AIIdle(commandType);
    }

    public void OrderAllPassengersToHackInternet(CommandSourceType commandType)
    {
        // Parachutes never carry hackers.
    }

    // ---- persistence -------------------------------------------------------------------------

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        reader.PersistObjectId(ref _riderId);

        reader.PersistSingle(ref _pitch);
        reader.PersistSingle(ref _roll);
        reader.PersistSingle(ref _pitchRate);
        reader.PersistSingle(ref _rollRate);

        var startZ = _startZ ?? float.NaN;
        reader.PersistSingle(ref startZ);
        if (reader.Mode == StatePersistMode.Read)
        {
            _startZ = float.IsNaN(startZ) ? null : startZ;
        }

        reader.PersistBoolean(ref _isLandingOverrideSet);
        reader.PersistVector3(ref _landingOverride);

        reader.PersistVector3(ref _riderAttachBone);
        reader.PersistVector3(ref _paraAttachBone);
        reader.PersistVector3(ref _paraSwayBone);

        reader.PersistVector3(ref _riderAttachOffset);
        reader.PersistVector3(ref _riderSwayOffset);
        reader.PersistVector3(ref _paraAttachOffset);
        reader.PersistVector3(ref _paraSwayOffset);

        reader.PersistBoolean(ref _needToUpdateRiderBones);
        reader.PersistBoolean(ref _needToUpdateParaBones);
        reader.PersistBoolean(ref _opened);
    }
}

/// <summary>
/// Hardcoded to utilize PARA_MAN, PARA_ATTACH and PARA_COG bones on contained object.
/// </summary>
public sealed class ParachuteContainModuleData : ContainModuleData
{
    internal static ParachuteContainModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<ParachuteContainModuleData> FieldParseTable = new IniParseTable<ParachuteContainModuleData>
    {
        { "PitchRateMax", (parser, x) => x.PitchRateMax = parser.ParseAngularVelocityToLogicFrames() },
        { "RollRateMax", (parser, x) => x.RollRateMax = parser.ParseAngularVelocityToLogicFrames() },
        { "LowAltitudeDamping", (parser, x) => x.LowAltitudeDamping = parser.ParseFloat() },
        { "ParachuteOpenDist", (parser, x) => x.ParachuteOpenDist = parser.ParseFloat() },
        { "AllowInsideKindOf", (parser, x) => x.AllowInsideKindOf = parser.ParseEnumBitArray<ObjectKinds>() },
        { "ParachuteOpenSound", (parser, x) => x.ParachuteOpenSound = parser.ParseAudioEventReference() },
        { "FreeFallDamagePercent", (parser, x) => x.FreeFallDamagePercent = parser.ParsePercentage() },
        { "KillWhenLandingInWaterSlop", (parser, x) => x.KillWhenLandingInWaterSlop = parser.ParseFloat() },
    };

    public float PitchRateMax { get; private set; }
    public float RollRateMax { get; private set; }
    public float LowAltitudeDamping { get; private set; } = 0.2f;
    public float ParachuteOpenDist { get; private set; }
    public BitArray<ObjectKinds> AllowInsideKindOf { get; private set; } = new();
    public LazyAssetReference<BaseAudioEventInfo> ParachuteOpenSound { get; private set; }

    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public Percentage FreeFallDamagePercent { get; private set; } = new(0.5f);

    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public float KillWhenLandingInWaterSlop { get; private set; } = 10.0f;

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new ParachuteContain(gameObject, gameEngine, this);
    }
}
