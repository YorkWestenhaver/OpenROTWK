// SpectreGunshipUpdate - R12 port (Round-4 backlog; census: Update).
//
// Behavioral reference: generals-gpl GeneralsMD SpectreGunshipUpdate.cpp/.h (semantics only;
// fresh code against the frozen contract). GPL's module is a SpecialPowerUpdateModule: a
// three-phase orbital-bombardment state machine (INSERTING -> ORBITING -> DEPARTING) that
// flies the gunship to a satellite point on an orbit ring around the cast target, then
// alternates a continuously-tracking "gattling" aim point with periodic howitzer volleys,
// then departs off-map and self-destructs.
//
// SEAM GAPS (recorded, not invented - the analyzer/ISimContext facts this port works around):
//   - No SimState SpecialPowerUpdateModule base and no order-pipeline wiring exist yet for
//     ANY special power (Logic/Orders/SpecialPower/*.cs is still the pre-SimCore float
//     applicator system; grep for [SimState] across Logic/Object/SpecialPower/*.cs finds
//     nothing). initiateIntentToDoSpecialPower/setSpecialPowerOverridableDestination have no
//     production caller yet, so this port exposes their semantics as public entry points
//     (Activate/SetOverrideDestination/Disconnect) a future order-pipeline wiring task calls,
//     exactly the shape PointDefenseLaserUpdate and SimHordeContain used for their own
//     not-yet-wired activation seams.
//   - IPartitionQuery.QueryObjectsInRadius only centers on a GameObject, never an arbitrary
//     position (S3 gap, same one PointDefenseLaserUpdate's header does not need but this port
//     does: GPL centers its target search on m_overrideTargetDestination /
//     m_initialTargetPosition, not on the gunship). This port queries a generous
//     GameObject-centered superset (guaranteed to contain the whole attack-area circle) and
//     filters candidates by real point-to-point distance against the actual GPL center, which
//     is mathematically identical to a position-centered query, just less efficient.
//   - PartitionFilterPossibleToAttack / PartitionFilterFreeOfFog have no ported equivalent
//     (no weapon-range-vs-kindof table and no fog-of-war system on this seam yet); this port
//     keeps the live/dead, off-map-parity, relationship and stealth filters (GPL-exact) and
//     omits those two, a strict widening of the candidate set, not a narrowing.
//   - The gattling is a contained UNIT whose OWN AI/weapon module fires it in GPL
//     (aiAttackObject/aiAttackPosition -> that object's own WeaponSet); no SimState AI-attack
//     dispatch exists to command an arbitrary object to fire. This port models the piece that
//     IS fully sim-owned by SpectreGunshipUpdate itself: target ACQUISITION (the validTarget
//     search + isFairDistanceFromShip gate) and the aim-point WIND (m_gattlingTargetPosition
//     stepping by StrafingIncrement, feeding m_okToFireHowitzerCounter) - both observable via
//     AcquiredTargetId/GattlingTargetPosition. The gattling's own shots are a recorded gap
//     (client-presentation-adjacent, same shape as PointDefenseLaserUpdate's unmodeled
//     FX/OCL chain), not delivered here. Howitzer volleys ARE delivered (below): they carry
//     a real WeaponTemplate/DamageNugget and go through DamagePipeline.
//     IMPORTANT (R13 fix): the aim-point wind and its howitzer-lag counter are NOT
//     unconditional in GPL - the whole "GATTLING TARGETING LOGIC" block is gated by
//     `tmp && gattling && gattling->testStatus(OBJECT_STATUS_IS_FIRING_WEAPON)`
//     (SpectreGunshipUpdate.cpp:622), where `tmp` is the configured
//     GattlingStrafeFXParticleSystem template and `gattling` is the contained unit. That gate
//     protects real sim state (the counter that fires the damage-dealing howitzer volley), not
//     just client FX, so this port reproduces it via GattlingIsActivelyFiring(). Because the
//     gattling's own firing is unmodeled (previous bullet), that gate is almost always false
//     today - the howitzer will rarely/never fire until the gattling-firing seam lands, which
//     is the GPL-faithful behavior, not a regression.
//   - GameObject.IsOffMap is a pre-existing engine gap, not one this port introduces
//     (GameObject.cs:1490 "TODO(Port): Actually set _privateStatus." - the flag is declared
//     but nothing sets it yet). Departure -> destroy-self is wired to it faithfully (GPL-exact
//     condition); it activates once that flag is wired elsewhere. Contract tests exercise it
//     by flipping the (reflection-only, test-side) private flag directly.
//   - Disabled/paralysis (DISABLED_PARALYZED gating the contained gattling while off-orbit) has
//     no ported Disabled-condition system for SimState objects; omitted (a recorded gap, not
//     sim-visible today since gattling firing itself is unmodeled per the bullet above).
//   - Howitzer damage delivery: GPL's createAndFireTempWeapon has no persistent ammo/timing
//     (a brand-new temp Weapon per shot, always full) and splashes around a random-offset
//     POSITION. DamagePipeline has no position-centered area delivery yet (D-7 gap, its own
//     file header). This port delivers the howitzer's DamageNugget as direct damage to the
//     currently ACQUIRED target (the point the random offset is centered on) when one exists;
//     a volley with nothing acquired still consumes the cadence slot (a "miss"), matching the
//     original's fire-and-forget shape.
//
// Every mutable sim field appears in Xfer exactly once; tolerances are the field's
// conformance class at its declaration site.

