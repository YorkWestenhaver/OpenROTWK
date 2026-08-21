// EntEnragedUpdate - R13 port (modules-r13/specs/EntEnragedUpdateModuleData.md). BFME-only, no
// generals-gpl sibling (grep of generals-gpl/generals-community for enrage/berserk/rampage:
// zero hits, spec §0) - fresh engineering composition against the frozen contract, in the same
// posture as AttributeModifierAuraUpdate and OneRingPenaltyUpdate.
//
// Trigger model (spec §1, F-ENRAGE-3): no cross-object death-notification primitive exists
// (GameObject.OnDie only dispatches to the dying object's own IDieModules - verified by direct
// read, zero broadcast/callback surface on ISimContext). This port implements the trigger as a
// periodic scan-based proxy for "a hated enemy is standing near a dead ally," not true kill
// attribution: on each scan, within ScanDistance, look for at least one dead ally matching
// FriendlyDeadFilter AND at least one live enemy matching HatedObjectFilter. Both present ->
// enrage. Relationship (ally/enemy) is derived from Player.Allies/Player.Enemies rather than
// trusting the filter's own ANY/ENEMIES/ALLIES tokens, because ObjectFilter.Matches has no
// "relative to whom" parameter and never reads the Rules bits (same documented gap
// AttributeModifierAuraUpdate.IsEligible routes around).
//
// Scan cadence (F-ENRAGE-2): no ScanDelayTime-equivalent field exists on this class. Reuses
// EnemyNearUpdate's own landed default (LOGICFRAMES_PER_SECOND = 5 frames at 5 Hz), the same
// numeric default the sibling nearby-object-scan module ships with when no override exists.
//
// Detection radius (F-ENRAGE-1): ScanDistance is a new field, grounded by retail-authored (not
// Ghidra) INI comment text (entsinfantry.ini, always disabled). Defaults to Fix64.Zero, which
// reproduces shipped AotR's actual behavior exactly (every sampled instance has this feature
// commented out, so the trigger never fires in retail data as authored).
//
// Only ModelConditionFlag.WeaponsetEnraged is driven (spec §0 correction 1): WeaponSetConditions
// (the enum that actually keys ObjectDefinition.WeaponSets) has no Enraged member at all, so
// this module cannot and does not drive a weapon-set swap - only the already-landed
// animation/visual-state ModelConditionFlag path is real. ModelConditionFlag.InitialEnraged is
// a different, unrelated concept this module does not touch (F-ENRAGE-4).
//
// Unconsumed-but-parsed fields (tracked not invented, same posture as
// AttributeModifierAuraUpdate.AntiFX/MaxActiveRank):
//   - EnragedTransitionTime (F-ENRAGE-6): no sim-facing transition/blend primitive exists.
//   - EnragedLifeTimer (F-ENRAGE-5): its one grounded sighting is commented out even inside an
//     otherwise-live block; no other field names what a "lifetime" would bound.
//
// Every mutable sim field appears in Xfer exactly once (spec §2); tolerances are the field's
// conformance class at its declaration site.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

/// <summary>The module's two-state lifecycle (spec §1's state machine).</summary>
public enum EntEnragedPhase
{
    Idle,
    Enraged,
}

[SimState]
public sealed class EntEnragedUpdate : UpdateModule
{
    /// <summary>Reused from EnemyNearUpdate's own landed GPL default: LOGICFRAMES_PER_SECOND at
    /// the frozen 5 Hz BFME2 title rate (F-ENRAGE-2 - no ScanDelayTime-equivalent field exists
    /// on this class).</summary>
    private static readonly LogicFrameSpan ScanCadence = new LogicFrameSpan(5);

    private readonly EntEnragedUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    private EntEnragedPhase _phase;

    /// <summary>Only meaningful in <see cref="EntEnragedPhase.Enraged"/>; a stale value read
    /// back while <see cref="EntEnragedPhase.Idle"/> is harmless (never compared there).</summary>
    private LogicFrame _enrageEndFrame;

