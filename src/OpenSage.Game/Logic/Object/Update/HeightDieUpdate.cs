// HeightDieUpdate - R12 port, translated from generals-gpl HeightDieUpdate.cpp/.h (GPL
// semantics reference, identical byte-for-byte between Generals and GeneralsMD save the
// header banner; api-freeze-v1 §6 / template v1.1).
//
// Behavioral facts translated from the GPL source:
//   - state is { hasDied, particlesDestroyed, lastPosition, earliestDeathFrame }. The GPL
//     ctor seeds lastPosition to (-1,-1,-1) and earliestDeathFrame to UINT_MAX (a "not yet
//     computed" sentinel), translated here as LogicFrame.MaxValue.
//   - update() every frame (GPL returns UPDATE_SLEEP_NONE):
//       1. lazily computes earliestDeathFrame = now + InitialDelay the first time update()
//          runs (NOT at construction - GPL reads TheGameLogic->getFrame() inside update()),
//          then stops doing anything (but keeps ticking every frame) until that frame.
//       2. while contained (getContainedBy() != NULL, e.g. riding a transport): does nothing
//          but keeps lastPosition current, so the direction check is correct the instant the
//          object is released.
//       3. directionOK starts TRUE every call; when hasDied is still false and
//          OnlyWhenMovingDown is set, directionOK goes false when the object's Z has not
//          decreased since lastPosition (pos.z >= lastPosition.z). Note this recompute only
//          happens while hasDied is false - once the object has died, directionOK is simply
//          left at its per-call default of TRUE (matches the GPL guard shape exactly: the
//          whole "if (m_hasDied == FALSE)" block, including the directionOK assignment,
//          is skipped after death).
//       4. targetHeight = terrainHeight + TargetHeight, terrainHeight from
//          Context.Terrain.GetGroundHeight. When TargetHeightIncludesStructures, scans
//          structures within the object's own bounding-circle radius (GPL
//          iterateObjectsInRange + PartitionFilterAcceptByKindOf(STRUCTURE)) for the tallest
//          MaxHeightAbovePosition; if that beats the INI TargetHeight, targetHeight becomes
//          tallestHeight + terrainHeight instead (GPL's "either specified height above
//          terrain OR the tallest structure underneath" - not both added together).
//       5. dies when pos.z < targetHeight && directionOK: snaps to terrainHeight first when
//          SnapToGroundOnDeath OR pos.z is already below terrain (GPL's "never let death
//          leave us below ground" clause), then Kill()s and latches hasDied.
//       6. independently of the above (and evaluated every call, not just while alive):
//          once particlesDestroyed is false and pos.z < DestroyAttachedParticlesAtHeight and
//          (hasDied || directionOK), fires the attached-particle-cleanup event once and
//          latches particlesDestroyed. Default DestroyAttachedParticlesAtHeight is -1 (a
//          floor height objects essentially never reach), matching GPL's "practically never
//          triggers unless the INI opts in" default.
//       7. lastPosition = pos, every call, whether or not anything else happened.
//
// FINDINGS (behavior-fact gaps, filed not invented):
//   F-HDU-1 destroyAttachedSystems: OpenSAGE's ParticleSystemManager has no object-attachment
//     tracking (a created ParticleSystem is never linked back to the GameObject that spawned
//     it), so Context.Events.DestroyAttachedParticleSystems is currently a recorded-but-inert
//     request on the adapter side (SimContext.SimEventsAdapter) - the sim-relevant half (the
//     once-only particlesDestroyed latch, which this module owns and Xfers) is fully modeled;
//     the client cleanup itself waits on that tracking.
//   F-HDU-2 bridge/layer height: GPL's TargetHeightIncludesStructures branch also consults
//     TheTerrainLogic->getHighestLayerForDestination/getLayerHeight to raise terrainHeightAtPos
//     for objects standing on a bridge. ITerrainLogic exposes no layer/bridge query yet (only
//     GetGroundHeight); the structure-scan half of TargetHeightIncludesStructures (the part
//     every existing test in this codebase's genre of object actually exercises) is fully
//     ported, the bridge-layer contribution is not.
//
// Every mutable sim field appears in Xfer exactly once (§3); field order mirrors the GPL
// xfer() order (hasDied, particlesDestroyed, lastPosition, earliestDeathFrame).