using System.Linq;
using OpenSage.Data.Ini;
using OpenSage.Gui.InGame;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

/// <summary>GPL GunshipStatus. Idle is the unactivated/parked rest state (also the default
/// value of a freshly constructed or freshly loaded-with-nothing module).</summary>
public enum GunshipStatus
{
    Idle,
    Inserting,
    Orbiting,
    Departing,
}

[SimState]
public sealed class SpectreGunshipUpdate : UpdateModule
{
    // GPL ORBIT_INSERTION_SLOPE_MIN/MAX.
    private static readonly Fix64 SlopeMin = Fix64.FromDecimalLiteral("0.5");
    private static readonly Fix64 SlopeMax = Fix64.FromDecimalLiteral("0.8");
    private static readonly Fix64 ThreeQuarters = Fix64.FromDecimalLiteral("0.75");

    // GPL disengageAndDepartAO's mapSize: "head off map in the facing direction, far".
    private static readonly Fix64 DepartureDistance = Fix64.FromDecimalLiteral("99999");

    // SetTargetPosition's desiredSpeed clamps to the locomotor's own max every frame (its own
    // doc comment); this is "as fast as the authored locomotor allows", matching GPL's
    // aiMoveToPosition (no per-call speed override exists in the original either).
    private static readonly Fix64 FullSpeedAhead = Fix64.FromDecimalLiteral("99999");

    private readonly SpectreGunshipUpdateModuleData _data;

    /// <summary>The gunship's altitude at construction (GPL onObjectCreated:
    /// m_satellitePosition.set(obj->getPosition()) - only x/y are recomputed per tick
    /// afterward, so z holds at whatever it started).</summary>
    private readonly Fix64 _orbitAltitude;

    /// <summary>GPL's superset-query radius workaround for the missing position-centered
    /// partition query (see file header). Generous by 2x, not tuned.</summary>
    private readonly Fix64 _acquireQueryRadius;

    private readonly bool _hasHowitzerDamage;
    private readonly Fix64 _howitzerDamageAmount;
    private readonly DamageType _howitzerDamageType;
    private readonly DeathType _howitzerDeathType;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    private GunshipStatus _status;

    /// <summary>GPL m_initialTargetPosition: the cast location, fixed for the whole flight.</summary>
    private FixVector3 _initialTargetPosition;

    /// <summary>GPL m_overrideTargetDestination: player aim-override, constrained to the
    /// attack area each tick.</summary>
    private FixVector3 _overrideTargetDestination;

    /// <summary>GPL m_satellitePosition: the current move-to point on the orbit ring.</summary>
    private FixVector3 _satellitePosition;

