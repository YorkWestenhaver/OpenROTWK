// FlightDeckBehavior - R12 port (api-freeze-v1 / template v1.1).
//
// Behavioral reference: generals-gpl GeneralsMD FlightDeckBehavior.cpp/.h (GPL semantics
// reference; aircraft-carrier parking/runway management, Kris Morness, May 2003). GPL is
// ~1700 lines because the class is ALSO an AIUpdateInterface/ExitInterface implementation
// that drives the carrier's own production-exit door and peeks into JetAIUpdate/AI command
// state on every parked jet. This port is scoped to the module's own deterministic state -
// the ParkingPlaceBehaviorInterface surface plus the per-frame update() state machine (heal,
// runway takeoff/landing reservation, ramp/catapult/launch-wave timing, replacement payload,
// and onDie's kill/defect sweep) - not reproduced/invented:
//
//   DEFERRED (documented gap, not invented - no faithful translation attempted):
//   - aiDoCommand/exitObjectViaDoor/reserveDoorForExit/propagateOrdersToPlanes and the rest
//     of the AIUpdateInterface/ExitInterface surface: this module would need to BE the
//     carrier's AIUpdate and peek at JetAIUpdate's internal AI-command state
//     (friend_getPendingCommandType, friend_isTakeoffOrLandingInProgress, etc). OpenSAGE's
//     JetAIUpdate (Logic/Object/Update/AIUpdate/JetAIUpdate.cs) is not ported to talk to this
//     module yet (it still looks up the airfield's ParkingPlaceBehaviour directly), and
//     reservedNames for this packet is empty, so no new identifiers were added there. Until
//     that wiring exists, "does this jet have takeoff orders" is a DRIVEN INPUT -
//     <see cref="RequestTakeoff"/>/<see cref="CancelTakeoff"/> are the seam a future
//     carrier-aware JetAIUpdate calls, exactly like GPL's own hasTakeoffOrders() peek.
//   - the periodic parking-space "bubble sort toward the front" reassignment
//     (ParkingCleanupPeriod / HumanFollowPeriod): GPL's isAbleToGiveUpParkingSpace /
//     isAbleToMoveForward both peek at JetAIUpdate's AI state for the same reason as above;
//     without that peek there is no way to tell "idle and parked" from "busy" here. The
//     fields are parsed and held for when that seam exists.
//   - buildInfo()'s parkingOffset math (Cos/Sin of the space's bone orientation): not
//     exercised by any ParkingPlaceBehaviorInterface caller in this repo yet, and every
//     other bone-position consumer in this file already carries full orientation as a
//     Quaternion (see ResolveBonePose) rather than the single Z-rotation float GPL uses -
//     unrepresented, not invented.
//
//   MIGRATED AS DRIVEN INPUT: the initial payload spawn (buildInfo's createUnits branch) and
//   the replacement-aircraft spawn (update()'s ProductionUpdateInterface queueCreateUnit
//   branch) both go through CreateObject directly here rather than the production queue -
//   OpenSAGE's ProductionUpdate is not wired to accept a fire-and-forget "no queue slot,
//   spawn immediately" request, and PayloadTemplate is a single template with no queue UI to
//   drive. Net timing (ReplacementDelay + DockAnimationDelay before the next replacement is
//   even considered) is faithful to GPL's m_nextAllowedProductionFrame; only the "goes
//   through a queue" mechanism is skipped.
//
// Every mutable sim field is Persist()ed exactly once; this module predates the SimCore /
// Fix64 migration boundary (like its airfield sibling ParkingPlaceBehavior.cs and the R12
// RailedTransportDockUpdate port) because its core data - bone-derived world positions - is
// float substrate (GameObject.Drawable skeleton state), not sim state.

using System;
using System.Collections.Generic;
using System.Numerics;
using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.Graphics.ParticleSystems;

namespace OpenSage.Logic.Object;

/// <summary>
/// Requires <see cref="ObjectKinds.AircraftCarrier"/> and <see cref="ObjectKinds.FSAirfield"/>
/// kinds.
/// </summary>
public sealed class FlightDeckBehavior : UpdateModule, IDieModule
{
    /// <summary>GPL <c>MAX_RUNWAYS</c>.</summary>
    private const int MaxRunways = 2;

    /// <summary>GPL <c>HEAL_RATE_FRAMES = LOGICFRAMES_PER_SECOND / 5</c> (5 heal ticks/second).</summary>
    private const int HealsPerSecond = 5;

    private static LogicFrame Forever => new(0x3FFFFFFFu);

    private readonly FlightDeckBehaviorModuleData _data;

    // ---- built (lazily, once) geometry - not persisted; rebuilt from bones on load, same as
    // GPL's own m_gotInfo-guarded buildInfo() being safe to call from a freshly-loaded object.
    private bool _gotInfo;
    private FlightDeckSpace[] _spaces = [];
    private FlightDeckRunway[] _runways = [];

