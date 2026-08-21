// PointDefenseLaserUpdate - R12 port (Round-4 backlog; census: Update).
//
// Behavioral reference: generals-gpl GeneralsMD PointDefenseLaserUpdate.cpp/.h (semantics
// only; fresh code against the frozen contract). GPL update philosophy: rather than scanning
// every frame, the module scans infrequently (ScanRate frames) over a wide radius
// (ScanRange), remembers the "best" target between scans, and tracks/fires at only that
// target until it dies, leaves ScanRange, or the next scheduled scan.
//
// scanClosestTarget (GPL): walk objects within ScanRange, bucket them as index 0
// (PrimaryTargetTypes) or index 1 (SecondaryTargetTypes) - anything matching neither is
// skipped - reject non-enemies, reject stealthed-and-undetected-and-undisguised candidates,
// and reject ground targets when the weapon cannot hit the ground (the "AA-only laser vs.
// airfield" guard: keep the candidate only if it is airborne or the weapon's AntiMask has
// AntiGround). Among survivors, track the closest-in-firing-range candidate per bucket and,
// failing that, the closest-outside-range candidate (velocity-adjusted by
// PredictTargetVelocityFactor) per bucket. Primary-in-range wins over secondary-in-range wins
// over primary-out-of-range wins over secondary-out-of-range.
//
// fireWhenReady (GPL): re-derive m_inRange from the tracked target's live distance; on a
// was-in-range-now-isn't transition, drop the target and bias the next scan by
// GameLogicRandomValue(0,3) frames (0 forces an immediate rescan). Fire when in range and the
// weapon is ready; on a kill, drop the target and apply the same 0-3 rescan bias.
//
// DEVIATIONS (documented, not invented):
//   - GPL's actual weapon call allocates a brand-new disposable Weapon, force-fills its clip
//     (loadAmmoNow) and destroys it every shot (allocateNewWeapon + loadAmmoNow + fireWeapon +
//     deleteInstance, PointDefenseLaserUpdate.cpp:216-219). Because the clip is force-filled
//     and thrown away every single shot, it is never the same clip twice - ClipSize/
//     AutoReloadsClip/ClipReloadTime are structurally INERT for this module in retail; cadence
//     is governed purely by DelayBetweenShots (GPL m_nextShotAvailableInFrames). This port
//     mirrors that exactly with a plain LogicFrameSpan cooldown (_nextShotAvailableInFrames,
//     GPL-named) and no persistent ammo core - a prior revision wired a persistent SimWeapon
//     here (R12 review finding, blocker), which made ClipSize/ClipReloadTime genuinely gate
//     fire rate and diverge from the oracle for any INI with a finite ClipSize; that design is
//     reverted.
//   - GPL's out-of-range branch computes a velocity-predicted position (pos) but then
//     recomputes fDist from the ORIGINAL (me, other) pair, never reading pos back - the
//     prediction is computed and silently discarded (a retail dead-store bug). This port uses
//     the predicted position for the out-of-range distance compare, matching the plain reading
//     of "PredictTargetVelocityFactor" and this port's test packet ("predicts future position
//     ... and fires when predicted position enters firing range"); the fire gate itself still
//     compares the target's real, un-predicted position every frame (fireWhenReady, GPL-exact).
//   - Damage delivery: GPL's fireWeapon() drives the full WeaponTemplate effect-nugget chain
//     (FX, OCL, projectiles, ...) through the legacy Weapon/WeaponStore. Only the sim-visible
//     effect - the DamageNugget's direct hit - is delivered here, through DamagePipeline
//     (build-roadmap S1); FX/sound/OCL are client-presentation (S8) and unmodeled. A
//     WeaponTemplate with no DamageNugget fires (consumes ammo/cooldown) but deals no damage.
//   - AntiMask/isAirborneTarget: modeled directly (WeaponAntiFlagsExtensions.CanAttackObject's
//     table is already Fix64-free; GameObject.IsAirborne() is a bool predicate crossing the
//     float substrate on the far side of its own file, D-7), unlike EnemyNearUpdate's
//     unmodeled LOS/stealth gaps - this port's stealth gate below IS modeled (StealthedField
//     detected/disguised triad, GPL-exact).
//   - Distance: ISimContext.Partition has no getDistanceSquared seam (MobMemberSlavedUpdate
//     finding, R11/R12). This port pulls both positions through the same D-7 boundary
//     SimLocomotorUpdate/CrushDie already use (SimTransformBridge.PullPosition, F4-quantized
//     from the float transform) rather than requiring a locomotor, so it works for immobile
//     turret platforms too. Target velocity for prediction comes from the target's own
//     SimLocomotorUpdate.Physics.Velocity when it has one; targets without a landed locomotor
//     are treated as stationary for prediction purposes (a recorded gap, not a crash).
//
// Every mutable sim field appears in Xfer exactly once; tolerances are the field's
// conformance class at its declaration site.