    /// <summary>GPL m_gattlingTargetPosition: the winding aim point that steps toward
    /// m_positionToShootAt by StrafingIncrement per tick.</summary>
    private FixVector3 _gattlingTargetPosition;

    /// <summary>GPL m_positionToShootAt: this tick's chosen aim point (override destination,
    /// or an acquired target's position).</summary>
    private FixVector3 _positionToShootAt;

    /// <summary>GPL m_orbitEscapeFrame.</summary>
    private LogicFrame _orbitEscapeFrame;

    /// <summary>GPL m_okToFireHowitzerCounter.</summary>
    private uint _okToFireHowitzerCounter;

    /// <summary>GPL m_gattlingID.</summary>
    private ObjectId _gattlingId = ObjectId.Invalid;

    public SpectreGunshipUpdate(GameObject gameObject, ISimContext context, SpectreGunshipUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _orbitAltitude = SimTransformBridge.PullPosition(gameObject).Z;
        _acquireQueryRadius = (_data.GunshipOrbitRadius + _data.AttackAreaRadius) * Fix64.Two;

        var howitzerTemplate = _data.HowitzerWeaponTemplate?.Value;
        var nugget = howitzerTemplate?.Nuggets.OfType<DamageNugget>().FirstOrDefault();
        if (nugget != null)
        {
            _hasHowitzerDamage = true;
            _howitzerDamageAmount = nugget.Damage;
            _howitzerDamageType = nugget.DamageType;
            _howitzerDeathType = nugget.DeathType;
        }

        // Parked until Activate() (GPL: the module does nothing until
        // initiateIntentToDoSpecialPower fires it).
        SetWakeFrame(UpdateSleepTime.Forever);
    }

    // ---- test/inspector-only views: not part of the save contract (re-derived or transient
    // in GPL too - see the field-by-field Xfer method for what IS persisted) ----

    public GunshipStatus Status => _status;
    internal FixVector3 InitialTargetPosition => _initialTargetPosition;
    internal FixVector3 OverrideTargetDestination => _overrideTargetDestination;
    internal FixVector3 SatellitePosition => _satellitePosition;
    internal FixVector3 GattlingTargetPosition => _gattlingTargetPosition;
    internal FixVector3 PositionToShootAt => _positionToShootAt;
    internal LogicFrame OrbitEscapeFrame => _orbitEscapeFrame;
    internal uint OkToFireHowitzerCounter => _okToFireHowitzerCounter;
    internal ObjectId GattlingId => _gattlingId;

    /// <summary>The target acquired on the most recent periodic re-evaluation tick; not
    /// persisted (GPL doesn't persist validTargetObject either - it is fully re-derived every
    /// HowitzerFiringRate ticks).</summary>
    internal ObjectId AcquiredTargetId { get; private set; } = ObjectId.Invalid;

    /// <summary>GPL doesSpecialPowerHaveOverridableDestinationActive (m_status &lt;
    /// GUNSHIP_STATUS_DEPARTING): true only while actively flying the mission.</summary>
    public bool IsActive => _status == GunshipStatus.Inserting || _status == GunshipStatus.Orbiting;

    // ---- activation surface (future order-pipeline wiring calls these; see file header) ----