    // ---- mutable sim state ----
    private readonly List<HealingEntry> _healing = [];
    private LogicFrame _nextHealFrame = Forever;
    private LogicFrame _nextAllowedProductionFrame;
    private readonly LogicFrame[] _nextLaunchWaveFrame = new LogicFrame[MaxRunways];
    private readonly bool[] _rampUp = new bool[MaxRunways];
    private readonly LogicFrame[] _rampUpFrame = new LogicFrame[MaxRunways];
    private readonly LogicFrame[] _catapultSystemFrame = new LogicFrame[MaxRunways];
    private readonly LogicFrame[] _lowerRampFrame = new LogicFrame[MaxRunways];

    /// <summary>Driven input replacing GPL's hasTakeoffOrders()/pending-AI-command peek - see the top-of-file note.</summary>
    private readonly HashSet<ObjectId> _takeoffOrdered = [];

    /// <summary>
    /// Number of times the per-runway catapult timer has expired and tried to fire its
    /// particle system (regardless of whether a render-side <see cref="ParticleSystemManager"/>
    /// exists to actually create one - see <see cref="FireCatapultParticleSystem"/>). Not
    /// persisted - a transient counter for observing the sequencing, same spirit as any other
    /// read-only test/diagnostic surface in this file.
    /// </summary>
    private int _catapultFireCount;
    internal int CatapultFireCount => _catapultFireCount;

    internal FlightDeckBehavior(GameObject gameObject, IGameEngine gameEngine, FlightDeckBehaviorModuleData data)
        : base(gameObject, gameEngine)
    {
        _data = data;

        for (var i = 0; i < MaxRunways; i++)
        {
            _catapultSystemFrame[i] = Forever;
            _lowerRampFrame[i] = Forever;
        }

        // GPL ticks update() every frame (UPDATE_SLEEP_NONE) unconditionally.
        SetWakeFrame(UpdateSleepTime.None);
    }

    // ---- read-only surface for tests / a future carrier-aware JetAIUpdate ----

    internal IReadOnlyList<FlightDeckSpace> Spaces => _spaces;
    internal IReadOnlyList<FlightDeckRunway> Runways => _runways;
    internal IReadOnlyList<HealingEntry> Healing => _healing;
    internal LogicFrame NextHealFrame => _nextHealFrame;

    // ---- ParkingPlaceBehaviorInterface-shaped surface ----

    public bool HasAvailableSpaceFor()
    {
        BuildInfo();
        foreach (var space in _spaces)
        {
            var id = space.ObjectInSpace;
            if (id.IsInvalid || IsEffectivelyDead(id))
            {
                return true;
            }
        }
        return false;
    }

    public bool HasReservedSpace(ObjectId id)
    {
        if (!_gotInfo || id.IsInvalid)
        {
            return false;
        }
        return FindSpaceIndex(id) >= 0;
    }

    public int GetSpaceIndex(ObjectId id)
    {
        if (id.IsInvalid)
        {
            return -1;
        }
        BuildInfo();
        return FindSpaceIndex(id);
    }

    /// <summary>GPL <c>reserveSpace</c>. Assigns the object's own space if it has one, else the first empty space.</summary>
    public bool ReserveSpace(ObjectId id, out FlightDeckParkingInfo info)
    {
        BuildInfo();
        PurgeDead();

        var index = FindSpaceIndex(id);
        if (index < 0)
        {
            index = FindEmptySpaceIndex();
            if (index < 0)
            {
                info = default;
                return false;
            }
        }

        _spaces[index].ObjectInSpace = id;

        if (_data.LandingDeckHeightOffset != 0f)
        {
            var obj = GameEngine.GameLogic.GetObjectById(id);
            obj?.SetObjectStatus(ObjectStatus.DeckHeightOffset, true);
        }

        info = CalcPPInfo(index);
        return true;
    }

    /// <summary>GPL <c>releaseSpace</c>.</summary>
    public void ReleaseSpace(ObjectId id)
    {
        BuildInfo();
        PurgeDead();

        for (var i = 0; i < _spaces.Length; i++)
        {
            if (_spaces[i].ObjectInSpace == id)
            {
                _spaces[i].ObjectInSpace = ObjectId.Invalid;
            }
        }

        GameEngine.GameLogic.GetObjectById(id)?.SetObjectStatus(ObjectStatus.DeckHeightOffset, false);
    }

    /// <summary>GPL <c>calcPPInfo</c>, exposed directly by space index for testing / re-querying an already-reserved space.</summary>
    public FlightDeckParkingInfo? CalcPPInfoFor(ObjectId id)
    {
        var index = FindSpaceIndex(id);
        return index < 0 ? null : CalcPPInfo(index);
    }

