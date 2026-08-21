// HitReactionBehavior (damage half) - the one float-substrate crossing the arming path needs
// (D-7, the ProductionQueueHordeContainDamage.cs precedent): DamageInfo is legacy float
// substrate, so Result.ActualDamageDealt is a plain float and the HitReactionThresholdN
// flyweight fields it is compared against are float for the same reason (see
// HitReactionBehaviorData.cs). This partial-class half carries NO [SimState] attribute
// anywhere in THIS file, so the per-file SIMCORE quarantine scope
// (SimCoreScope.DeclaresSimStateType, which scans one syntax tree at a time) never turns on
// for it. Partial classes share one field set, so _activeTier here is the same instance state
// the [SimState] half owns and Xfers - nothing is duplicated or re-xfered.
//
// Timing: the wake is armed at now + LifeTimerN, i.e. the frame the hit lands is frame zero of
// the hold and Update() clears on the frame the hold runs out - GPL PoisonedBehavior's
// startPoisonedEffects (m_poisonOverallStopFrame = now + m_poisonDurationData, cleared in
// update() once now >= that frame) applied to a reaction hold. A hold that quantizes to one
// logic frame therefore clears on the very next frame; at BFME2's 5 Hz logic rate every INI
// duration at or under 200 ms quantizes to exactly that. One consequence, engine-owned and not
// a defect of this module: a FastHitsResetReaction re-arm of a one-frame hold landing on the
// hold's own wake frame is a SetWakeFrame(now, now + 1) against an already-due module, which
// GameLogic.AwakenUpdateModule deliberately ignores (GPL's "already awake, don't reset"
// short-circuit), so that reaction still ends on schedule instead of restarting.

namespace OpenSage.Logic.Object;

public partial class HitReactionBehavior : IDamageModule
{
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
}
