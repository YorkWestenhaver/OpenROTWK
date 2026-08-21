// ReplenishUnitsBehavior - R10 port through the full task packet (api-freeze-v1 §6 / template v1.1).
//
// Behavioral reference: NONE in generals-gpl (AddedIn Bfme; a BFME horde-identity behavior with
// no GeneralsMD sibling). The cadence is read off a CLEAN-ROOM behavioral spec of the retail
// module (facts only, no decompiled logic transplanted; evidence in
// bfme2-workbench/research/modules-r10/ReplenishUnitsBehavior.md):
//   * registration: TheModuleFactory maps "ReplenishUnitsBehavior" -> data-alloc / module-alloc
//     (interface mask = Update + Upgrade-mux).
//   * ctor: if StartsActive is false the first wake is SleepForever; otherwise the first wake is
//     a LogicRandom draw in [1, ReplenishDelay] frames - a startup stagger so a crowd of hordes
//     does not all replenish on the same frame (the same shape AutoHeal/EnemyNear use, one draw
//     at construction, CRC-relevant).
//   * update: (1) if not active (upgrade-mux virtual) or the object's runtime disabled bit is
//     set -> return SleepForever; (2) if ReplenishHordeMembersOnly require a contain module
//     whose replenish-count is non-zero, else sleep forever; (3) if
//     NoReplenishIfEnemyWithinRadius is non-sentinel, a partition scan for an enemy within that
//     radius suppresses this cycle; (4) otherwise a partition/region query (ReplenishRadius)
//     drives the member respawn which fires the spawn FX and applies the status; (5) the return
//     value is ReplenishDelay - the steady cadence, re-evaluated even when a cycle was
//     suppressed.
//
// Landed systems consumed (task packet - nothing reimplemented): S6 hordes (SimHordeContain's
// member roster + the TryReplenishOneMember slot-vacancy/create/FX path), S4 production (member
// re-creation lives inside that path via Context.GameLogic.CreateObjectAt), and the S3 (R8)
// partition query (Context.Partition.QueryObjectsInRadius, ascending ObjectId) for the enemy and
// nearby-horde scans. No pathfinding (members spawn at the horde; no move orders are issued).
//
// FINDINGS (behavior-fact gaps, filed not invented - see modules-r10/ReplenishUnitsBehavior.md):
//   F-RUB-1 The engine's runtime disabled bit and the upgrade-mux "is active"
//     virtual are not modeled: this ModuleData's frozen field set carries no TriggeredBy, so
//     _active derives solely from StartsActive with no in-sim toggle path. _active is still
//     xfered so a future upgrade-mux wiring round-trips.
//   F-RUB-2 ReplenishHordeMembersOnly=false: the retail path additionally replenishes non-horde
//     contained/garrisoned units. No landed system exposes a non-horde replenish target, so only
//     the horde-member path (the =true behavior) is modeled; =false is a strict superset we
//     under-serve rather than mis-serve.
//   F-RUB-3 One-vs-all per cycle: the retail inner respawn count is ambiguous. This port tops the
//     horde back up to full (fills every vacant non-banner slot) each cadence tick, matching the
//     "respawns dead horde members" packet wording; a one-per-tick reading is the alternative.
//   F-RUB-4 ReplenishDelay is parsed ms->frames (ParseDurationLogicFrames, S5 wire boundary) on
//     the SAGE convention that *Delay/*Time fields are authored in milliseconds; the retail ctor
//     uses the already-converted frame count directly. Same posture as AutoHeal/EnemyNear.