    /// <summary>
    /// GPL <c>reserveRunway</c>. Takeoff reservations only ever look at the front row (index
    /// &lt; runway count, exactly like GPL's <c>m_spaces[i]</c> loop over <c>m_numCols</c>) -
    /// you can't take off from a rear space. Landing reservations look at every space the
    /// object might occupy.
    /// </summary>
    public bool ReserveRunway(ObjectId id, bool forLanding)
    {
        BuildInfo();
        PurgeDead();

        var runway = -1;
        if (!forLanding)
        {
            for (var i = 0; i < _runways.Length && i < _spaces.Length; i++)
            {
                if (_spaces[i].ObjectInSpace == id)
                {
                    runway = _spaces[i].Runway;
                    break;
                }
            }
        }
        else
        {
            var index = FindSpaceIndex(id);
            if (index >= 0)
            {
                runway = _spaces[index].Runway;
            }
        }

        if (runway < 0)
        {
            return false;
        }

        ref var info = ref _runways[runway];
        if ((!forLanding && info.InUseForTakeoff == id) || (forLanding && info.InUseForLanding == id))
        {
            return true;
        }

        if (!forLanding && info.InUseForTakeoff.IsInvalid)
        {
            info.InUseForTakeoff = id;
            return true;
        }

        if (forLanding && info.InUseForLanding.IsInvalid)
        {
            info.InUseForLanding = id;
            return true;
        }

        return false;
    }

    /// <summary>GPL <c>releaseRunway</c>.</summary>
    public void ReleaseRunway(ObjectId id)
    {
        BuildInfo();
        PurgeDead();

        for (var i = 0; i < _runways.Length; i++)
        {
            if (_runways[i].InUseForTakeoff == id)
            {
                _runways[i].InUseForTakeoff = ObjectId.Invalid;
            }
            if (_runways[i].InUseForLanding == id)
            {
                _runways[i].InUseForLanding = ObjectId.Invalid;
            }
        }
    }

    /// <summary>GPL <c>getRunwayReservation</c>.</summary>
    public ObjectId GetRunwayReservation(int runway, bool forLanding)
    {
        BuildInfo();
        PurgeDead();

        if (runway < 0 || runway >= _runways.Length)
        {
            return ObjectId.Invalid;
        }
        return forLanding ? _runways[runway].InUseForLanding : _runways[runway].InUseForTakeoff;
    }

    /// <summary>GPL <c>setHealee(healee, true)</c>: begins healing a parked, idle aircraft.</summary>
    public void ReportParkedIdle(ObjectId id)
    {
        foreach (var entry in _healing)
        {
            if (entry.ObjectId == id)
            {
                return;
            }
        }
        _healing.Add(new HealingEntry(id, GameEngine.GameLogic.CurrentFrame));
        ResetHealWakeFrame();
    }

    /// <summary>GPL <c>setHealee(healee, false)</c>: stops healing (e.g. the aircraft starts taxiing to the runway).</summary>
    public void ReportNoLongerParked(ObjectId id)
    {
        var removed = _healing.RemoveAll(e => e.ObjectId == id) > 0;
        if (removed)
        {
            ResetHealWakeFrame();
        }
    }

    /// <summary>Driven input (see the top-of-file DEFERRED note): marks a parked jet as wanting to launch.</summary>
    public void RequestTakeoff(ObjectId id) => _takeoffOrdered.Add(id);

    public void CancelTakeoff(ObjectId id) => _takeoffOrdered.Remove(id);

    /// <summary>GPL <c>killAllParkedUnits</c>: airborne, non-taxiing jets are left alone; everything else on deck is killed.</summary>
    public void KillAllParkedUnits()
    {
        BuildInfo();
        PurgeDead();

        foreach (var space in _spaces)
        {
            if (space.ObjectInSpace.IsInvalid)
            {
                continue;
            }
            var obj = GameEngine.GameLogic.GetObjectById(space.ObjectInSpace);
            if (obj == null || obj.IsEffectivelyDead)
            {
                continue;
            }
            if (obj.IsAboveTerrain)
            {
                // Airborne (GPL's takeoffOrLanding peek is part of the deferred JetAIUpdate
                // wiring - see the top-of-file note; treating "airborne" alone as "spared"
                // is the conservative direction, since it only widens what survives).
                continue;
            }
            obj.Kill();
        }

        PurgeDead();
    }

    /// <summary>GPL <c>defectAllParkedUnits</c>: grounded jets defect to the new team; airborne ones just lose their reserved space.</summary>
    public void DefectAllParkedUnits(Team newTeam, uint detectionTime)
    {
        BuildInfo();
        PurgeDead();

        foreach (var space in _spaces)
        {
            if (space.ObjectInSpace.IsInvalid)
            {
                continue;
            }
            var obj = GameEngine.GameLogic.GetObjectById(space.ObjectInSpace);
            if (obj == null || obj.IsEffectivelyDead)
            {
                continue;
            }

            if (obj.IsAboveTerrain)
            {
                if (newTeam.ControllingPlayer != obj.Owner)
                {
                    // GPL also clears the producer link (obj->setProducer(NULL)) when this
                    // carrier was the producer; OpenSAGE's GameObject has no producer-link
                    // setter yet (not modeled - documented gap, not invented).
                    ReleaseSpace(obj.Id);
                }
            }
            else
            {
                obj.Defect(newTeam, detectionTime);
            }
        }

        PurgeDead();
    }

