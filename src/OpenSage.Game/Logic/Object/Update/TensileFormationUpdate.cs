// TensileFormationUpdate - R10 port through the full task packet (api-freeze-v1 §6 / template v1.1).
//
// Behavioral reference: generals-gpl GeneralsMD TensileFormationUpdate.cpp/.h (GPL semantics
// reference only; this is fresh code against the frozen contract). The GPL module is a springy
// "avalanche" formation: a cluster of objects that sit inert until one is damaged, then unzip -
// each dislodged member knocking its neighbours loose - while sliding down the terrain until
// they settle into rubble ~300 frames later.
//
// SCOPE (task packet: "S1 damage pipeline + S3 partition nearby-query; no movement, no
// pathfinding"). The GPL file is dominated by MOVEMENT / TERRAIN / PATHFINDER / CLIENT code that
// the landed systems (S1-S4, S6, S7, S8) deliberately do not cover this round. The
// sim-deterministic residue that IS dependency-satisfiable is faithfully ported here:
//   - state { enabled, life } (GPL m_enabled + m_life);
//   - the disabled->enabled transition on self-damage (GPL update() head: bdt >= BODY_DAMAGED);
//   - propagateDislodgement, the S1+S3 cascade the packet names ("damage bleeds to nearby
//     objects that also carry this module"): every TFU member within 100 units is knocked to
//     BODY_DAMAGED, which flips its own disabled TFU on that member's next poll (GPL 100.0f
//     iterateObjectsInRange + PartitionFilterTensileFormationMember);
//   - the life>300 rubble settle (GPL body->setDamageState(BODY_RUBBLE) + sleep forever).
// Everything else in the GPL update() is out of scope - see FINDINGS below.
//
// Every mutable sim field appears in Xfer exactly once (§3); tolerances are the field's
// conformance class at its declaration site (§4).
//
// FINDINGS (behavior-fact gaps / scope excisions, filed not invented - modules-r10/TensileFormationUpdate.md):
//   F-TFU-1 pathfinder obstacle: GPL ctor + rubble-settle call createAWallFromMyFootprint and
//     the enable path calls removeWallFromMyFootprint (treat a stationary/collapsed member as a
//     pathfind obstacle). Pathfinding is explicitly excluded this round (landing in parallel on
//     sys/pathfinding); the wall create/remove is not modeled. A seam growth is required once
//     pathfinding lands.
//   F-TFU-2 crack audio: GPL plays m_crackSound (an AudioEventRTS asset on the module) when the
//     formation first breaks. ISimEvents exposes FireUnitSoundAtObject (a UnitSpecificSounds
//     KEY resolved against the object's own template) and FX, but no member to play an arbitrary
//     named AudioEventRTS asset. CrackSound is parsed (audited) but not emitted; audio is a
//     client output with no determinism obligation, so this has no sim consequence.
//   F-TFU-3 avalanche physics (inertia, terrain-slope flow, 4-neighbour tensor spring,
//     setPosition, shrubbery topple): pure movement/transform on float substrate (no orientation
//     or position setter exists in a [SimState] module by design, ISimContext). Dropped whole;
//     it belongs to the movement/transform round.
//   F-TFU-4 link cache + random orientation: GPL initLinks() caches the 4 nearest members as
//     spring anchors and draws GameLogicRandomValueReal(-PI,PI) to set a random facing. The
//     tensors are movement-only (F-TFU-3) and the orientation is transform-only; the cascade
//     uses a live 100-unit radius scan every propagation, which is a superset of the 4 cached
//     links, so dropping the cache loses no reach. The lone logic-RNG draw is deliberately NOT
//     reproduced (it only fed the discarded orientation; drawing to discard would desync the
//     stream against any future faithful movement port).
//   F-TFU-5 client model-conditions (MOVING / FREEFALL / POST_COLLAPSE): driven by the physics;
//     not modeled (no physics to drive them). Presentation only, never sim CRC.
//   F-TFU-6 propagate revive quirk: GPL setDamageState(BODY_DAMAGED) is UNCONDITIONAL - it pulls
//     a member that is already ReallyDamaged/Rubble back UP to the Damaged health boundary
//     (ActiveBody recomputes health from the state ratio, S1). We match GPL exactly; the repeated
//     re-clamp is what holds the cluster at Damaged health until each member's own life>300
//     forces rubble. Noted because it is a health mutation, not just a state flag.
//   F-TFU-7 Xfer completeness vs GPL: GPL xfer (version 1) serializes ONLY m_enabled, silently
//     dropping m_life (and all physics state) - a latent save/load bug (the life timer would
//     reset on load, restarting the propagation cadence and the rubble countdown). The contract
//     rule "every mutable sim field is in Xfer exactly once" (§3) fixes this: we xfer { enabled,
//     life }. This is an intentional, correctness-improving divergence, kept at version 1 since
//     no released .sav carries a mid-avalanche TFU (formations are map-scripted, transient).

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class TensileFormationUpdate : UpdateModule
{
    private readonly TensileFormationUpdateModuleData _data;

    // ---- GPL literals -----------------------------------------------------------------------

    /// <summary>GPL update(): a disabled formation returns UPDATE_SLEEP(30) between polls.</summary>
    private static readonly LogicFrameSpan DisabledPollInterval = new LogicFrameSpan(30);

    /// <summary>GPL update(): propagate on m_life % 30 == 29.</summary>
    private const uint PropagateInterval = 30;

    /// <summary>GPL update(): m_life > 300 => settle into rubble, sleep forever.</summary>
    private const uint RubbleLifetime = 300;

    /// <summary>GPL propagateDislodgement(): iterateObjectsInRange radius 100.0f.</summary>
    private static readonly Fix64 PropagationRadius = Fix64.FromDecimalLiteral("100");

    // ---- mutable sim state (the whole inventory; every field is in Xfer, F-TFU-7) ------------

    /// <summary>Formation is unzipping (GPL m_enabled). Starts from the INI flag; latched true
    /// the frame this object - or a dislodged neighbour - becomes damaged.</summary>
    private bool _enabled;

    /// <summary>Frames elapsed since enabling (GPL m_life): gates the propagation cadence and
    /// the rubble cutoff. Not xfered by GPL; xfered here for save/load correctness (F-TFU-7).</summary>
    private LogicFrameSpan _life;

    public TensileFormationUpdate(GameObject gameObject, ISimContext context, TensileFormationUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _enabled = data.Enabled;

        // GPL ctor extras deliberately not modeled: createAWallFromMyFootprint (F-TFU-1),
        // crackSound.setObjectID (F-TFU-2). GPL initLinks() runs lazily on first update in the
        // original but is dropped whole (F-TFU-4).

        // A disabled formation idle-polls every 30 frames; a formation that starts enabled
        // (INI Enabled = Yes) ticks immediately to run the cascade.
        SetWakeFrame(_enabled ? UpdateSleepTime.None : UpdateSleepTime.Frames(DisabledPollInterval));
    }

    public override UpdateSleepTime Update()
    {
        if (!_enabled)
        {
            // GPL: "We are all going to sit here idle ... until one of us gets hurt."
            // bdt >= BODY_DAMAGED  <=>  worse than Pristine.
            if (GameObject.BodyModule.DamageState.IsWorseThan(BodyDamageType.Pristine))
            {
                // The hurt one enables and starts moving. (GPL also removeWallFromMyFootprint,
                // F-TFU-1, and plays the crack sound, F-TFU-2.)
                _enabled = true;
                return UpdateSleepTime.None;
            }

            return UpdateSleepTime.Frames(DisabledPollInterval);
        }

        // GPL runs avalanche physics every frame here (F-TFU-3/-5); the sim-deterministic
        // residue is the life timer, the periodic dislodgement, and the rubble cutoff.
        _life += LogicFrameSpan.One;

        if (_life.Value > RubbleLifetime)
        {
            // GPL: clear the motion model-conditions, become rubble, re-wall (F-TFU-1), sleep.
            GameObject.BodyModule.DamageState = BodyDamageType.Rubble;
            return UpdateSleepTime.Forever;
        }

        if (_life.Value % PropagateInterval == PropagateInterval - 1) // GPL m_life % 30 == 29
        {
            PropagateDislodgement();
        }

        return UpdateSleepTime.None;
    }

    /// <summary>
    /// GPL propagateDislodgement: every TensileFormationUpdate member within 100 units is
    /// knocked to BODY_DAMAGED (S1), so its own disabled TFU enables on its next poll - the
    /// formation unzips outward (S3 radius scan + TFU-membership filter). The set is
    /// unconditional, matching GPL (F-TFU-6). GPL also re-damages its 4 cached links; those are
    /// a subset of this radius, so they add nothing here (F-TFU-4).
    /// </summary>
    private void PropagateDislodgement()
    {
        foreach (var other in Context.Partition.QueryObjectsInRadius(GameObject, PropagationRadius))
        {
            if (other == GameObject)
            {
                // The query already excludes the centre; belt-and-suspenders.
                continue;
            }

            // PartitionFilterTensileFormationMember.allow: getTFU(objOther) != NULL.
            if (!other.HasBehavior<TensileFormationUpdate>())
            {
                continue;
            }

            var body = other.BodyModule;
            if (body is null)
            {
                continue;
            }

            body.DamageState = BodyDamageType.Damaged;
        }
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferBool("Enabled", ref _enabled);
        xfer.XferFrameSpan("Life", ref _life, Tolerance.Exact); // frame count: Exact (A3)
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight (design-module-api §2.2). No numeric fields to quantize:
// Enabled is a bool and CrackSound is an audio asset reference (client output, F-TFU-2), so
// the S5 quantizing vocabulary does not apply here.
// ============================================================================
[SimDataAudited]
public sealed class TensileFormationUpdateModuleData : UpdateModuleData
{
    internal static TensileFormationUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<TensileFormationUpdateModuleData> FieldParseTable =
        new IniParseTable<TensileFormationUpdateModuleData>
        {
            { "Enabled", (parser, x) => x.Enabled = parser.ParseBoolean() },
            { "CrackSound", (parser, x) => x.CrackSound = parser.ParseAssetReference() }
        };

    /// <summary>Initial formation state (GPL m_enabled, default FALSE): Yes means the cluster
    /// is already unzipping at spawn.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Audio event played when the formation first breaks (GPL m_crackSound). Client
    /// output only; parsed but not emitted sim-side (F-TFU-2).</summary>
    public string CrackSound { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new TensileFormationUpdate(gameObject, gameEngine.SimContext, this);
    }
}