    private LogicFrame _cooldownUntilFrame;

    public EntEnragedUpdate(GameObject gameObject, ISimContext context, EntEnragedUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // No StartsActive-style gate field exists on this class at all (spec §1, same posture
        // as OneRingPenaltyUpdate's F-RING-1): the scan runs from construction.
        _cooldownUntilFrame = Context.CurrentFrame;
        SetWakeFrame(UpdateSleepTime.None);
    }

    public override UpdateSleepTime Update()
    {
        switch (_phase)
        {
            case EntEnragedPhase.Enraged:
                if (Context.CurrentFrame >= _enrageEndFrame)
                {
                    _phase = EntEnragedPhase.Idle;
                    GameObject.ClearModelConditionState(ModelConditionFlag.WeaponsetEnraged);
                    FireBuffFX(_data.EnragedOffBuffFX);
                    _cooldownUntilFrame = Context.CurrentFrame + _data.TimeUntilCanRageAgain;
                }
                break;

            case EntEnragedPhase.Idle:
                if (Context.CurrentFrame >= _cooldownUntilFrame && ScanTriggersEnrage())
                {
                    _phase = EntEnragedPhase.Enraged;
                    _enrageEndFrame = Context.CurrentFrame + _data.EnragedTime;
                    GameObject.SetModelConditionState(ModelConditionFlag.WeaponsetEnraged);
                    FireBuffFX(_data.EnragedOnBuffFX);
                }
                break;
        }

        return UpdateSleepTime.Frames(ScanCadence);
    }