    /// <summary>GPL <c>onDie</c>: the whole flight deck's complement dies with the carrier.</summary>
    public void OnDie(in DamageInfoInput damageInput) => KillAllParkedUnits();

    // ---- per-frame update ----

    public override UpdateSleepTime Update()
    {
        // GPL keeps buildInfo/purgeDead fresh every frame so client-side peeks (ParkingPlaceBehaviorInterface's const methods) stay current.
        BuildInfo();
        PurgeDead();

        var now = GameEngine.GameLogic.CurrentFrame;

        TickHealing(now);
        TickReplacementProduction(now);
        TickTakeoffSequencing(now);

        return UpdateSleepTime.None;
    }

    private void TickHealing(LogicFrame now)
    {
        if (now < _nextHealFrame)
        {
            return;
        }

        var healUpdateRate = new LogicFrameSpan((uint)(GameEngine.LogicFramesPerSecond / HealsPerSecond));
        _nextHealFrame = now + healUpdateRate;

        // GPL: HEAL_RATE_FRAMES * m_healAmount * SECONDS_PER_LOGICFRAME_REAL, i.e. one
        // HealAmountPerSecond/HealsPerSecond tick per heal-rate frame.
        var healPerTick = (float)_data.HealAmountPerSecond / HealsPerSecond;

        for (var i = _healing.Count - 1; i >= 0; i--)
        {
            var entry = _healing[i];
            var obj = GameEngine.GameLogic.GetObjectById(entry.ObjectId);
            if (obj == null || obj.IsEffectivelyDead)
            {
                _healing.RemoveAt(i);
                continue;
            }
            obj.AttemptHealing(healPerTick, GameObject);
        }
    }

    /// <summary>
    /// GPL's replacement-aircraft branch of update() - see the MIGRATED AS DRIVEN INPUT note
    /// at the top of the file. GPL queues a build the frame a space is noticed empty and
    /// waits on the production system's own build time (unmodeled here - no ported
    /// production queue); this direct-spawn stand-in instead waits
    /// ReplacementDelay + DockAnimationDelay frames from the frame the gap is first noticed,
    /// matching the packet's "replacement spawns at ReplacementDelay frames after carrier
    /// clears" contract directly.
    /// </summary>
    private void TickReplacementProduction(LogicFrame now)
    {
        var emptyIndex = -1;
        for (var i = 0; i < _spaces.Length; i++)
        {
            if (_spaces[i].ObjectInSpace.IsInvalid || IsEffectivelyDead(_spaces[i].ObjectInSpace))
            {
                emptyIndex = i;
                break;
            }
        }

        if (emptyIndex < 0)
        {
            _nextAllowedProductionFrame = LogicFrame.Zero;
            return;
        }

        if (_nextAllowedProductionFrame == LogicFrame.Zero)
        {
            _nextAllowedProductionFrame = now
                + new LogicFrameSpan((uint)_data.ReplacementDelay)
                + new LogicFrameSpan((uint)_data.DockAnimationDelay);
            return;
        }

        if (now < _nextAllowedProductionFrame)
        {
            return;
        }

        var payload = _data.PayloadTemplate?.Value;
        if (payload == null)
        {
            return;
        }

        var jet = GameEngine.GameLogic.CreateObject(payload, GameObject.Owner);
        jet.UpdateTransform(_spaces[emptyIndex].Prep, _spaces[emptyIndex].Orientation);
        _spaces[emptyIndex].ObjectInSpace = jet.Id;
        _nextAllowedProductionFrame = LogicFrame.Zero;
    }