using OpenSage.Data.Ini;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class HeightDieUpdate : UpdateModule
{
    private readonly HeightDieUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>TRUE once we have triggered death (GPL m_hasDied).</summary>
    private bool _hasDied;

    /// <summary>TRUE once attached particle systems have been requested destroyed (GPL m_particlesDestroyed).</summary>
    private bool _particlesDestroyed;

    /// <summary>Our position as of the last update(), for the OnlyWhenMovingDown direction check.</summary>
    private FixVector3 _lastPosition;

    /// <summary>
    /// Earliest frame we are allowed to consider dying. LogicFrame.MaxValue means "not yet
    /// computed" (GPL's UINT_MAX sentinel), resolved to now + InitialDelay on the first
    /// update() call.
    /// </summary>
    private LogicFrame _earliestDeathFrame;

    public HeightDieUpdate(GameObject gameObject, ISimContext context, HeightDieUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _lastPosition = new FixVector3(-Fix64.One, -Fix64.One, -Fix64.One);
        _earliestDeathFrame = LogicFrame.MaxValue;

        // GPL update() ticks every frame (UPDATE_SLEEP_NONE).
        SetWakeFrame(UpdateSleepTime.None);
    }

    public override UpdateSleepTime Update()
    {
        var now = Context.CurrentFrame;

        // Lazily compute the delay gate on the first update(), exactly as the GPL source
        // does (it reads TheGameLogic->getFrame() inside update(), not the ctor).
        if (_earliestDeathFrame == LogicFrame.MaxValue)
        {
            _earliestDeathFrame = now + _data.InitialDelay;
        }

        if (_earliestDeathFrame > now)
        {
            return UpdateSleepTime.None;
        }

        // Contained (e.g. riding a transport): do nothing, but keep our position current.
        if (GameObject.ContainedBy != null)
        {
            _lastPosition = SimTransformBridge.PullPosition(GameObject);
            return UpdateSleepTime.None;
        }

        var pos = SimTransformBridge.PullPosition(GameObject);
        var directionOk = true;

        if (!_hasDied)
        {
            if (_data.OnlyWhenMovingDown && pos.Z >= _lastPosition.Z)
            {
                directionOk = false;
            }

            var terrainHeight = Context.Terrain.GetGroundHeight(pos);
            var targetHeight = terrainHeight + _data.TargetHeight;

            if (_data.TargetHeightIncludesStructures)
            {
                var range = SimTransformBridge.PullGeometry(GameObject).BoundingCircleRadius;
                var tallestHeight = Fix64.Zero;

                foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, range))
                {
                    if (candidate == GameObject)
                    {
                        continue;
                    }

                    if (candidate.Definition.KindOf is null || !candidate.Definition.KindOf.Get(ObjectKinds.Structure))
                    {
                        continue;
                    }

                    var thisHeight = candidate.MaxHeightAbovePosition;
                    if (thisHeight > tallestHeight)
                    {
                        tallestHeight = thisHeight;
                    }
                }

                if (tallestHeight > _data.TargetHeight)
                {
                    targetHeight = tallestHeight + terrainHeight;
                }
            }

            if (pos.Z < targetHeight && directionOk)
            {
                if (_data.SnapToGroundOnDeath || pos.Z < terrainHeight)
                {
                    var ground = new FixVector3(pos.X, pos.Y, terrainHeight);
                    SimTransformBridge.Push(GameObject, ground, SimTransformBridge.PullYaw(GameObject));
                    pos = ground;
                }

                GameObject.Kill();
                _hasDied = true;
            }
        }

        if (!_particlesDestroyed && pos.Z < _data.DestroyAttachedParticlesAtHeight && (_hasDied || directionOk))
        {
            Context.Events.DestroyAttachedParticleSystems(GameObject.Id);
            _particlesDestroyed = true;
        }

        _lastPosition = pos;

        return UpdateSleepTime.None;
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order mirrors the GPL xfer() order (hasDied, particlesDestroyed, lastPosition,
    // earliestDeathFrame).

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferBool("HasDied", ref _hasDied);
        xfer.XferBool("ParticlesDestroyed", ref _particlesDestroyed);
        xfer.XferFixVector3("LastPosition", ref _lastPosition, Tolerance.Exact);
        xfer.XferFrame("EarliestDeathFrame", ref _earliestDeathFrame, Tolerance.Exact); // sentinel-valued: Exact (A3)
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

/// <summary>
/// Kills the object when it drops below a configured height above the terrain (or, optionally,
/// above any structure underneath it).
/// </summary>
[SimDataAudited]
public sealed class HeightDieUpdateModuleData : UpdateModuleData
{
    internal static HeightDieUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<HeightDieUpdateModuleData> FieldParseTable = new IniParseTable<HeightDieUpdateModuleData>
    {
        { "TargetHeight", (parser, x) => x.TargetHeight = parser.ParseFix64() },
        { "TargetHeightIncludesStructures", (parser, x) => x.TargetHeightIncludesStructures = parser.ParseBoolean() },
        { "DestroyAttachedParticlesAtHeight", (parser, x) => x.DestroyAttachedParticlesAtHeight = parser.ParseFix64() },
        { "OnlyWhenMovingDown", (parser, x) => x.OnlyWhenMovingDown = parser.ParseBoolean() },
        { "SnapToGroundOnDeath", (parser, x) => x.SnapToGroundOnDeath = parser.ParseBoolean() },
        { "InitialDelay", (parser, x) => x.InitialDelay = parser.ParseDurationLogicFrames() },
    };

    /// <summary>Die at this height above the terrain (or structure top).</summary>
    public Fix64 TargetHeight { get; private set; }

    /// <summary>Target height considers terrain AND structure height underneath us.</summary>
    public bool TargetHeightIncludesStructures { get; private set; }

    /// <summary>
    /// INI comment indicates that this is a hack, and should be removed... Destroy any
    /// attached particle system of the object once it is below this height. Default -1: a
    /// floor essentially never reached unless the INI opts in.
    /// </summary>
    public Fix64 DestroyAttachedParticlesAtHeight { get; private set; } = -Fix64.One;

    /// <summary>Don't die unless moving in the downward Z direction.</summary>
    public bool OnlyWhenMovingDown { get; private set; }

    /// <summary>Snap to the ground when killed.</summary>
    public bool SnapToGroundOnDeath { get; private set; }

    /// <summary>Don't consider dying before this many frames have elapsed.</summary>
    public LogicFrameSpan InitialDelay { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new HeightDieUpdate(gameObject, gameEngine.SimContext, this);
    }
}