    /// <summary>
    /// GPL initiateIntentToDoSpecialPower's non-script path (a player/AI cast at a location).
    /// Spawns a fresh contained gattling (GPL's literal, bug-for-bug behavior: any previously
    /// tracked gattling id is dropped, not destroyed, and a new one is always created - see the
    /// original's onObjectCreated/initiateIntentToDoSpecialPower) and begins the INSERTING
    /// phase. Re-activating while already active is legal in GPL (isPowerCurrentlyInUse always
    /// returns false) and is preserved here.
    /// </summary>
    public void Activate(in FixVector3 targetPosition)
    {
        _initialTargetPosition = targetPosition;
        _overrideTargetDestination = targetPosition;
        _gattlingTargetPosition = targetPosition;

        SpawnGattling();

        _status = GunshipStatus.Inserting;
        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>GPL setSpecialPowerOverridableDestination: the player's targeting-reticle
    /// click. Only takes effect while active (GPL guards on !isDisabled(); this port has no
    /// Disabled-condition seam, so it substitutes the same activity gate the original exposes
    /// via doesSpecialPowerHaveOverridableDestinationActive - a strictly narrower, never wider,
    /// accept condition).</summary>
    public void SetOverrideDestination(in FixVector3 position)
    {
        if (!IsActive)
        {
            return;
        }

        _overrideTargetDestination = position;
    }

    /// <summary>Early termination (task-packet test case): special-power module disconnect, or
    /// any other reason a caller needs to tear the mission down without waiting for orbit
    /// expiry. GPL has no single named method for this - it is the shared tail of cleanUp()
    /// reached from both the orbit-expiry path and the "object vanished" branch of update().
    /// Idempotent.</summary>
    public void Disconnect()
    {
        if (_status == GunshipStatus.Idle)
        {
            return;
        }

        DestroyGattling();
        _status = GunshipStatus.Idle;
        SetWakeFrame(UpdateSleepTime.Forever);
    }

    // ---- the update loop (GPL update()) ----

    public override UpdateSleepTime Update()
    {
        if (GameObject.IsEffectivelyDead)
        {
            // GPL's "else if (m_status != GUNSHIP_STATUS_IDLE)" branch: "the gunship must have
            // gotten shot down" - clean up and park.
            if (_status != GunshipStatus.Idle)
            {
                DestroyGattling();
                _status = GunshipStatus.Idle;
            }

            return UpdateSleepTime.Forever;
        }

        if (_status == GunshipStatus.Idle)
        {
            return UpdateSleepTime.Forever;
        }

        // GPL's two blocks are sequential top-level ifs, NOT mutually exclusive branches: the
        // very tick that transitions INSERTING -> ORBITING also runs the ORBITING combat block
        // that same tick, because the second check re-reads _status after the first block may
        // have changed it. Preserved here for the same reason (see UpdateOrbitNavigation's
        // Inserting -> Orbiting transition).
        if (_status == GunshipStatus.Inserting || _status == GunshipStatus.Orbiting)
        {
            UpdateOrbitNavigation();
        }

        if (_status == GunshipStatus.Orbiting)
        {
            UpdateOrbiting();
        }
        else if (_status == GunshipStatus.Departing)
        {
            UpdateDeparture();
        }

        return UpdateSleepTime.None;
    }

    /// <summary>
    /// GPL's shared INSERTING/ORBITING block: the perigee/apogee/declination orbit-insertion
    /// math (see the original's header ASCII diagram) that produces this tick's satellite
    /// move-to point, the override-destination attack-area constraint, and the
    /// INSERTING -&gt; ORBITING transition once the ship is within GunshipOrbitRadius of the
    /// target.
    /// </summary>
    private void UpdateOrbitNavigation()
    {
        var gunshipPosition = SimTransformBridge.PullPosition(GameObject);

        var perigee = new FixVector3(
            gunshipPosition.X - _initialTargetPosition.X,
            gunshipPosition.Y - _initialTargetPosition.Y,
            Fix64.Zero);
        var distanceToTarget = perigee.Length();
        var perigeeNormalized = perigee.NormalizedOrZero();

        // Anticlockwise perpendicular of perigee.
        var apogee = new FixVector3(-perigeeNormalized.Y, perigeeNormalized.X, Fix64.Zero);

        var n1 = FixMath.Clamp(_data.OrbitInsertionSlope, SlopeMin, SlopeMax);
        var n2 = Fix64.One - n1;

        var declination = (perigeeNormalized * n1) + (apogee * n2);
        var orbitalRadius = _data.GunshipOrbitRadius;

        _satellitePosition = new FixVector3(
            _initialTargetPosition.X + declination.X * orbitalRadius,
            _initialTargetPosition.Y + declination.Y * orbitalRadius,
            _orbitAltitude);

        GameObject.FindBehavior<SimLocomotorUpdate>()?.SetTargetPosition(_satellitePosition, FullSpeedAhead);

        // Constrain the override destination to the attack area (attackAreaRadius -
        // targetingReticleRadius from the initial target).
        var constraintRadius = _data.AttackAreaRadius - _data.TargetingReticleRadius;
        var overrideDelta = new FixVector3(
            _initialTargetPosition.X - _overrideTargetDestination.X,
            _initialTargetPosition.Y - _overrideTargetDestination.Y,
            Fix64.Zero);
        if (overrideDelta.Length() > constraintRadius)
        {
            var clamped = overrideDelta.NormalizedOrZero() * constraintRadius;
            _overrideTargetDestination = new FixVector3(
                _initialTargetPosition.X - clamped.X,
                _initialTargetPosition.Y - clamped.Y,
                _overrideTargetDestination.Z);
        }

        if (_status == GunshipStatus.Inserting && distanceToTarget < orbitalRadius)
        {
            _status = GunshipStatus.Orbiting;
            _orbitEscapeFrame = Context.CurrentFrame + _data.OrbitTime;
        }
    }

    /// <summary>GPL's ORBITING body: orbit-expiry check, the periodic (HowitzerFiringRate)
    /// target re-acquisition + howitzer volley gate, and the gattling aim wind - which GPL
    /// only advances (and only then increments/resets the howitzer-lag counter) while
    /// `tmp && gattling && gattling->testStatus(OBJECT_STATUS_IS_FIRING_WEAPON)`
    /// (SpectreGunshipUpdate.cpp:622) - i.e. a GattlingStrafeFXParticleSystem is configured,
    /// the contained gattling unit is alive, and that unit itself is actively firing its own
    /// weapon. See GattlingIsActivelyFiring().</summary>
    private void UpdateOrbiting()
    {
        if (Context.CurrentFrame >= _orbitEscapeFrame)
        {
            BeginDeparture();
            return;
        }

        if (_data.HowitzerFiringRate.Value == 0 || (Context.CurrentFrame.Value % _data.HowitzerFiringRate.Value) == 0)
        {
            ReacquireTargetAndMaybeFireHowitzer();
        }

        if (GattlingIsActivelyFiring())
        {
            UpdateGattlingAimWind();
        }
    }

    /// <summary>GPL's gate for the gattling-winding block: `tmp && gattling &&
    /// gattling->testStatus(OBJECT_STATUS_IS_FIRING_WEAPON)` (SpectreGunshipUpdate.cpp:622),
    /// where `tmp` is `data->m_gattlingStrafeFXParticleSystem` (a configured template, not a
    /// target object) and `gattling` is the contained unit resolved by <see cref="_gattlingId"/>.
    /// Because this port has no SimState AI-attack dispatch to command the gattling to fire
    /// (see file header), the contained unit can currently never report
    /// <see cref="ObjectStatus.IsFiringWeapon"/>, so this gate is almost always false - matching
    /// GPL's real behavior once the missing gattling-firing seam is filled in, rather than the
    /// always-on shortcut this port previously took.</summary>
    private bool GattlingIsActivelyFiring()
    {
        if (string.IsNullOrEmpty(_data.GattlingStrafeFXParticleSystem))
        {
            return false;
        }

        if (_gattlingId.IsInvalid)
        {
            return false;
        }

        var gattling = Context.GameLogic.GetObjectById(_gattlingId);
        return gattling != null && !gattling.IsDestroyed && gattling.TestStatus(ObjectStatus.IsFiringWeapon);
    }

    /// <summary>GPL's frame-modulator block: re-derive the aim point, try the reticle-radius
    /// search first, fall back to the whole attack area for non-human players (GPL: human
    /// players must babysit the reticle), and fire the howitzer when the wind counter has
    /// lagged long enough.</summary>
    private void ReacquireTargetAndMaybeFireHowitzer()
    {
        _positionToShootAt = _overrideTargetDestination;
        AcquiredTargetId = ObjectId.Invalid;

        var target = AcquireTargetNear(_overrideTargetDestination, _data.TargetingReticleRadius);

        if (target == null && GameObject.Owner is { IsHuman: false })
        {
            target = AcquireTargetNear(_initialTargetPosition, _data.AttackAreaRadius);
            if (target != null)
            {
                _positionToShootAt = SimTransformBridge.PullPosition(target);
            }
        }

        if (target != null)
        {
            AcquiredTargetId = target.Id;
        }

        if (_okToFireHowitzerCounter > _data.HowitzerFollowLag.Value)
        {
            FireHowitzer();
        }
    }

    /// <summary>GPL's PartitionFilterLiveMapEnemies + stealth + isFairDistanceFromShip search,
    /// nearest-first, within <paramref name="radius"/> of <paramref name="center"/> (see file
    /// header on the superset-query workaround for the missing position-centered partition
    /// seam).</summary>
    private GameObject AcquireTargetNear(in FixVector3 center, Fix64 radius)
    {
        var radiusSquared = radius * radius;

        GameObject best = null;
        var bestDistanceSquared = Fix64.Zero;

        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _acquireQueryRadius))
        {
            if (candidate.IsEffectivelyDead || candidate.IsOffMap != GameObject.IsOffMap)
            {
                continue;
            }

            if (DamagePipeline.GetRelationship(GameObject, candidate) != DamagePipeline.CombatRelationship.Enemies)
            {
                continue;
            }

            if (candidate.TestStatus(ObjectStatus.Stealthed)
                && !candidate.TestStatus(ObjectStatus.Detected)
                && !candidate.TestStatus(ObjectStatus.Disguised))
            {
                continue;
            }

            var candidatePosition = SimTransformBridge.PullPosition(candidate);
            var distanceSquared = DistanceSquared2D(center, candidatePosition);
            if (distanceSquared > radiusSquared)
            {
                continue;
            }

            if (!IsFairDistanceFromShip(candidatePosition))
            {
                continue;
            }

            if (best == null || distanceSquared < bestDistanceSquared)
            {
                best = candidate;
                bestDistanceSquared = distanceSquared;
            }
        }