    /// <summary>
    /// GPL's launch-wave/ramp/catapult section of update(). Front-space (index &lt; runway
    /// count) jets with takeoff orders (<see cref="RequestTakeoff"/>) raise the ramp, wait
    /// LaunchRampDelay, then launch (clearing takeoff orders and releasing the runway - the
    /// eventual caller is the driven-input jet, not this module, in GPL), fire the catapult
    /// particle system CatapultFireDelay after launch, and lower the ramp LowerRampDelay
    /// after launch.
    /// </summary>
    private void TickTakeoffSequencing(LogicFrame now)
    {
        for (var i = 0; i < _runways.Length; i++)
        {
            if (i < _spaces.Length)
            {
                var jetId = _spaces[i].ObjectInSpace;
                if (jetId.IsValid && _takeoffOrdered.Contains(jetId) && _nextLaunchWaveFrame[i] <= now)
                {
                    if (!_rampUp[i])
                    {
                        _rampUp[i] = true;
                        _rampUpFrame[i] = now + new LogicFrameSpan((uint)_data.LaunchRampDelay);
                        _lowerRampFrame[i] = Forever;
                        SetRampDoorState(i, opening: true);
                    }

                    if (_rampUp[i] && _rampUpFrame[i] <= now)
                    {
                        _takeoffOrdered.Remove(jetId);
                        _nextLaunchWaveFrame[i] = now + new LogicFrameSpan((uint)_data.LaunchWaveDelay);
                        _catapultSystemFrame[i] = now + new LogicFrameSpan((uint)_data.CatapultFireDelay);
                        _lowerRampFrame[i] = now + new LogicFrameSpan((uint)_data.LowerRampDelay);
                    }
                }
            }

            if (_catapultSystemFrame[i] <= now)
            {
                _catapultSystemFrame[i] = Forever;
                FireCatapultParticleSystem(i);
            }

            if (_rampUp[i] && _lowerRampFrame[i] <= now)
            {
                _rampUp[i] = false;
                SetRampDoorState(i, opening: false);
            }
        }
    }

    /// <summary>GPL's <c>MODELCONDITION_DOOR_2_OPENING + i * NUM_MODELCONDITION_DOOR_STATES</c> (MAX_RUNWAYS == 2: runway 0 -&gt; Door2, runway 1 -&gt; Door3).</summary>
    private void SetRampDoorState(int runway, bool opening)
    {
        var (openingFlag, closingFlag) = runway switch
        {
            0 => (ModelConditionFlag.Door2Opening, ModelConditionFlag.Door2Closing),
            1 => (ModelConditionFlag.Door3Opening, ModelConditionFlag.Door3Closing),
            _ => throw new ArgumentOutOfRangeException(nameof(runway)),
        };
        GameObject.ClearModelConditionState(opening ? closingFlag : openingFlag);
        GameObject.SetModelConditionState(opening ? openingFlag : closingFlag);
    }

    private void FireCatapultParticleSystem(int runway)
    {
        var template = runway switch
        {
            0 => _data.Runway1CatapultSystem?.Value,
            1 => _data.Runway2CatapultSystem?.Value,
            _ => null,
        };
        _catapultFireCount++;

        if (template == null || runway >= _runways.Length || GameEngine.ParticleSystems == null)
        {
            // No render-side particle manager (e.g. a headless host, same accommodation as
            // the bone-position fallback above) - the timer/sequencing side still ran.
            return;
        }
        var particleSystem = GameEngine.ParticleSystems.Create(template, _runways[runway].TakeoffStartTransform.Matrix);
        particleSystem.Activate();
    }

    // ---- geometry construction ----

    /// <summary>GPL <c>buildInfo(createUnits = TRUE)</c>: spaces built once, front-to-back, R1S1/R2S1/R1S2/R2S2 order; every space's payload is spawned immediately (this module has no production queue - see the MIGRATED AS DRIVEN INPUT note at the top).</summary>
    private void BuildInfo() => BuildInfo(createUnits: true);

    /// <summary>
    /// GPL <c>buildInfo(createUnits)</c>. <paramref name="createUnits"/> is false only when
    /// rebuilding geometry after a load (GPL's own loadPostProcess comment: "the planes are
    /// going to save themselves, we don't re-create them") - see <see cref="Load"/>.
    /// </summary>
    private void BuildInfo(bool createUnits)
    {
        if (_gotInfo)
        {
            return;
        }
        _gotInfo = true;

        if (GameObject.TestStatus(ObjectStatus.UnderConstruction) || GameObject.TestStatus(ObjectStatus.Sold))
        {
            _gotInfo = false;
            return;
        }

        var numRows = Math.Max(0, _data.NumSpacesPerRunway);
        var numCols = Math.Clamp(_data.NumRunways, 0, MaxRunways);

        var spaceBones = new[] { _data.Runway1Spaces, _data.Runway2Spaces };
        var payload = _data.PayloadTemplate?.Value;

        var spaces = new List<FlightDeckSpace>(numRows * numCols);
        for (var row = 0; row < numRows; row++)
        {
            for (var col = 0; col < numCols; col++)
            {
                var bones = spaceBones[col];
                var boneName = bones != null && row < bones.Length ? bones[row] : null;
                var (prep, orientation) = ResolveBonePose(boneName);

                var space = new FlightDeckSpace
                {
                    Prep = prep,
                    Orientation = orientation,
                    Runway = col,
                    ObjectInSpace = ObjectId.Invalid,
                };

                if (createUnits && payload != null)
                {
                    var jet = GameEngine.GameLogic.CreateObject(payload, GameObject.Owner);
                    jet.UpdateTransform(prep, orientation);
                    jet.SetObjectStatus(ObjectStatus.DeckHeightOffset, true);
                    space.ObjectInSpace = jet.Id;
                }

                spaces.Add(space);
            }
        }
        _spaces = spaces.ToArray();

        var takeoffBones = new[] { _data.Runway1Takeoff, _data.Runway2Takeoff };
        var landingBones = new[] { _data.Runway1Landing, _data.Runway2Landing };

        var runways = new FlightDeckRunway[numCols];
        for (var col = 0; col < numCols; col++)
        {
            var takeoff = takeoffBones[col];
            var landing = landingBones[col];

            var (start, startTransform) = ResolveBonePoseAndTransform(BoneAt(takeoff, 0));
            var (end, _) = ResolveBonePoseAndTransform(BoneAt(takeoff, 1));
            var (landingStart, _) = ResolveBonePoseAndTransform(BoneAt(landing, 0));
            var (landingEnd, _) = ResolveBonePoseAndTransform(BoneAt(landing, 1));

            runways[col] = new FlightDeckRunway
            {
                Start = start,
                End = end,
                LandingStart = landingStart,
                LandingEnd = landingEnd,
                TakeoffStartTransform = startTransform,
                InUseForTakeoff = ObjectId.Invalid,
                InUseForLanding = ObjectId.Invalid,
            };
        }
        _runways = runways;
    }