using System.Linq;
using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class PointDefenseLaserUpdate : UpdateModule
{
    private readonly PointDefenseLaserUpdateModuleData _data;

    /// <summary>The resolved weapon template, or null when the module is misconfigured
    /// (no WeaponTemplate authored - GPL DEBUG_CRASHes; here the module just parks).</summary>
    private readonly WeaponTemplate _weaponTemplate;

    /// <summary>WeaponTemplate.AttackRange quantized once at construction (D-7 boundary).</summary>
    private readonly Fix64 _fireRangeSquared;

    private readonly WeaponAntiFlags _antiMask;

    /// <summary>The first DamageNugget's payload, cached once (all Fix64/enum already).</summary>
    private readonly bool _hasDamageNugget;
    private readonly Fix64 _damageAmount;
    private readonly DamageType _damageType;
    private readonly DeathType _deathType;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>GPL m_bestTargetID.</summary>
    private ObjectId _bestTargetId = ObjectId.Invalid;

    /// <summary>GPL m_inRange.</summary>
    private bool _inRange;

    /// <summary>GPL m_nextScanFrames: frames remaining before the next periodic scan.</summary>
    private LogicFrameSpan _nextScanFrames = LogicFrameSpan.Zero;

    /// <summary>GPL m_nextShotAvailableInFrames: the ONLY thing that gates fire cadence in
    /// retail for this module (see file header deviation note - ClipSize/ClipReloadTime are
    /// structurally inert because GPL's weapon is disposable-and-refilled every shot).</summary>
    private LogicFrameSpan _nextShotAvailableInFrames = LogicFrameSpan.Zero;

    public PointDefenseLaserUpdate(GameObject gameObject, ISimContext context, PointDefenseLaserUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _weaponTemplate = _data.WeaponTemplate?.Value;

        if (_weaponTemplate == null)
        {
            // GPL onObjectCreated() DEBUG_CRASHes on a missing weapon template; there is
            // nothing sim-meaningful this module can do without one, so it parks forever.
            SetWakeFrame(UpdateSleepTime.Forever);
            return;
        }

        var fireRange = CombatLegacyBridge.QuantizeAttackRange(_weaponTemplate);
        _fireRangeSquared = fireRange * fireRange;
        _antiMask = _weaponTemplate.AntiMask;

        var nugget = _weaponTemplate.Nuggets.OfType<DamageNugget>().FirstOrDefault();
        if (nugget != null)
        {
            _hasDamageNugget = true;
            _damageAmount = nugget.Damage;
            _damageType = nugget.DamageType;
            _deathType = nugget.DeathType;
        }

        // GPL ctor: setWakeFrame(UPDATE_SLEEP_NONE) with m_nextScanFrames = 0 and
        // m_nextShotAvailableInFrames = 0, so the very first Update() always scans immediately
        // (no stagger bias, unlike EnemyNearUpdate/StealthDetectorUpdate, whose own GPL ctors
        // explicitly draw one).
        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>Test/inspector-only view of the tracked target; not part of the save contract.</summary>
    internal ObjectId BestTargetId => _bestTargetId;

    /// <summary>Test/inspector-only view of the in-range flag; not part of the save contract.</summary>
    internal bool InRange => _inRange;

    /// <summary>Test/inspector-only view of the fire-cooldown counter; not part of the save
    /// contract.</summary>
    internal LogicFrameSpan NextShotAvailableInFrames => _nextShotAvailableInFrames;

    public override UpdateSleepTime Update()
    {
        if (_weaponTemplate == null)
        {
            return UpdateSleepTime.Forever;
        }

        if (GameObject.IsEffectivelyDead)
        {
            // GPL: "No more laser fo you."
            return UpdateSleepTime.Forever;
        }

        if (_nextScanFrames > LogicFrameSpan.Zero)
        {
            _nextScanFrames -= LogicFrameSpan.One;
            FireWhenReady();
            return UpdateSleepTime.None;
        }

        _nextScanFrames = _data.ScanRate;

        if (ScanClosestTarget())
        {
            // GPL: "1 frame can make a big difference so fire ASAP!"
            FireWhenReady();
        }

        return UpdateSleepTime.None;
    }

    /// <summary>GPL fireWhenReady().</summary>
    private void FireWhenReady()
    {
        var target = ResolveTrackedTarget();

        // GPL: "if (m_nextShotAvailableInFrames > 0) { m_nextShotAvailableInFrames--; return; }"
        // - this decrement/return happens unconditionally, BEFORE the target/in-range check,
        // and regardless of whether a target was even resolved above.
        if (_nextShotAvailableInFrames > LogicFrameSpan.Zero)
        {
            _nextShotAvailableInFrames -= LogicFrameSpan.One;
            return;
        }

        // GPL: "if (target && m_inRange)" - ResolveTrackedTarget only ever returns non-null
        // when that condition already holds (including the stale-target case below).
        if (target == null)
        {
            return;
        }

        if (!target.IsEffectivelyDead)
        {
            // GPL fires through a brand-new disposable Weapon every shot (allocateNewWeapon +
            // loadAmmoNow + fireWeapon + deleteInstance) - no persistent ammo/clip state, so
            // there is nothing here but the direct damage effect (file header deviation note)
            // and the DelayBetweenShots cooldown re-arm.
            if (_hasDamageNugget)
            {
                var input = new CombatDamageInput
                {
                    SourceId = GameObject.Id,
                    DamageType = _damageType,
                    DeathType = _deathType,
                    Amount = _damageAmount,
                };
                DamagePipeline.DealDirectDamage(target, input);
            }

            _nextShotAvailableInFrames = DrawDelayBetweenShots();
        }

        if (target.IsEffectivelyDead)
        {
            DropTargetAndBiasRescan();
        }
    }

    /// <summary>
    /// GPL <c>WeaponTemplate::getDelayBetweenShots</c> with the module's always-cleared
    /// WeaponBonus (rateOfFireMultiplier == 1, so the divide/floor step never applies):
    /// uniform draw in [min, max] frames, drawn only when min != max.
    /// </summary>
    private LogicFrameSpan DrawDelayBetweenShots()
    {
        var range = _weaponTemplate.CoolDownDelayBetweenShots;
        if (range.Min == range.Max)
        {
            return range.Min;
        }

        return new LogicFrameSpan((uint)Context.GameLogicRandom.Next((int)range.Min.Value, (int)range.Max.Value));
    }

    /// <summary>
    /// Re-derives m_inRange from the tracked target's live (real, un-predicted) distance and
    /// returns the target to fire at, or null when there is nothing to fire at this frame
    /// (GPL: the target lookup + in-range re-evaluation half of fireWhenReady()).
    /// </summary>
    private GameObject ResolveTrackedTarget()
    {
        if (_bestTargetId.IsInvalid)
        {
            return null;
        }

        var target = Context.GameLogic.GetObjectById(_bestTargetId);
        if (target == null || target.IsDestroyed)
        {
            _bestTargetId = ObjectId.Invalid;
            return null;
        }

        var distanceSquared = DistanceSquared2D(SimTransformBridge.PullPosition(GameObject), SimTransformBridge.PullPosition(target));

        if (distanceSquared <= _fireRangeSquared)
        {
            _inRange = true;
            return target;
        }

        if (_inRange)
        {
            // GPL: "We were in range last frame, but the target has moved out of firing
            // range, so re-evaluate by forcing a new scan." Bias-draw the next scan and
            // unconditionally drop the tracked ID - BUT m_inRange is NOT reset to false here,
            // and (except in the bias==0 sub-case) the local target pointer is NOT nulled
            // either. So on the ~75% of transitions where the bias draws nonzero, GPL's own
            // `if (target && m_inRange)` gate back in fireWhenReady() still passes and fires
            // one more shot at the target it just decided to drop, using this frame's stale
            // in-range/target pair (PointDefenseLaserUpdate.cpp:178-196). Only replicate the
            // "drop and null" outcome in the bias==0 sub-case, where GPL does both explicitly.
            var bias = new LogicFrameSpan((uint)Context.GameLogicRandom.Next(0, 3));
            _nextScanFrames = bias;
            _bestTargetId = ObjectId.Invalid;

            if (bias == LogicFrameSpan.Zero)
            {
                ScanClosestTarget();
                _nextScanFrames = _data.ScanRate;
                return null;
            }

            // Stale-target frame: _inRange is left exactly as it was (true), and we still
            // return the target we're about to drop next scan - matching retail's one extra
            // shot on the transition frame.
            return target;
        }

        _inRange = false;

        // GPL: "Set target to NULL so we don't shoot at it (might be out of range)."
        return null;
    }

    /// <summary>
    /// GPL's shared 0-3 frame rescan bias on target death: drop the tracked target, and when
    /// the draw is 0, rescan immediately.
    /// </summary>
    private void DropTargetAndBiasRescan()
    {
        _bestTargetId = ObjectId.Invalid;
        _inRange = false;

        var bias = new LogicFrameSpan((uint)Context.GameLogicRandom.Next(0, 3));
        _nextScanFrames = bias;

        if (bias == LogicFrameSpan.Zero)
        {
            ScanClosestTarget();
            _nextScanFrames = _data.ScanRate;
        }
    }

    /// <summary>GPL scanClosestTarget(). Returns true when a target was found (in or out of
    /// firing range).</summary>
    private bool ScanClosestTarget()
    {
        GameObject bestPrimaryInRange = null, bestSecondaryInRange = null;
        GameObject bestPrimaryOutOfRange = null, bestSecondaryOutOfRange = null;
        var closestPrimaryInRange = Fix64.Zero;
        var closestSecondaryInRange = Fix64.Zero;
        var closestPrimaryOutOfRange = Fix64.Zero;
        var closestSecondaryOutOfRange = Fix64.Zero;

        var myPosition = SimTransformBridge.PullPosition(GameObject);

        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _data.ScanRange))
        {
            if (candidate.IsEffectivelyDead)
            {
                // GPL's live-map partition filter excludes the dying/dead; without this a
                // corpse still resolvable by ObjectId would keep winning the scan forever
                // (it never re-fires - fireWhenReady's own dead-target check drops it again
                // every frame) and starve out the next real target.
                continue;
            }

            bool primary;
            var kindOf = candidate.Definition.KindOf;
            if (kindOf != null && _data.PrimaryTargetTypes != null && kindOf.Intersects(_data.PrimaryTargetTypes))
            {
                primary = true;
            }
            else if (kindOf != null && _data.SecondaryTargetTypes != null && kindOf.Intersects(_data.SecondaryTargetTypes))
            {
                primary = false;
            }
            else
            {
                // Not a valid target type.
                continue;
            }

            // GPL: "Borrow" the AA-only-laser check so it doesn't shoot planes on airports -
            // keep the candidate only if it is airborne, or the weapon can hit the ground.
            if (!candidate.IsAirborne() && (_antiMask & WeaponAntiFlags.AntiGround) == 0)
            {
                continue;
            }

            // GPL: "order matters: we want to know if I consider it to be an enemy."
            if (DamagePipeline.GetRelationship(GameObject, candidate) != DamagePipeline.CombatRelationship.Enemies)
            {
                continue;
            }

            if (candidate.TestStatus(ObjectStatus.Stealthed)
                && !candidate.TestStatus(ObjectStatus.Detected)
                && !candidate.TestStatus(ObjectStatus.Disguised))
            {
                // GPL: "We can't see it."
                continue;
            }

            var candidatePosition = SimTransformBridge.PullPosition(candidate);
            var distanceSquared = DistanceSquared2D(myPosition, candidatePosition);

            if (distanceSquared <= _fireRangeSquared)
            {
                if (primary)
                {
                    if (bestPrimaryInRange == null || distanceSquared < closestPrimaryInRange)
                    {
                        bestPrimaryInRange = candidate;
                        closestPrimaryInRange = distanceSquared;
                    }
                }
                else
                {
                    if (bestSecondaryInRange == null || distanceSquared < closestSecondaryInRange)
                    {
                        bestSecondaryInRange = candidate;
                        closestSecondaryInRange = distanceSquared;
                    }
                }

                continue;
            }

            // Outside firing range: predict where the target will be
            // (PredictTargetVelocityFactor * its current velocity) and rank candidates by the
            // PREDICTED distance - see file header deviation note on GPL's discarded prediction.
            var predictedPosition = candidatePosition;
            if (_data.PredictTargetVelocityFactor != Fix64.Zero && !candidate.IsKindOf(ObjectKinds.Immobile))
            {
                var velocity = candidate.FindBehavior<SimLocomotorUpdate>()?.Physics.Velocity ?? FixVector3.Zero;
                predictedPosition = candidatePosition + velocity * _data.PredictTargetVelocityFactor;
            }

            var predictedDistanceSquared = DistanceSquared2D(myPosition, predictedPosition);

            if (primary)
            {
                if (bestPrimaryOutOfRange == null || predictedDistanceSquared < closestPrimaryOutOfRange)
                {
                    bestPrimaryOutOfRange = candidate;
                    closestPrimaryOutOfRange = predictedDistanceSquared;
                }
            }
            else
            {
                if (bestSecondaryOutOfRange == null || predictedDistanceSquared < closestSecondaryOutOfRange)
                {
                    bestSecondaryOutOfRange = candidate;
                    closestSecondaryOutOfRange = predictedDistanceSquared;
                }
            }
        }

        // GPL priority: primary-in-range, secondary-in-range, primary-out-of-range,
        // secondary-out-of-range.
        if (bestPrimaryInRange != null)
        {
            _bestTargetId = bestPrimaryInRange.Id;
            _inRange = true;
            return true;
        }

        if (bestSecondaryInRange != null)
        {
            _bestTargetId = bestSecondaryInRange.Id;
            _inRange = true;
            return true;
        }

        if (bestPrimaryOutOfRange != null)
        {
            _bestTargetId = bestPrimaryOutOfRange.Id;
            _inRange = false;
            return true;
        }

        if (bestSecondaryOutOfRange != null)
        {
            _bestTargetId = bestSecondaryOutOfRange.Id;
            _inRange = false;
            return true;
        }

        // GPL: "Utter failure -- nothing on the scope."
        _bestTargetId = ObjectId.Invalid;
        _inRange = false;
        return false;
    }

    /// <summary>2D (FROM_CENTER_2D) squared distance - no sqrt needed anywhere this module
    /// only ever compares against a squared range.</summary>
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
        xfer.XferObjectId("BestTargetId", ref _bestTargetId);
        xfer.XferBool("InRange", ref _inRange);
        xfer.XferFrameSpan("NextScanFrames", ref _nextScanFrames, Tolerance.Exact); // frame count: Exact (A3)
        xfer.XferFrameSpan("NextShotAvailableInFrames", ref _nextShotAvailableInFrames, Tolerance.Exact); // frame count: Exact (A3)
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================
[SimDataAudited]
public sealed class PointDefenseLaserUpdateModuleData : UpdateModuleData
{
    internal static PointDefenseLaserUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<PointDefenseLaserUpdateModuleData> FieldParseTable = new IniParseTable<PointDefenseLaserUpdateModuleData>
    {
        { "WeaponTemplate", (parser, x) => x.WeaponTemplate = parser.ParseWeaponTemplateReference() },
        { "PrimaryTargetTypes", (parser, x) => x.PrimaryTargetTypes = parser.ParseEnumBitArray<ObjectKinds>() },
        { "SecondaryTargetTypes", (parser, x) => x.SecondaryTargetTypes = parser.ParseEnumBitArray<ObjectKinds>() },
        // ms in INI, ceil-quantized to logic frames at parse (S5 wire boundary; GPL
        // INI::parseDurationUnsignedInt).
        { "ScanRate", (parser, x) => x.ScanRate = parser.ParseDurationLogicFrames() },
        // Deterministic S3-query radius -> Fix64 (never float across the analyzer wall).
        { "ScanRange", (parser, x) => x.ScanRange = parser.ParseFix64() },
        { "PredictTargetVelocityFactor", (parser, x) => x.PredictTargetVelocityFactor = parser.ParseFix64() },
    };

    public LazyAssetReference<WeaponTemplate> WeaponTemplate { get; private set; }
    public BitArray<ObjectKinds> PrimaryTargetTypes { get; private set; }
    public BitArray<ObjectKinds> SecondaryTargetTypes { get; private set; }

    /// <summary>Frames between target scans (ms in INI, ceil-quantized at parse, S5).</summary>
    public LogicFrameSpan ScanRate { get; private set; }

    public Fix64 ScanRange { get; private set; }
    public Fix64 PredictTargetVelocityFactor { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new PointDefenseLaserUpdate(gameObject, gameEngine.SimContext, this);
    }
}