    /// <summary>
    /// The scan-based trigger proxy (spec §1 / F-ENRAGE-3): true iff, within ScanDistance,
    /// there is at least one dead ally matching FriendlyDeadFilter AND at least one live enemy
    /// matching HatedObjectFilter. Short-circuits per category once a hit is found - only
    /// presence matters, not count or identity.
    /// </summary>
    private bool ScanTriggersEnrage()
    {
        if (_data.ScanDistance <= Fix64.Zero)
        {
            // F-ENRAGE-1: a zero-radius scan finds nothing, matching every sampled shipped
            // instance's real (disabled) behavior exactly.
            return false;
        }

        var owner = GameObject.Owner;
        if (owner == null)
        {
            return false;
        }

        var foundDeadAlly = false;
        var foundHatedEnemy = false;

        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _data.ScanDistance))
        {
            if (!foundDeadAlly && IsDeadAlly(candidate, owner))
            {
                foundDeadAlly = true;
            }

            if (!foundHatedEnemy && IsHatedEnemy(candidate, owner))
            {
                foundHatedEnemy = true;
            }

            if (foundDeadAlly && foundHatedEnemy)
            {
                return true;
            }
        }

        return foundDeadAlly && foundHatedEnemy;
    }

    /// <summary>A dead ally matching FriendlyDeadFilter. Relationship is derived from
    /// Player.Allies rather than the filter's own (inert) relationship tokens - see the file
    /// header.</summary>
    private bool IsDeadAlly(GameObject candidate, Player owner)
    {
        if (!candidate.IsEffectivelyDead)
        {
            return false;
        }

        var candidateOwner = candidate.Owner;
        if (candidateOwner == null)
        {
            return false;
        }

        if (!ReferenceEquals(owner, candidateOwner) && !owner.Allies.Contains(candidateOwner))
        {
            return false;
        }

        return _data.FriendlyDeadFilter == null || _data.FriendlyDeadFilter.Matches(candidate);
    }

    /// <summary>A live enemy matching HatedObjectFilter. Relationship is derived from
    /// Player.Enemies rather than the filter's own (inert) relationship tokens - see the file
    /// header.</summary>
    private bool IsHatedEnemy(GameObject candidate, Player owner)
    {
        if (candidate.IsEffectivelyDead)
        {
            return false;
        }

        var candidateOwner = candidate.Owner;
        if (candidateOwner == null || !owner.Enemies.Contains(candidateOwner))
        {
            return false;
        }

        return _data.HatedObjectFilter == null || _data.HatedObjectFilter.Matches(candidate);
    }

    private void FireBuffFX(string fxName)
    {
        if (string.IsNullOrEmpty(fxName))
        {
            return;
        }

        Context.Events.FireParticleSystemAtObject(fxName, GameObject.Id, string.Empty, false);
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferEnum("Phase", ref _phase);
        xfer.XferFrame("EnrageEndFrame", ref _enrageEndFrame);
        xfer.XferFrame("CooldownUntilFrame", ref _cooldownUntilFrame);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================
[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class EntEnragedUpdateModuleData : UpdateModuleData
{
    internal static EntEnragedUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<EntEnragedUpdateModuleData> FieldParseTable = new IniParseTable<EntEnragedUpdateModuleData>
    {
        // Kept as a raw Fix64, NOT quantized to logic frames - parsed/stored, not consumed by
        // the trigger (F-ENRAGE-5; see the file header).
        { "EnragedLifeTimer", (parser, x) => x.EnragedLifeTimer = parser.ParseFix64() },
        { "HatedObjectFilter", (parser, x) => x.HatedObjectFilter = ObjectFilter.Parse(parser) },
        { "FriendlyDeadFilter", (parser, x) => x.FriendlyDeadFilter = ObjectFilter.Parse(parser) },
        // ms in INI, ceil-quantized to logic frames at parse (S5 wire boundary).
        { "EnragedTime", (parser, x) => x.EnragedTime = parser.ParseDurationLogicFrames() },
        { "TimeUntilCanRageAgain", (parser, x) => x.TimeUntilCanRageAgain = parser.ParseDurationLogicFrames() },
        { "EnragedTransitionTime", (parser, x) => x.EnragedTransitionTime = parser.ParseDurationLogicFrames() },
        { "EnragedTransitionFX", (parser, x) => x.EnragedTransitionFX = parser.ParseAssetReference() },
        { "EnragedOnBuffFX", (parser, x) => x.EnragedOnBuffFX = parser.ParseAssetReference() },
        { "EnragedOffBuffFX", (parser, x) => x.EnragedOffBuffFX = parser.ParseAssetReference() },
        // Deterministic S3-query radius -> Fix64 (never float across the analyzer wall). New
        // field this port adds (spec §1); always commented out in every sampled shipped
        // instance, so Fix64.Zero reproduces observed shipped behavior exactly (F-ENRAGE-1).
        { "ScanDistance", (parser, x) => x.ScanDistance = parser.ParseFix64() },
    };

    public Fix64 EnragedLifeTimer { get; private set; }
    public ObjectFilter HatedObjectFilter { get; private set; }
    public ObjectFilter FriendlyDeadFilter { get; private set; }

    /// <summary>Duration of the buffed state once triggered (ms in INI, ceil-quantized at
    /// parse, S5).</summary>
    public LogicFrameSpan EnragedTime { get; private set; }

    /// <summary>Post-expiry cooldown before the trigger can re-fire (ms in INI, ceil-quantized
    /// at parse, S5). Zero means "always enrage if you should" (entsinfantry.ini:1068's own
    /// inline comment).</summary>
    public LogicFrameSpan TimeUntilCanRageAgain { get; private set; }

    /// <summary>Parsed and stored, not consumed (F-ENRAGE-6): no sim-facing transition/blend
    /// primitive exists to attach it to.</summary>
    public LogicFrameSpan EnragedTransitionTime { get; private set; }

    public string EnragedTransitionFX { get; private set; }
    public string EnragedOnBuffFX { get; private set; }
    public string EnragedOffBuffFX { get; private set; }

    /// <summary>New field this port adds (spec §1). Defaults to zero, matching every sampled
    /// shipped instance where this field is always commented out (F-ENRAGE-1).</summary>
    public Fix64 ScanDistance { get; private set; } = Fix64.Zero;

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new EntEnragedUpdate(gameObject, gameEngine.SimContext, this);
    }
}