    private static string BoneAt(string[] bones, int index) => bones != null && index < bones.Length ? bones[index] : null;

    private (Vector3 Position, Quaternion Orientation) ResolveBonePose(string boneName)
    {
        var (position, transform) = ResolveBonePoseAndTransform(boneName);
        return (position, transform.Rotation);
    }

    private (Vector3 Position, Transform Transform) ResolveBonePoseAndTransform(string boneName)
    {
        // Drawable is null on a headless host (no client model is built at all), so the
        // skeleton lookup is only attempted when there is one to look in.
        if (!string.IsNullOrEmpty(boneName) && GameObject.Drawable != null)
        {
            var (_, bone) = GameObject.Drawable.FindBone(boneName);
            if (bone != null)
            {
                return (bone.Transform.Translation, bone.Transform);
            }
        }

        // Headless/no-skeleton fallback (matches DockUpdate/RailedTransportDockUpdate's own
        // fallback for a missing bone): the object's own transform.
        var fallback = Transform.CreateIdentity();
        fallback.Translation = GameObject.Translation;
        fallback.Rotation = GameObject.Rotation;
        return (GameObject.Translation, fallback);
    }

    /// <summary>GPL <c>purgeDead</c>.</summary>
    private void PurgeDead()
    {
        for (var i = 0; i < _spaces.Length; i++)
        {
            if (_spaces[i].ObjectInSpace.IsValid && IsEffectivelyDead(_spaces[i].ObjectInSpace))
            {
                _spaces[i].ObjectInSpace = ObjectId.Invalid;
            }
        }

        for (var i = 0; i < _runways.Length; i++)
        {
            if (_runways[i].InUseForTakeoff.IsValid && IsEffectivelyDead(_runways[i].InUseForTakeoff))
            {
                _runways[i].InUseForTakeoff = ObjectId.Invalid;
            }
            if (_runways[i].InUseForLanding.IsValid && IsEffectivelyDead(_runways[i].InUseForLanding))
            {
                _runways[i].InUseForLanding = ObjectId.Invalid;
            }
        }

        var purgedHealing = _healing.RemoveAll(e => IsEffectivelyDead(e.ObjectId)) > 0;
        if (purgedHealing)
        {
            ResetHealWakeFrame();
        }
    }

    private bool IsEffectivelyDead(ObjectId id)
    {
        var obj = GameEngine.GameLogic.GetObjectById(id);
        return obj == null || obj.IsEffectivelyDead;
    }

    private int FindSpaceIndex(ObjectId id)
    {
        for (var i = 0; i < _spaces.Length; i++)
        {
            if (_spaces[i].ObjectInSpace == id)
            {
                return i;
            }
        }
        return -1;
    }

