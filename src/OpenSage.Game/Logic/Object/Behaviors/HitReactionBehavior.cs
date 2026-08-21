// HitReactionBehavior - R13 port (data-derivable; no GPL sibling, see modules-r13/specs/
// HitReactionBehaviorData.md).
//
// Behavior facts (grounded in two independent retail AotR data sources plus already-landed
// codebase precedent - see the spec's §1 for the full citation chain):
//   - Three ascending damage-amount tiers (Threshold1 < Threshold2 < Threshold3), each with
//     its own reaction-hold duration (LifeTimerN). On OnDamage, the highest tier crossed by
//     ActualDamageDealt wins (same "highest threshold met" ladder shape as
//     ActiveBody.CalculateDamageState).
//   - LifeTimerN is ms-in-INI, parsed via ParseDurationLogicFrames() (established convention
//     for every other landed *Timer/*Delay field in this codebase; the two data sources'
//     modder comments disagree on unit and are not treated as engine ground truth).
//   - FastHitsResetReaction gates what happens when a new qualifying hit arrives while a
//     reaction is already active: true = restart (re-arm to the new tier's full duration from
//     now, swap HitLevelN flags if the tier changed); false = the in-flight reaction is left
//     alone and the new hit is dropped entirely.
//   - Cosmetic output: ModelConditionFlag.HitReaction plus the tier's HitLevelN flag, both
//     already-declared BFME-generation flags with no other landed owner. Cleared together when
//     the armed wake fires.
//   - No death/damage-state gating invented: IDamageModule.OnDamage is only ever dispatched
//     from ActiveBody.AttemptDamage while health is actively decreasing, which does not fire
//     post-death - no extra guard is added here.

using System;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class HitReactionBehavior : UpdateModule, IDamageModule
{
    private readonly HitReactionBehaviorData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Tier (1-3) of the currently-armed reaction; 0 = none armed.</summary>
    private int _activeTier;

    public HitReactionBehavior(GameObject gameObject, ISimContext context, HitReactionBehaviorData data)
        : base(gameObject, context)
    {
        _data = data;
        SetWakeFrame(UpdateSleepTime.Forever);
    }

    public void OnDamage(in DamageInfo damageData)
    {
        var amount = damageData.Result.ActualDamageDealt;

        int tier;
        if (amount >= _data.HitReactionThreshold3) tier = 3;
        else if (amount >= _data.HitReactionThreshold2) tier = 2;
        else if (amount >= _data.HitReactionThreshold1) tier = 1;
        else return; // below every tier: no reaction

        if (_activeTier != 0 && !_data.FastHitsResetReaction)
        {
            return; // already reacting, not fast-hits-reset: this hit is dropped
        }

        if (_activeTier != 0 && _activeTier != tier)
        {
            GameObject.ClearModelConditionState(HitLevelFlag(_activeTier));
        }

        _activeTier = tier;
        GameObject.SetModelConditionState(ModelConditionFlag.HitReaction);
        GameObject.SetModelConditionState(HitLevelFlag(tier));

        SetWakeFrame(UpdateSleepTime.Frames(LifeTimerFor(tier)));
    }

    public override UpdateSleepTime Update()
    {
        // Wake fires only at expiry (armed exactly once per trigger/restart via OnDamage).
        GameObject.ClearModelConditionState(ModelConditionFlag.HitReaction);
        if (_activeTier != 0)
        {
            GameObject.ClearModelConditionState(HitLevelFlag(_activeTier));
        }
        _activeTier = 0;
        return UpdateSleepTime.Forever;
    }

    private LogicFrameSpan LifeTimerFor(int tier) => tier switch
    {
        1 => _data.HitReactionLifeTimer1,
        2 => _data.HitReactionLifeTimer2,
        3 => _data.HitReactionLifeTimer3,
        _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };

    private static ModelConditionFlag HitLevelFlag(int tier) => tier switch
    {
        1 => ModelConditionFlag.HitLevel1,
        2 => ModelConditionFlag.HitLevel2,
        3 => ModelConditionFlag.HitLevel3,
        _ => throw new ArgumentOutOfRangeException(nameof(tier)),
    };

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferInt("ActiveTier", ref _activeTier); // 0-3, Exact (small bounded enum-ish int)
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);
        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
        // No legacy retail-save layout to be compatible with: this module was [ParseOnly]
        // before this port and never executed, so no prior legacy state existed to persist.
        // Flag to integrator: if an oracle/ddiff finding later surfaces a legacy .sav layout
        // for this module, this stub needs real field reads.
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================
[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class HitReactionBehaviorData : UpdateModuleData
{
    internal static HitReactionBehaviorData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal static readonly IniParseTable<HitReactionBehaviorData> FieldParseTable = new IniParseTable<HitReactionBehaviorData>
    {
        { "HitReactionLifeTimer1", (parser, x) => x.HitReactionLifeTimer1 = parser.ParseDurationLogicFrames() },
        { "HitReactionLifeTimer2", (parser, x) => x.HitReactionLifeTimer2 = parser.ParseDurationLogicFrames() },
        { "HitReactionLifeTimer3", (parser, x) => x.HitReactionLifeTimer3 = parser.ParseDurationLogicFrames() },
        { "HitReactionThreshold1", (parser, x) => x.HitReactionThreshold1 = parser.ParseFloat() },
        { "HitReactionThreshold2", (parser, x) => x.HitReactionThreshold2 = parser.ParseFloat() },
        { "HitReactionThreshold3", (parser, x) => x.HitReactionThreshold3 = parser.ParseFloat() },
        { "FastHitsResetReaction", (parser, x) => x.FastHitsResetReaction = parser.ParseBoolean() }
    };

    /// <summary>Level 1 (light damage) reaction hold duration. Ms in INI, ceil-quantized at parse.</summary>
    public LogicFrameSpan HitReactionLifeTimer1 { get; private set; }

    /// <summary>Level 2 (medium damage) reaction hold duration. Ms in INI, ceil-quantized at parse.</summary>
    public LogicFrameSpan HitReactionLifeTimer2 { get; private set; }

    /// <summary>Level 3 (heavy damage) reaction hold duration. Ms in INI, ceil-quantized at parse.</summary>
    public LogicFrameSpan HitReactionLifeTimer3 { get; private set; }

    /// <summary>Thresholds stay float (F3: rate/time-adjacent scalar compared against the
    /// legacy float `DamageInfo.Result.ActualDamageDealt` - see
    /// FireWeaponWhenDamagedBehaviorModuleData.DamageAmount precedent, same field shape, same
    /// reasoning: IDamageModule's callback struct is still float-typed engine-wide, not yet
    /// migrated to Fix64/CombatDamageOutput).</summary>
    public float HitReactionThreshold1 { get; private set; }
    public float HitReactionThreshold2 { get; private set; }
    public float HitReactionThreshold3 { get; private set; }

    /// <summary>True = a qualifying hit during an active reaction restarts the timer to the
    /// new tier's full duration (swapping HitLevelN if the tier changed); false = the new hit
    /// is dropped and the in-flight reaction runs to its own completion.</summary>
    public bool FastHitsResetReaction { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
        => new HitReactionBehavior(gameObject, gameEngine.SimContext, this);
}