        return best;
    }

    /// <summary>GPL isFairDistanceFromShip: reject candidates too close to the gunship itself
    /// (2D, GunshipOrbitRadius * 0.75).</summary>
    private bool IsFairDistanceFromShip(in FixVector3 targetPosition)
    {
        var gunshipPosition = SimTransformBridge.PullPosition(GameObject);
        var delta = new FixVector3(
            gunshipPosition.X - targetPosition.X,
            gunshipPosition.Y - targetPosition.Y,
            Fix64.Zero);
        return delta.Length() > _data.GunshipOrbitRadius * ThreeQuarters;
    }

    /// <summary>GPL's howitzer-fire block: a random-offset impact point around the current
    /// gattling aim point, delivered as direct damage to the acquired target (see file header
    /// deviation on the missing position-centered area-damage seam).</summary>
    private void FireHowitzer()
    {
        if (!_hasHowitzerDamage)
        {
            return;
        }

        var offset = _data.RandomOffsetForHowitzer;
        var impactX = _gattlingTargetPosition.X + Context.GameLogicRandom.NextFix64(-offset, offset);
        var impactY = _gattlingTargetPosition.Y + Context.GameLogicRandom.NextFix64(-offset, offset);
        HowitzerImpactPosition = new FixVector3(impactX, impactY, _gattlingTargetPosition.Z);

        if (AcquiredTargetId.IsInvalid)
        {
            return;
        }

        var target = Context.GameLogic.GetObjectById(AcquiredTargetId);
        if (target == null || target.IsEffectivelyDead)
        {
            return;
        }

        var input = new CombatDamageInput
        {
            SourceId = GameObject.Id,
            DamageType = _howitzerDamageType,
            DeathType = _howitzerDeathType,
            Amount = _howitzerDamageAmount,
        };
        DamagePipeline.DealDirectDamage(target, input);
    }

    /// <summary>Last howitzer volley's randomized impact point; test/inspector-only (GPL never
    /// persists the temp weapon's aim, only the state that drives it).</summary>
    internal FixVector3 HowitzerImpactPosition { get; private set; }

    /// <summary>GPL's gattling-winding block (SpectreGunshipUpdate.cpp:622-655), reached only
    /// when GattlingIsActivelyFiring() gates true (see UpdateOrbiting): step
    /// m_gattlingTargetPosition toward m_positionToShootAt by StrafingIncrement, or snap to it
    /// and grow the howitzer-lag counter once within one increment.</summary>
    private void UpdateGattlingAimWind()
    {
        var delta = new FixVector3(
            _positionToShootAt.X - _gattlingTargetPosition.X,
            _positionToShootAt.Y - _gattlingTargetPosition.Y,
            _positionToShootAt.Z - _gattlingTargetPosition.Z);
        var distance = delta.Length();

        if (distance < _data.StrafingIncrement)
        {
            _gattlingTargetPosition = _positionToShootAt;
            _okToFireHowitzerCounter++;
        }
        else
        {
            _okToFireHowitzerCounter = 0;
            _gattlingTargetPosition += delta.NormalizedOrZero() * _data.StrafingIncrement;
        }
    }

    /// <summary>GPL's orbit-expiry branch: cleanUp() + DEPARTING + disengageAndDepartAO() fused
    /// (the original calls cleanUp() twice on this path - once directly, once again inside
    /// disengageAndDepartAO - both destroy-gattling calls are idempotent here too).</summary>
    private void BeginDeparture()
    {
        DestroyGattling();
        _status = GunshipStatus.Departing;

        var gunshipPosition = SimTransformBridge.PullPosition(GameObject);
        var yaw = SimTransformBridge.PullYaw(GameObject);
        var direction = new FixVector3(FixTrig.Cos(yaw), FixTrig.Sin(yaw), Fix64.Zero);
        var exitPoint = gunshipPosition + direction * DepartureDistance;

        GameObject.FindBehavior<SimLocomotorUpdate>()?.SetTargetPosition(exitPoint, FullSpeedAhead);
    }

    /// <summary>GPL's DEPARTING branch: destroy self once truly off-map (see file header on
    /// GameObject.IsOffMap being a pre-existing, not-yet-wired engine flag).</summary>
    private void UpdateDeparture()
    {
        if (!GameObject.IsOffMap)
        {
            return;
        }

        Context.GameLogic.DestroyObject(GameObject);
        _status = GunshipStatus.Idle;
        DestroyGattling();
    }

    /// <summary>GPL's contain-and-disable spawn (initiateIntentToDoSpecialPower): always
    /// spawns a fresh gattling, bug-for-bug (see Activate's doc comment). Owned by the
    /// gunship's own player, standing at the gunship's position.</summary>
    private void SpawnGattling()
    {
        _gattlingId = ObjectId.Invalid;

        var definition = Context.Assets.GetObjectDefinition(_data.GattlingTemplateName);
        if (definition == null)
        {
            return;
        }

        var gattling = Context.GameLogic.CreateObjectAt(definition, GameObject.Owner, GameObject);
        if (gattling != null)
        {
            _gattlingId = gattling.Id;
        }
    }

    private void DestroyGattling()
    {
        if (_gattlingId.IsInvalid)
        {
            return;
        }

        var gattling = Context.GameLogic.GetObjectById(_gattlingId);
        if (gattling != null && !gattling.IsDestroyed)
        {
            Context.GameLogic.DestroyObject(gattling);
        }

        _gattlingId = ObjectId.Invalid;
    }

    /// <summary>2D squared distance - no sqrt needed for a radius compare.</summary>
    private static Fix64 DistanceSquared2D(in FixVector3 a, in FixVector3 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferEnum("Status", ref _status);
        xfer.XferFixVector3("InitialTargetPosition", ref _initialTargetPosition);
        xfer.XferFixVector3("OverrideTargetDestination", ref _overrideTargetDestination);
        xfer.XferFixVector3("SatellitePosition", ref _satellitePosition);
        xfer.XferFixVector3("GattlingTargetPosition", ref _gattlingTargetPosition);
        xfer.XferFixVector3("PositionToShootAt", ref _positionToShootAt);
        xfer.XferFrame("OrbitEscapeFrame", ref _orbitEscapeFrame);
        xfer.XferUInt("OkToFireHowitzerCounter", ref _okToFireHowitzerCounter);
        xfer.XferObjectId("GattlingId", ref _gattlingId);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================
[AddedIn(SageGame.CncGeneralsZeroHour)]
[SimDataAudited]
public sealed class SpectreGunshipUpdateModuleData : UpdateModuleData
{
    internal static SpectreGunshipUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<SpectreGunshipUpdateModuleData> FieldParseTable = new IniParseTable<SpectreGunshipUpdateModuleData>
    {
        { "GattlingStrafeFXParticleSystem", (parser, x) => x.GattlingStrafeFXParticleSystem = parser.ParseAssetReference() },
        // Retained for the day a SimState SpecialPowerModule exists (unused by sim code today
        // - see file header); parsed so authored INI stays valid.
        { "SpecialPowerTemplate", (parser, x) => x.SpecialPowerTemplate = parser.ParseAssetReference() },
        { "HowitzerWeaponTemplate", (parser, x) => x.HowitzerWeaponTemplate = parser.ParseWeaponTemplateReference() },
        { "GattlingTemplateName", (parser, x) => x.GattlingTemplateName = parser.ParseAssetReference() },
        { "RandomOffsetForHowitzer", (parser, x) => x.RandomOffsetForHowitzer = parser.ParseFix64() },
        { "TargetingReticleRadius", (parser, x) => x.TargetingReticleRadius = parser.ParseFix64() },
        { "OrbitInsertionSlope", (parser, x) => x.OrbitInsertionSlope = parser.ParseFix64() },
        { "GunshipOrbitRadius", (parser, x) => x.GunshipOrbitRadius = parser.ParseFix64() },
        // ms in INI, ceil-quantized to logic frames at parse (S5 wire boundary).
        { "HowitzerFiringRate", (parser, x) => x.HowitzerFiringRate = parser.ParseDurationLogicFrames() },
        { "HowitzerFollowLag", (parser, x) => x.HowitzerFollowLag = parser.ParseDurationLogicFrames() },
        { "OrbitTime", (parser, x) => x.OrbitTime = parser.ParseDurationLogicFrames() },
        { "StrafingIncrement", (parser, x) => x.StrafingIncrement = parser.ParseFix64() },
        { "AttackAreaRadius", (parser, x) => x.AttackAreaRadius = parser.ParseFix64() },

        // No render-decal system exists on this seam (client-presentation, unmodeled - see
        // file header); parsed and discarded so authored INI stays valid.
        { "AttackAreaDecal", (parser, x) => RadiusDecalTemplate.Parse(parser) },
        { "TargetingReticleDecal", (parser, x) => RadiusDecalTemplate.Parse(parser) },
    };

    public string GattlingStrafeFXParticleSystem { get; private set; }
    public string SpecialPowerTemplate { get; private set; }
    public OpenSage.Content.LazyAssetReference<WeaponTemplate> HowitzerWeaponTemplate { get; private set; }
    public string GattlingTemplateName { get; private set; }
    public Fix64 RandomOffsetForHowitzer { get; private set; }
    public Fix64 TargetingReticleRadius { get; private set; }
    public Fix64 OrbitInsertionSlope { get; private set; }
    public Fix64 GunshipOrbitRadius { get; private set; }
    public LogicFrameSpan HowitzerFiringRate { get; private set; }
    public LogicFrameSpan HowitzerFollowLag { get; private set; }
    public LogicFrameSpan OrbitTime { get; private set; }
    public Fix64 StrafingIncrement { get; private set; }
    public Fix64 AttackAreaRadius { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new SpectreGunshipUpdate(gameObject, gameEngine.SimContext, this);
    }
}