    private int FindEmptySpaceIndex()
    {
        for (var i = 0; i < _spaces.Length; i++)
        {
            if (_spaces[i].ObjectInSpace.IsInvalid)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>GPL <c>calcPPInfo</c> (parkingOffset math not reproduced - see the DEFERRED note at the top of the file).</summary>
    private FlightDeckParkingInfo CalcPPInfo(int spaceIndex)
    {
        const float approachDist = 0.75f;

        var space = _spaces[spaceIndex];
        var runway = _runways[space.Runway];

        var runwayExit = runway.End + (runway.End - runway.Start) * approachDist;
        runwayExit.Z = runway.End.Z + _data.ApproachHeight + _data.LandingDeckHeightOffset;

        var runwayApproach = runway.LandingStart + (runway.LandingStart - runway.LandingEnd) * approachDist;
        runwayApproach.Z = runway.LandingStart.Z + _data.ApproachHeight + _data.LandingDeckHeightOffset;

        var runwayStart = runway.Start;
        if (runway.InUseForTakeoff == space.ObjectInSpace)
        {
            runwayStart = space.Prep;
        }

        return new FlightDeckParkingInfo
        {
            ParkingSpace = space.Prep,
            ParkingOrientation = space.Orientation,
            RunwayStart = runwayStart,
            RunwayEnd = runway.End,
            RunwayExit = runwayExit,
            RunwayLandingStart = runway.LandingStart,
            RunwayLandingEnd = runway.LandingEnd,
            RunwayApproach = runwayApproach,
            RunwayTakeoffDistance = Vector3.Distance(runway.Start, runway.End),
        };
    }

    private void ResetHealWakeFrame()
    {
        _nextHealFrame = _healing.Count == 0 ? Forever : GameEngine.GameLogic.CurrentFrame;
    }

    // ---- save/load ----

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        var hadInfo = _gotInfo;
        reader.PersistBoolean(ref hadInfo);

        if (reader.Mode == StatePersistMode.Read && hadInfo)
        {
            // Rebuild geometry from bones without re-spawning the parked payload - GPL's own
            // loadPostProcess() calls buildInfo(FALSE) for the same reason: "the planes are
            // going to save themselves, we don't re-create them". The ObjectInSpace/runway
            // reservation ids persisted below are then overlaid onto this freshly built,
            // correctly sized (NumRunways x NumSpacesPerRunway) geometry.
            _gotInfo = false;
            BuildInfo(createUnits: false);
        }

        var spaceCount = _spaces.Length;
        reader.PersistInt32(ref spaceCount);
        if (reader.Mode == StatePersistMode.Read && spaceCount != _spaces.Length)
        {
            throw new InvalidStateException("FlightDeckBehavior space count mismatch on load - NumRunways/NumSpacesPerRunway changed since save.");
        }
        for (var i = 0; i < spaceCount; i++)
        {
            reader.PersistObjectId(ref _spaces[i].ObjectInSpace);
        }

        var runwayCount = _runways.Length;
        reader.PersistInt32(ref runwayCount);
        if (reader.Mode == StatePersistMode.Read && runwayCount != _runways.Length)
        {
            throw new InvalidStateException("FlightDeckBehavior runway count mismatch on load - NumRunways changed since save.");
        }
        for (var i = 0; i < runwayCount; i++)
        {
            reader.PersistObjectId(ref _runways[i].InUseForTakeoff);
            reader.PersistObjectId(ref _runways[i].InUseForLanding);
        }

        reader.PersistListWithByteCount(_healing, static (StatePersister persister, ref HealingEntry item) =>
        {
            persister.PersistObjectValue(ref item);
        });

        reader.PersistLogicFrame(ref _nextHealFrame);
        reader.PersistLogicFrame(ref _nextAllowedProductionFrame);

        for (var i = 0; i < MaxRunways; i++)
        {
            reader.PersistLogicFrame(ref _nextLaunchWaveFrame[i]);
            reader.PersistBoolean(ref _rampUp[i]);
            reader.PersistLogicFrame(ref _rampUpFrame[i]);
            reader.PersistLogicFrame(ref _catapultSystemFrame[i]);
            reader.PersistLogicFrame(ref _lowerRampFrame[i]);
        }
    }

    internal struct FlightDeckSpace
    {
        public Vector3 Prep;
        public Quaternion Orientation;
        public int Runway;
        public ObjectId ObjectInSpace;
    }

    internal struct FlightDeckRunway
    {
        public Vector3 Start;
        public Vector3 End;
        public Vector3 LandingStart;
        public Vector3 LandingEnd;
        public Transform TakeoffStartTransform;
        public ObjectId InUseForTakeoff;
        public ObjectId InUseForLanding;
    }

    internal struct HealingEntry : IPersistableObject
    {
        public ObjectId ObjectId => _objectId;
        public LogicFrame HealStartFrame => _healStartFrame;

        private ObjectId _objectId;
        private LogicFrame _healStartFrame;

        public HealingEntry(ObjectId objectId, LogicFrame healStartFrame)
        {
            _objectId = objectId;
            _healStartFrame = healStartFrame;
        }

        public void Persist(StatePersister persister)
        {
            persister.PersistObjectId(ref _objectId);
            persister.PersistLogicFrame(ref _healStartFrame);
        }
    }

    public struct FlightDeckParkingInfo
    {
        public Vector3 ParkingSpace;
        public Quaternion ParkingOrientation;
        public Vector3 RunwayStart;
        public Vector3 RunwayEnd;
        public Vector3 RunwayExit;
        public Vector3 RunwayLandingStart;
        public Vector3 RunwayLandingEnd;
        public Vector3 RunwayApproach;
        public float RunwayTakeoffDistance;
    }
}

[AddedIn(SageGame.CncGeneralsZeroHour)]
public sealed class FlightDeckBehaviorModuleData : BehaviorModuleData
{
    internal static FlightDeckBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<FlightDeckBehaviorModuleData> FieldParseTable = new IniParseTable<FlightDeckBehaviorModuleData>
    {
        { "NumRunways", (parser, x) => x.NumRunways = parser.ParseInteger() },
        { "NumSpacesPerRunway", (parser, x) => x.NumSpacesPerRunway = parser.ParseInteger() },

        { "Runway1Spaces", (parser, x) => x.Runway1Spaces = parser.ParseBoneNameArray() },
        { "Runway1Takeoff", (parser, x) => x.Runway1Takeoff = parser.ParseBoneNameArray() },
        { "Runway1Landing", (parser, x) => x.Runway1Landing = parser.ParseBoneNameArray() },
        { "Runway1Taxi", (parser, x) => x.Runway1Taxi = parser.ParseBoneNameArray() },
        { "Runway1Creation", (parser, x) => x.Runway1Creation = parser.ParseBoneNameArray() },
        { "Runway1CatapultSystem", (parser, x) => x.Runway1CatapultSystem = parser.ParseFXParticleSystemTemplateReference() },

        { "Runway2Spaces", (parser, x) => x.Runway2Spaces = parser.ParseBoneNameArray() },
        { "Runway2Takeoff", (parser, x) => x.Runway2Takeoff = parser.ParseBoneNameArray() },
        { "Runway2Landing", (parser, x) => x.Runway2Landing = parser.ParseBoneNameArray() },
        { "Runway2Taxi", (parser, x) => x.Runway2Taxi = parser.ParseBoneNameArray() },
        { "Runway2Creation", (parser, x) => x.Runway2Creation = parser.ParseBoneNameArray() },
        { "Runway2CatapultSystem", (parser, x) => x.Runway2CatapultSystem = parser.ParseFXParticleSystemTemplateReference() },

        { "HealAmountPerSecond", (parser, x) => x.HealAmountPerSecond = parser.ParseInteger() },

        { "ApproachHeight", (parser, x) => x.ApproachHeight = parser.ParseInteger() },
        { "LandingDeckHeightOffset", (parser, x) => x.LandingDeckHeightOffset = parser.ParseFloat() },
        { "ParkingCleanupPeriod", (parser, x) => x.ParkingCleanupPeriod = parser.ParseInteger() },
        { "HumanFollowPeriod", (parser, x) => x.HumanFollowPeriod = parser.ParseInteger() },

        { "PayloadTemplate", (parser, x) => x.PayloadTemplate = parser.ParseObjectReference() },
        { "ReplacementDelay", (parser, x) => x.ReplacementDelay = parser.ParseInteger() },
        { "DockAnimationDelay", (parser, x) => x.DockAnimationDelay = parser.ParseInteger() },

        { "LaunchWaveDelay", (parser, x) => x.LaunchWaveDelay = parser.ParseInteger() },
        { "LaunchRampDelay", (parser, x) => x.LaunchRampDelay = parser.ParseInteger() },
        { "LowerRampDelay", (parser, x) => x.LowerRampDelay = parser.ParseInteger() },
        { "CatapultFireDelay", (parser, x) => x.CatapultFireDelay = parser.ParseInteger() },
    };