using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.Logic.Object.Horde;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class ReplenishUnitsBehavior : UpdateModule
{
    private readonly ReplenishUnitsBehaviorModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Whether the replenish timer runs. Seeded from StartsActive (F-RUB-1: no toggle
    /// path in this ModuleData; xfered so a future upgrade-mux wiring round-trips).</summary>
    private bool _active;

    public ReplenishUnitsBehavior(GameObject gameObject, ISimContext context, ReplenishUnitsBehaviorModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _active = data.StartsActive;

        // Ctor cadence (spec): inactive -> never; active -> a LogicRandom startup
        // stagger in [1, ReplenishDelay] frames drawn from the S3 lockstep stream (Next is
        // inclusive, matching the retail logicrandom(1, delay)). A zero delay is degenerate
        // (never replenish) and skips the draw.
        if (_active && _data.ReplenishDelay.Value > 0)
        {
            var stagger = Context.GameLogicRandom.Next(1, (int)_data.ReplenishDelay.Value);
            SetWakeFrame(UpdateSleepTime.Frames(new LogicFrameSpan((uint)stagger)));
        }
        else
        {
            SetWakeFrame(UpdateSleepTime.Forever);
        }
    }

    public override UpdateSleepTime Update()
    {
        // Active gate (spec: inactive -> SleepForever). F-RUB-1.
        if (!_active)
        {
            return UpdateSleepTime.Forever;
        }

        // Enemy suppression (spec step 3): a live enemy within NoReplenishIfEnemyWithinRadius
        // skips this cycle without disturbing the cadence.
        if (_data.NoReplenishIfEnemyWithinRadius > Fix64.Zero &&
            EnemyWithinRadius(_data.NoReplenishIfEnemyWithinRadius))
        {
            return Reschedule();
        }

        Replenish();
        return Reschedule();
    }

    /// <summary>The steady cadence (spec: update returns ReplenishDelay). A zero delay parks
    /// the module forever rather than spin every frame.</summary>
    private UpdateSleepTime Reschedule()
        => _data.ReplenishDelay.Value > 0
            ? UpdateSleepTime.Frames(_data.ReplenishDelay)
            : UpdateSleepTime.Forever;

    /// <summary>Any live, on-map, enemy-owned object within <paramref name="radius"/> of this
    /// object. Consumes the S3 partition seam and the Player relationship set (the mirror of the
    /// ally check AutoHeal consumes), exactly as EnemyNearUpdate does.</summary>
    private bool EnemyWithinRadius(Fix64 radius)
    {
        var owner = GameObject.Owner;
        if (owner is null)
        {
            return false;
        }
        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, radius))
        {
            if (candidate == GameObject || candidate.IsEffectivelyDead || candidate.IsOffMap)
            {
                continue;
            }
            if (candidate.Owner is not null && owner.Enemies.Contains(candidate.Owner))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Replenish this object's own horde, plus - when ReplenishRadius is set - every
    /// same-owner horde within that radius (the retail "replenish nearby hordes" reach). All
    /// member re-creation routes through the landed SimHordeContain path (S6/S4); this module
    /// only drives the cadence and applies ReplenishStatii to the freshly spawned members.</summary>
    private void Replenish()
    {
        var selfHorde = GameObject.FindBehavior<SimHordeContain>();
        if (selfHorde is not null)
        {
            ReplenishHorde(GameObject, selfHorde);
        }

        if (_data.ReplenishRadius <= Fix64.Zero)
        {
            return;
        }

        var owner = GameObject.Owner;
        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _data.ReplenishRadius))
        {
            if (candidate == GameObject || candidate.IsEffectivelyDead)
            {
                continue;
            }
            // Same player only (the retail relationship filter for the replenish targets).
            if (!ReferenceEquals(candidate.Owner, owner))
            {
                continue;
            }
            var horde = candidate.FindBehavior<SimHordeContain>();
            if (horde is not null)
            {
                ReplenishHorde(candidate, horde);
            }
        }
    }

    /// <summary>Tops <paramref name="horde"/> back up to full at <paramref name="anchor"/> (fills
    /// every vacant non-banner slot this cycle - F-RUB-3), then stamps ReplenishStatii onto each
    /// newly spawned member. The slot-vacancy/create/FX work is the landed
    /// SimHordeContain.TryReplenishOneMember; the FX name is ReplenishFXList.</summary>
    private void ReplenishHorde(GameObject anchor, SimHordeContain horde)
    {
        // Snapshot the roster so we can find (and status-stamp) only the members we just added.
        HashSet<ObjectId> before = null;
        if (_data.ReplenishStatii != ObjectStatus.None)
        {
            before = new HashSet<ObjectId>(horde.MemberIds);
        }

        // Fill vacant slots; SlotCount+1 caps the loop (TryReplenishOneMember returns false once
        // no vacant non-banner slot remains, or while the horde is not yet initialized).
        var guard = horde.SlotCount + 1;
        while (guard-- > 0 && horde.TryReplenishOneMember(anchor, _data.ReplenishFXList))
        {
        }

        if (before is null)
        {
            return;
        }
        foreach (var id in horde.MemberIds)
        {
            if (before.Contains(id))
            {
                continue;
            }
            var member = Context.GameLogic.GetObjectById(id);
            member?.SetObjectStatus(_data.ReplenishStatii, true);
        }
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9). The next-wake frame is engine-owned
    // (persisted by the base UpdateModule walk per S6), NOT a module field - so the cadence needs
    // no state here; _active is the entire per-module inventory.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferBool("Active", ref _active);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================
