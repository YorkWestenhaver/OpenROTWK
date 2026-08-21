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
//
// Hold-timing convention (the frame the hit lands is frame zero of the hold): OnDamage arms
// the wake at now + LifeTimerN and Update() clears at that frame, i.e. GPL PoisonedBehavior's
// startPoisonedEffects/update pair (m_poisonOverallStopFrame = now + duration, effects stopped
// once now >= that frame) applied to a reaction hold instead of a poison duration. See
// HitReactionBehaviorDamage.cs for the arming half.
//
// File split (D-7, ProductionQueueHordeContainDamage.cs precedent): the two float-substrate
// crossings this module needs - reading the legacy float DamageInfo.Result.ActualDamageDealt,
// and the float HitReactionThresholdN flyweight fields it compares against - live in
// HitReactionBehaviorDamage.cs and HitReactionBehaviorData.cs, neither of which declares a
// [SimState] type, so the per-file SIMCORE quarantine scope never turns on for them. THIS file
// declares [SimState] and stays float-free.

using System;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed partial class HitReactionBehavior : UpdateModule
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