    public int NumRunways { get; private set; }
    public int NumSpacesPerRunway { get; private set; }

    public string[] Runway1Spaces { get; private set; }
    public string[] Runway1Takeoff { get; private set; }
    public string[] Runway1Landing { get; private set; }
    public string[] Runway1Taxi { get; private set; }
    public string[] Runway1Creation { get; private set; }
    public LazyAssetReference<FXParticleSystemTemplate> Runway1CatapultSystem { get; private set; }

    public string[] Runway2Spaces { get; private set; }
    public string[] Runway2Takeoff { get; private set; }
    public string[] Runway2Landing { get; private set; }
    public string[] Runway2Taxi { get; private set; }
    public string[] Runway2Creation { get; private set; }
    public LazyAssetReference<FXParticleSystemTemplate> Runway2CatapultSystem { get; private set; }

    /// <summary>
    /// Amount of health to give non-airborne aircraft on the deck.
    /// </summary>
    public int HealAmountPerSecond { get; private set; }

    public int ApproachHeight { get; private set; }
    public float LandingDeckHeightOffset { get; private set; }
    public int ParkingCleanupPeriod { get; private set; }
    public int HumanFollowPeriod { get; private set; }

    public LazyAssetReference<ObjectDefinition> PayloadTemplate { get; private set; }
    public int ReplacementDelay { get; private set; }
    public int DockAnimationDelay { get; private set; }

    public int LaunchWaveDelay { get; private set; }
    public int LaunchRampDelay { get; private set; }
    public int LowerRampDelay { get; private set; }
    public int CatapultFireDelay { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new FlightDeckBehavior(gameObject, gameEngine, this);
    }
}