[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class ReplenishUnitsBehaviorModuleData : BehaviorModuleData
{
    internal static ReplenishUnitsBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(BaseFieldParseTable);

    internal static readonly IniParseTable<ReplenishUnitsBehaviorModuleData> BaseFieldParseTable = new IniParseTable<ReplenishUnitsBehaviorModuleData>
    {
        // ms in INI, ceil-quantized to logic frames at parse (S5 wire boundary; F-RUB-4). The
        // timer feeds UpdateSleepTime, so it must be a LogicFrameSpan, never an int of frames.
        { "ReplenishDelay", (parser, x) => x.ReplenishDelay = parser.ParseDurationLogicFrames() },
        // Deterministic S3-query radii -> Fix64 (never float across the analyzer wall).
        { "ReplenishRadius", (parser, x) => x.ReplenishRadius = parser.ParseFix64() },
        { "NoReplenishIfEnemyWithinRadius", (parser, x) => x.NoReplenishIfEnemyWithinRadius = parser.ParseFix64() },
        { "ReplenishStatii", (parser, x) => x.ReplenishStatii = parser.ParseEnum<ObjectStatus>() },
        { "ReplenishFXList", (parser, x) => x.ReplenishFXList = parser.ParseAssetReference() },
        { "ReplenishHordeMembersOnly", (parser, x) => x.ReplenishHordeMembersOnly = parser.ParseBoolean() },
        { "StartsActive", (parser, x) => x.StartsActive = parser.ParseBoolean() },
    };

    /// <summary>Frames between replenish attempts (ms in INI, ceil-quantized at parse, S5).</summary>
    public LogicFrameSpan ReplenishDelay { get; private set; }

    /// <summary>Reach for the "replenish nearby same-owner hordes" extension; 0 = self only.</summary>
    public Fix64 ReplenishRadius { get; private set; }

    /// <summary>A live enemy within this radius suppresses the current replenish cycle; 0 = never suppress.</summary>
    public Fix64 NoReplenishIfEnemyWithinRadius { get; private set; }

    /// <summary>Status stamped onto each freshly spawned member (ObjectStatus.None = no stamp).</summary>
    public ObjectStatus ReplenishStatii { get; private set; } = ObjectStatus.None;

    /// <summary>FX fired at each spawned member (routed through the landed SimHordeContain path).</summary>
    public string ReplenishFXList { get; private set; }

    /// <summary>Restrict replenish to horde members (F-RUB-2: the non-horde path is unmodeled).</summary>
    public bool ReplenishHordeMembersOnly { get; private set; }

    /// <summary>Whether the timer runs from creation (else the module parks until toggled).</summary>
    public bool StartsActive { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new ReplenishUnitsBehavior(gameObject, gameEngine.SimContext, this);
    }
}
