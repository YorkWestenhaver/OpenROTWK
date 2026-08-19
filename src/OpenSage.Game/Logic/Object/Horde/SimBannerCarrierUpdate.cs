// SimBannerCarrierUpdate - the member-side banner-carrier module (spec §4.3 parse table @
// 0xc66090, §7 behavior), implemented FRESH from the clean-room spec. While the horde has
// been melee-free for MeleeFreeUnitSpawnTime, the banner replenishes one missing member
// every IdleSpawnRate (FX UnitSpawnFX) - the horde owns seating and creation
// (SimHordeContain.TryReplenishOneMember); this module owns the timers.
//
// OPEN QUESTION 5 default (community consensus, recorded): idle replenish requires the
// banner carrier alive - the timers live HERE, so a dead banner stops replenish until the
// horde's DiedRespawnTime/MeleeFreeBannerReSpawnTime respawn path brings a new one.
// Deferred with findings: the banner-morphs-into-needed-unit path (FUN_00876481 alternative;
// MorphCondition/BannerMorphFX parse but no-op - HORDE-F5), ReplenishNearbyHorde /
// ReplenishAllNearbyHordes / ScanHordeDistance (parse-only - HORDE-F5), ExpLevelDraw
// (client), UpgradeRequired gating (upgrade seam).

using System.Collections.Generic;
using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object.Horde;

[SimState]
public sealed class SimBannerCarrierUpdate : UpdateModule
{
    private readonly SimBannerCarrierUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----
    private ObjectId _hordeId;
    private LogicFrame _nextIdleSpawnFrame;

    public SimBannerCarrierUpdate(GameObject gameObject, ISimContext context, SimBannerCarrierUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        SetWakeFrame(UpdateSleepTime.None);
    }

    public ObjectId HordeId => _hordeId;

    /// <summary>Called by the horde when this banner is seated.</summary>
    public void AttachToHorde(ObjectId hordeId)
    {
        _hordeId = hordeId;
        // First replenish waits one full IdleSpawnRate from seating (EA comment: "spawn a
        // new member every n seconds when idle" - a cadence, not an instant fill).
        _nextIdleSpawnFrame = Context.CurrentFrame + _data.IdleSpawnRate;
        SetWakeFrame(UpdateSleepTime.None);
    }

    public override UpdateSleepTime Update()
    {
        if (!_hordeId.IsValid)
        {
            return UpdateSleepTime.Forever;
        }
        var hordeObject = Context.GameLogic.GetObjectById(_hordeId);
        if (hordeObject == null || hordeObject.IsDestroyed)
        {
            _hordeId = ObjectId.Invalid;
            return UpdateSleepTime.Forever;
        }
        var horde = hordeObject.FindBehavior<SimHordeContain>();
        if (horde == null)
        {
            return UpdateSleepTime.Forever;
        }

        var now = Context.CurrentFrame;

        // "n ms units must not have been fighting to be able to spawn units when idle"
        // (EA comment, spec §4.3). LastMeleeFrame zero = never fought.
        var meleeFree = horde.LastMeleeFrame == LogicFrame.Zero ||
                        now >= horde.LastMeleeFrame + _data.MeleeFreeUnitSpawnTime;

        if (meleeFree && now >= _nextIdleSpawnFrame)
        {
            if (horde.TryReplenishOneMember(GameObject, _data.UnitSpawnFX))
            {
                _nextIdleSpawnFrame = now + _data.IdleSpawnRate;
            }
        }

        return UpdateSleepTime.None;
    }

    // ---- the single walk (F8 Objects channel; declaration order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferObjectId("HordeId", ref _hordeId);
        xfer.XferFrame("NextIdleSpawnFrame", ref _nextIdleSpawnFrame);
    }
}

/// <summary>
/// Audited field set of spec §4.3 (parse table @ 0xc66090). Durations quantize ms ->
/// LogicFrameSpan; FX/upgrade references stay names (outputs / unconsumed seams).
/// </summary>
[SimDataAudited]
public sealed class SimBannerCarrierUpdateModuleData : UpdateModuleData
{
    internal static SimBannerCarrierUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<SimBannerCarrierUpdateModuleData> FieldParseTable =
        new IniParseTable<SimBannerCarrierUpdateModuleData>
        {
            { "IdleSpawnRate", (parser, x) => x.IdleSpawnRate = parser.ParseDurationLogicFrames() },
            { "MeleeFreeUnitSpawnTime", (parser, x) => x.MeleeFreeUnitSpawnTime = parser.ParseDurationLogicFrames() },
            { "DiedRespawnTime", (parser, x) => x.DiedRespawnTime = parser.ParseDurationLogicFrames() },
            { "MeleeFreeBannerReSpawnTime", (parser, x) => x.MeleeFreeBannerReSpawnTime = parser.ParseDurationLogicFrames() },
            { "MorphCondition", (parser, x) => x.MorphConditions.Add(SimBannerMorphCondition.Parse(parser)) },
            { "BannerMorphFX", (parser, x) => x.BannerMorphFX = parser.ParseAssetReference() },
            { "UnitSpawnFX", (parser, x) => x.UnitSpawnFX = parser.ParseAssetReference() },
            { "ReplenishNearbyHorde", (parser, x) => x.ReplenishNearbyHorde = parser.ParseBoolean() },
            { "ReplenishAllNearbyHordes", (parser, x) => x.ReplenishAllNearbyHordes = parser.ParseBoolean() },
            { "ScanHordeDistance", (parser, x) => x.ScanHordeDistance = parser.ParseFix64() },
            { "UpgradeRequired", (parser, x) => x.UpgradeRequired = parser.ParseAssetReference() },
        };

    /// <summary>"spawn a new member every n seconds when idle" (EA comment).</summary>
    public LogicFrameSpan IdleSpawnRate { get; private set; }

    /// <summary>"n ms units must not have been fighting to be able to spawn units when idle".</summary>
    public LogicFrameSpan MeleeFreeUnitSpawnTime { get; private set; }

    /// <summary>"how much time must pass after Banner Carrier dies before horde can spawn another".</summary>
    public LogicFrameSpan DiedRespawnTime { get; private set; }

    /// <summary>"time since horde has been fighting before a new Banner Carrier can be respawned".</summary>
    public LogicFrameSpan MeleeFreeBannerReSpawnTime { get; private set; }

    public List<SimBannerMorphCondition> MorphConditions { get; } = new();
    public string BannerMorphFX { get; private set; }
    public string UnitSpawnFX { get; private set; }
    public bool ReplenishNearbyHorde { get; private set; }
    public bool ReplenishAllNearbyHordes { get; private set; }
    public Fix64 ScanHordeDistance { get; private set; }
    public string UpgradeRequired { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new SimBannerCarrierUpdate(gameObject, gameEngine.SimContext, this);
    }
}

/// <summary>"UnitType:&lt;t&gt; ModelState:"..."" - model state for the banner-morph path (parse-only this round).</summary>
public sealed class SimBannerMorphCondition
{
    internal static SimBannerMorphCondition Parse(IniParser parser)
    {
        return new SimBannerMorphCondition
        {
            UnitType = parser.ParseAttributeObjectReference("UnitType"),
            ModelState = parser.ParseAttribute("ModelState", parser.ParseString),
        };
    }

    public LazyAssetReference<ObjectDefinition> UnitType { get; private set; }
    public string ModelState { get; private set; }
}
