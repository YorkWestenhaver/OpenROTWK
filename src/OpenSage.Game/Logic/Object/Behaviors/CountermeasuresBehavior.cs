// CountermeasuresBehavior - R12 port.
//
// Behavioral reference: generals-gpl GeneralsMD Include/GameLogic/Module/CountermeasuresBehavior.h
// and Source/GameLogic/Object/Behavior/CountermeasuresBehavior.cpp (semantics only; this is
// fresh code against the frozen contract). GPL shape: an UpdateModule + UpgradeMux +
// CountermeasuresBehaviorInterface. While the upgrade is active and the object is an airborne
// target: validates tracked flares each tick (drops any whose object can no longer be
// resolved), fires an initial volley after a reaction-delay timer (armed the moment an incoming
// missile is successfully "evaded"), then fires successive volleys every DelayBetweenVolleys
// frames until the supply is spent; auto-reloads on ReloadTime once the supply hits zero.
//
// reportMissileForCountermeasures (GPL): counts every incoming missile, rolls EvasionRate once
// per missile, and on success arms the reaction timer for the FIRST volley only (if no
// countermeasures are already active and the timer isn't already armed).
//
// calculateCountermeasureToDivertTo (GPL): translated VERBATIM, including two of its own
// quirks, not smoothed over:
//   - the backward scan from m_counterMeasures.end()-1 only advances toward older entries in
//     the ELSE branch (object not found); when the current slot resolves, the loop re-examines
//     the SAME slot for every remaining iteration instead of stepping back. In practice this
//     means the call almost always just returns the most-recently-launched still-alive flare,
//     and only starts genuinely scanning backward once it hits stale/missing entries.
//   - the `victim` parameter is accepted (interface fidelity) but never read in the GPL body;
//     distance is measured from THIS object (getObject()), not from the victim/missile.
//
// DEVIATIONS (documented, not invented):
//   CM-1 missile-side retarget. GPL's evasion-success branch also reaches into the missile's
//        ProjectileUpdateInterface (setFramesTillCountermeasureDiversionOccurs) to make the
//        incoming projectile actually divert onto a flare. The frozen contract's
//        IProjectileUpdate (Update/UpdateModule.cs) has no such hook, and no sim projectile
//        module exists yet to own one; adding one would mean introducing a new identifier into
//        a shared file outside this task's reservedNames. Every bookkeeping/timer effect of a
//        successful evasion roll (m_divertedMissiles, the reaction-frame arm) IS implemented
//        and tested; only the missile's own flight retargeting is unmodeled - the same class of
//        gap FireOCLAfterWeaponCooldownUpdate records for its missing weapon-firing seam.
//   CM-2 flare launch velocity. GPL's launchVolley() combines `transferVelocityTo` (a raw,
//        mass-independent m_vel += donor.m_vel) with `applyMotiveForce` (force accumulated into
//        m_accel, divided by mass, integrated into m_vel next tick). SimPhysics (the frozen
//        S2 physics core) exposes no raw-velocity mutator - only the force-mediated
//        ApplyForce/ApplyMotiveForce, both mass-divided - so this port folds the carried
//        aircraft velocity into the SAME ApplyMotiveForce call as the launch kick: both
//        components now scale by 1/flareMass together, instead of only the kick doing so as in
//        GPL. The ratio/angle/kick-magnitude math itself (the packet's tested surface) is
//        computed exactly per GPL's formula.
//   CM-3 isAirborneTarget. GPL's obj->isAirborneTarget() has no ported equivalent; this port
//        uses GameObject.IsAirborne() (a bool predicate crossing the float substrate on the far
//        side of its own file), the same D-7 bridge PointDefenseLaserUpdate already documents
//        and relies on for its own AA-only-laser airborne check.
//
// Every mutable sim field appears in the Xfer walk exactly once.

using System.Collections.Generic;
using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class CountermeasuresBehavior : UpdateModule, IUpgradeableModule
{
    private readonly CountermeasuresBehaviorModuleData _data;
    private readonly UpgradeLogic _upgradeLogic;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>GPL m_counterMeasures: object IDs of flares this behavior has launched.</summary>
    private readonly List<ObjectId> _counterMeasures = new();

    /// <summary>GPL m_availableCountermeasures: launches remaining before reload is needed.</summary>
    private uint _availableCountermeasures;

    /// <summary>GPL m_activeCountermeasures: currently-tracked (still resolvable) flares.</summary>
    private uint _activeCountermeasures;

    /// <summary>GPL m_divertedMissiles: missiles successfully evaded (CM-1: bookkeeping only).</summary>
    private uint _divertedMissiles;

    /// <summary>GPL m_incomingMissiles: total missiles ever reported against this object.</summary>
    private uint _incomingMissiles;

    /// <summary>GPL m_reactionFrame; zero is the GPL "not armed" sentinel, faithfully kept
    /// (including its frame-0 edge case, since GPL uses the identical UnsignedInt-0 sentinel).</summary>
    private LogicFrame _reactionFrame;

    /// <summary>GPL m_nextVolleyFrame.</summary>
    private LogicFrame _nextVolleyFrame;

    /// <summary>GPL m_reloadFrame; zero is the "reload not yet timed" sentinel.</summary>
    private LogicFrame _reloadFrame;

    public CountermeasuresBehavior(GameObject gameObject, ISimContext context, CountermeasuresBehaviorModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // GPL ctor: m_availableCountermeasures = numberOfVolleys * volleySize; everything else
        // starts at zero.
        _availableCountermeasures = (uint)(_data.NumberOfVolleys * _data.VolleySize);

        // The mux fires OnUpgradeTriggered from its ctor when StartsActive (GPL ctor itself
        // sets setWakeFrame(UPDATE_SLEEP_NONE) unconditionally; the upgrade gate is checked in
        // update(), not the ctor - matched below).
        _upgradeLogic = new UpgradeLogic(data.UpgradeData, OnUpgradeTriggered);

        SetWakeFrame(UpdateSleepTime.None);
    }

    // ---- test/inspector-only views (not part of the save contract by themselves - the
    // backing fields are; these are read-only projections of them) ----

    internal IReadOnlyList<ObjectId> CounterMeasures => _counterMeasures;
    internal uint AvailableCountermeasures => _availableCountermeasures;
    internal uint ActiveCountermeasures => _activeCountermeasures;
    internal uint DivertedMissiles => _divertedMissiles;
    internal uint IncomingMissiles => _incomingMissiles;
    internal LogicFrame ReactionFrame => _reactionFrame;
    internal LogicFrame NextVolleyFrame => _nextVolleyFrame;
    internal LogicFrame ReloadFrame => _reloadFrame;

    /// <summary>
    /// Test/inspector-only record of the most recent LaunchVolley()'s per-flare ratio/angle/
    /// kick-velocity computation (GPL-exact formula; see CM-2 for why the DELIVERY of the kick
    /// deviates). Not part of the save contract - rebuilt fresh every volley, never xfered.
    /// </summary>
    internal readonly List<(Fix64 Ratio, Fix64 Angle, FixVector3 KickVelocity)> LastVolleyLaunches = new();

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    private void OnUpgradeTriggered()
    {
        // GPL upgradeImplementation(): setWakeFrame(UPDATE_SLEEP_NONE).
        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>GPL isActive(): isUpgradeActive() (isAlreadyUpgraded()).</summary>
    public bool IsActive => _upgradeLogic.Triggered;

    /// <summary>
    /// GPL reportMissileForCountermeasures(Object *missile). See the file header (CM-1) for the
    /// documented gap: the missile's own retargeting is unreachable on the frozen contract, so
    /// this owns every OTHER effect of a successful evasion roll faithfully.
    /// </summary>
    public void ReportMissileForCountermeasures(GameObject missile)
    {
        if (missile == null)
        {
            return;
        }

        _incomingMissiles++;

        if (_availableCountermeasures + _activeCountermeasures == 0)
        {
            // No countermeasures to use at all - GPL skips the evasion roll entirely.
            return;
        }

        if (Context.GameLogicRandom.NextFix64(Fix64.Zero, Fix64.One) >= _data.EvasionRate)
        {
            return;
        }

        // This missile will be diverted! (missile-side retarget deferred - CM-1.)
        _divertedMissiles++;

        if (_activeCountermeasures == 0 && _reactionFrame == LogicFrame.Zero)
        {
            // We need to launch our first volley of countermeasures, but not immediately - set
            // up a reaction-delay timer to fake a reaction delay (GPL comment, verbatim intent).
            _reactionFrame = Context.CurrentFrame + _data.ReactionLaunchLatency;
        }
    }

    /// <summary>GPL calculateCountermeasureToDivertTo(const Object&amp; victim) - see the file
    /// header for the two GPL quirks preserved verbatim.</summary>
    public ObjectId CalculateCountermeasureToDivertTo(GameObject victim)
    {
        var iteratorMax = _data.VolleySize > 1 ? _data.VolleySize : 1;
        var closestDistanceSquared = Fix64.MaxValue;
        var closestFlareId = ObjectId.Invalid;

        var index = _counterMeasures.Count - 1;
        while (iteratorMax-- > 0)
        {
            if (index < 0)
            {
                break;
            }

            var candidate = Context.GameLogic.GetObjectById(_counterMeasures[index]);
            if (candidate != null)
            {
                var distanceSquared = DistanceSquared2D(
                    SimTransformBridge.PullPosition(GameObject),
                    SimTransformBridge.PullPosition(candidate));
                if (distanceSquared < closestDistanceSquared)
                {
                    closestDistanceSquared = distanceSquared;
                    closestFlareId = candidate.Id;
                }
                // GPL bug preserved verbatim: `it` is NOT decremented here, only in the else
                // branch below - see the file header note.
            }
            else
            {
                index--;
            }
        }

        return closestFlareId;
    }

    public void ReloadCountermeasures()
    {
        _availableCountermeasures = (uint)(_data.NumberOfVolleys * _data.VolleySize);
        _reloadFrame = LogicFrame.Zero;
    }

    public override UpdateSleepTime Update()
    {
        if (GameObject.IsEffectivelyDead)
        {
            return UpdateSleepTime.Forever;
        }
        if (!_upgradeLogic.Triggered)
        {
            return UpdateSleepTime.Forever;
        }

        var now = Context.CurrentFrame;

        // Validate all existing flares, cleaning them up as needed.
        for (var i = _counterMeasures.Count - 1; i >= 0; i--)
        {
            if (Context.GameLogic.GetObjectById(_counterMeasures[i]) == null)
            {
                _counterMeasures.RemoveAt(i);
                _activeCountermeasures--;
            }
        }

        if (GameObject.IsAirborne()) // CM-3: isAirborneTarget bridge, see file header.
        {
            // Handle flare volley launching (initial reaction, and continuation firing).
            if (_availableCountermeasures > 0)
            {
                // Deal with the initial volley, but wait until we are permitted to react.
                if (_reactionFrame != LogicFrame.Zero && _reactionFrame == now)
                {
                    LaunchVolley();
                    _nextVolleyFrame = now + _data.DelayBetweenVolleys;
                    _reactionFrame = LogicFrame.Zero;
                }

                // Handle subsequent volley launching.
                if (_nextVolleyFrame == now)
                {
                    LaunchVolley();
                    _nextVolleyFrame = now + _data.DelayBetweenVolleys;
                }
            }
        }

        // Handle auto-reloading (ReloadTime of zero means it's not possible to auto-reload).
        if (_availableCountermeasures == 0 && _data.ReloadTime > LogicFrameSpan.Zero)
        {
            if (_reloadFrame != LogicFrame.Zero)
            {
                if (_reloadFrame <= now)
                {
                    ReloadCountermeasures();
                }
            }
            else
            {
                _reloadFrame = now + _data.ReloadTime;
            }
        }

        return UpdateSleepTime.None;
    }

    /// <summary>GPL launchVolley(). See CM-2 (file header) for the documented velocity-delivery
    /// deviation; the ratio/angle/kick-magnitude formula itself is GPL-exact.</summary>
    private void LaunchVolley()
    {
        var flareTemplate = _data.FlareTemplate?.Value;
        if (flareTemplate == null)
        {
            return;
        }

        var volleySize = _data.VolleySize;
        var aircraftPhysics = GameObject.FindBehavior<SimLocomotorUpdate>()?.Physics;
        var yaw = aircraftPhysics?.Yaw ?? SimTransformBridge.PullYaw(GameObject);
        var facing = new FixVector2(FixTrig.Cos(yaw), FixTrig.Sin(yaw));
        var aircraftVelocity = aircraftPhysics?.Velocity ?? FixVector3.Zero;

        var speed = aircraftPhysics?.VelocityMagnitude() ?? Fix64.Zero;
        if (speed < Fix64.One)
        {
            speed = Fix64.FromDecimalLiteral("-10");
        }

        var now = Context.CurrentFrame;
        LastVolleyLaunches.Clear();
        for (var i = 0; i < volleySize; i++)
        {
            // Ratio between -1.0 and +1.0 (single-flare volleys go straight out the back).
            var ratio = volleySize != 1
                ? new Fix64(i) / new Fix64(volleySize - 1) * Fix64.Two - Fix64.One
                : Fix64.Zero;
            var angle = ratio * _data.VolleyArcAngle;

            var cos = FixTrig.Cos(angle);
            var sin = FixTrig.Sin(angle);
            var rotated = new FixVector2(
                facing.X * cos - facing.Y * sin,
                facing.X * sin + facing.Y * cos);

            var kickVelocity = new FixVector3(rotated.X, rotated.Y, Fix64.Zero) * (speed * _data.VolleyVelocityFactor);
            LastVolleyLaunches.Add((ratio, angle, kickVelocity));

            var flare = Context.GameLogic.CreateObjectAt(flareTemplate, GameObject.Owner, GameObject, yaw);
            if (flare == null)
            {
                continue;
            }

            // CM-2: the carried aircraft velocity (GPL transferVelocityTo) and the launch kick
            // (GPL applyMotiveForce) are folded into one ApplyMotiveForce call - see file header.
            var flarePhysics = flare.FindBehavior<SimLocomotorUpdate>()?.Physics;
            flarePhysics?.ApplyMotiveForce(aircraftVelocity + kickVelocity, now);

            _activeCountermeasures++;
            _availableCountermeasures--;
            _counterMeasures.Add(flare.Id);
        }
    }

    private static Fix64 DistanceSquared2D(in FixVector3 a, in FixVector3 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    // ---- the single walk: save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9). Frame indices/counters are integers on
    // both sides -> Tolerance.Exact (ruling A3).

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _upgradeLogic.Xfer(xfer);
        xfer.XferList("CounterMeasures", _counterMeasures, XferCounterMeasureId);
        xfer.XferUInt("AvailableCountermeasures", ref _availableCountermeasures);
        xfer.XferUInt("ActiveCountermeasures", ref _activeCountermeasures);
        xfer.XferUInt("DivertedMissiles", ref _divertedMissiles);
        xfer.XferUInt("IncomingMissiles", ref _incomingMissiles);
        xfer.XferFrame("ReactionFrame", ref _reactionFrame, Tolerance.Exact);
        xfer.XferFrame("NextVolleyFrame", ref _nextVolleyFrame, Tolerance.Exact);
        xfer.XferFrame("ReloadFrame", ref _reloadFrame, Tolerance.Exact);
    }

    private static void XferCounterMeasureId(IXfer xfer, ref ObjectId id)
    {
        xfer.XferObjectId("Id", ref id);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// GPL CountermeasuresBehaviorModuleData: an UpdateModuleData carrying the flare/volley tuning
// and an embedded UpgradeMuxData (TriggeredBy / ConflictsWith / ...), shared here through the
// same UpgradeLogicData child table every other upgrade-driven port uses.
// ============================================================================
[SimDataAudited]
public sealed class CountermeasuresBehaviorModuleData : UpdateModuleData
{
    internal static CountermeasuresBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<CountermeasuresBehaviorModuleData> FieldParseTable =
        new IniParseTableChild<CountermeasuresBehaviorModuleData, UpgradeLogicData>(x => x.UpgradeData, UpgradeLogicData.FieldParseTable)
        .Concat(new IniParseTable<CountermeasuresBehaviorModuleData>
        {
            { "FlareTemplateName", (parser, x) => x.FlareTemplate = parser.ParseObjectReference() },
            // Parsed (audited); GPL's own launchVolley() never reads m_flareBoneBaseName - it
            // spawns the flare at the object's center, not a named bone - so this stays
            // unconsumed vocabulary, same posture as GPL itself.
            { "FlareBoneBaseName", (parser, x) => x.FlareBoneBaseName = parser.ParseBoneName() },
            { "VolleySize", (parser, x) => x.VolleySize = parser.ParseInteger() },
            // GPL INI::parseAngleReal: degrees -> radians, quantized once at parse (S5).
            { "VolleyArcAngle", (parser, x) => x.VolleyArcAngle = parser.ParseAngleDegrees() },
            { "VolleyVelocityFactor", (parser, x) => x.VolleyVelocityFactor = parser.ParseFix64() },
            // ms in INI, ceil-quantized to logic frames at parse (S5; GPL parseDurationUnsignedInt).
            { "DelayBetweenVolleys", (parser, x) => x.DelayBetweenVolleys = parser.ParseDurationLogicFrames() },
            { "NumberOfVolleys", (parser, x) => x.NumberOfVolleys = parser.ParseInteger() },
            { "ReloadTime", (parser, x) => x.ReloadTime = parser.ParseDurationLogicFrames() },
            { "EvasionRate", (parser, x) => x.EvasionRate = parser.ParseFix64Percentage() },
            // Parsed (audited); GPL parses it but this .cpp's update()/reportMissileForCountermeasures
            // never reads m_mustReloadAtAirfield either - left as unconsumed vocabulary to match.
            { "MustReloadAtAirfield", (parser, x) => x.MustReloadAtAirfield = parser.ParseBoolean() },
            // Parsed (audited); consumed only by the CM-1 missile-side retarget this port defers.
            { "MissileDecoyDelay", (parser, x) => x.MissileDecoyDelay = parser.ParseDurationLogicFrames() },
            { "ReactionLaunchLatency", (parser, x) => x.ReactionLaunchLatency = parser.ParseDurationLogicFrames() },
        });

    /// <summary>The embedded UpgradeMux (GPL UpgradeMuxData): TriggeredBy / ConflictsWith /
    /// RequiresAllTriggers / StartsActive / ... shared with every other upgrade-driven module.</summary>
    public UpgradeLogicData UpgradeData { get; } = new();

    public LazyAssetReference<ObjectDefinition> FlareTemplate { get; private set; }
    public string FlareBoneBaseName { get; private set; }
    public int VolleySize { get; private set; }
    public Fix64 VolleyArcAngle { get; private set; }
    public Fix64 VolleyVelocityFactor { get; private set; } = Fix64.One;
    public LogicFrameSpan DelayBetweenVolleys { get; private set; }
    public int NumberOfVolleys { get; private set; }
    public LogicFrameSpan ReloadTime { get; private set; }
    public Fix64 EvasionRate { get; private set; }
    public bool MustReloadAtAirfield { get; private set; }
    public LogicFrameSpan MissileDecoyDelay { get; private set; }
    public LogicFrameSpan ReactionLaunchLatency { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new CountermeasuresBehavior(gameObject, gameEngine.SimContext, this);
    }
}
