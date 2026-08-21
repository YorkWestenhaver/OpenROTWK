// RespawnUpdate - the R14 respawn seam (design-respawn-seam.md, as amended by the wave-2a
// adversarial review; owner-ratified as dr-0033/dr-0034).
//
// Behavioral reference: BFME2/ROTWK-only. There is NO GPL RespawnUpdate - grep -rli respawn
// over generals-gpl/generals-community returns only RebuildHoleBehavior and SpawnBehavior - so
// the lifecycle below is written from the shipped INI vocabulary and the written seam design,
// never from the retail binary (clean-room wall). The one GPL thing that IS translated is the
// CONTROL FLOW of the claim: generals-gpl GeneralsMD Object.cpp's onDie consults a condition on
// the dying object and hands the death to another module's named interface instead of running
// the corpse path. That idiom is now IReviveLifecycleModule, and this is its first
// implementation.
//
// WHAT THIS MODULE OWNS
//
//   1. The CLAIM. On the killing blow, GameObject.OnDie offers the death to every
//      IReviveLifecycleModule in ascending ModuleIndex. This module claims it when the death
//      is non-permanent, and the claim's only effect is that OnDie returns early: no slow
//      death, no IDieModule, no die sound, no Destroy(). Nothing ever reaches GameLogic's
//      destroy list, so the object stays in the world and keeps ticking - the reap suppression
//      IS the early return (design §3.3; the "veto a reaper" alternatives were rejected in
//      §3.5, and the census says no RespawnUpdate carrier has a second reaper anyway).
//
//   2. The PHASES after that claim: DeathAnimation -> AwaitingRevive -> Reviving ->
//      RespawnAnimation -> Alive. They live in this module's own Xfer walk and nowhere else -
//      that is the review's H2 ruling: the awaiting-revive state is MODULE state, not a new
//      GameObject-level channel field. No private status bit was added to GameObject.
//
//   3. The REVIVE, performed IN PLACE through the Body (OQ-1 decided in favour of in-place
//      survival, dr-0033; H4 required the exit from the dead state to be specified rather than
//      assumed). See RespawnBody.Revive: restoring health through the landed body path is what
//      clears GameObject.IsEffectivelyDead, because ActiveBody recomputes that flag from the
//      Fix64 health ledger on every health change. It also re-arms the body's permanence
//      resolver, so a LATER death latches its own verdict (the second-death latch).
//
// PERMANENCE, AND WHY ClaimDeath TAKES THE DAMAGE (review finding H1). ActiveBody calls
// GameObject.OnDie from INSIDE base.AttemptDamage, before any subclass post-processing runs.
// A claim predicate that read RespawnBody.IsPermanentlyKilled would therefore see false for
// EVERY death, permanent ones included, and would claim and strand them. ClaimDeath instead
// drives the resolution from the killing blow it is handed
// (RespawnBody.ResolvePermanenceForDeath), so the verdict exists at the instant the decision
// needs it.
//
// KNOWN DIVERGENCES, recorded rather than papered over:
//   * The hero is revived where it fell, not at the anchor's production exit point (OQ-7).
//     ISimContext has no "move an existing object" member and transforms are still float
//     substrate (D-7), so honouring the exit point needs a separate seam.
//   * RespawnAsTemplate is not honoured - coming back as a different template means
//     destroy-and-recreate, which is exactly what OQ-1 decided against.
//   * A dead-but-awaiting-revive hero keeps ticking ALL of its update modules, because
//     GameLogic's sleepy queue gates on DisabledFlags and never on death (the R14 census's
//     refutation of "blocker #1"). Suppressing non-revive modules while dead is a real
//     question this seam does not answer; it is filed, not fixed here.
//   * The revive purchase plays no sound (OQ-6): ISimEvents has no general "play a MiscAudio
//     entry" member and this packet does not grow one for a single caller.

#nullable enable

using OpenSage.SimCore;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class RespawnUpdate : UpdateModule, IReviveLifecycleModule
{
    private readonly RespawnUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    private RespawnPhase _phase;

    /// <summary>End of the current timed phase. Meaningless outside a timed phase.</summary>
    private LogicFrame _phaseEndFrame;

    /// <summary>
    /// The object whose revive slot was purchased (the fortress/keep). Carried so a cancel can
    /// refund against the same anchor and so the completion is attributable; Invalid outside
    /// the Reviving phase.
    /// </summary>
    private ObjectId _anchorObjectId;

    /// <summary>
    /// Gold actually withdrawn for the in-flight revive, so a cancel refunds exactly what was
    /// paid rather than recomputing a price that may since have changed (an upgrade could have
    /// been bought, or lost, mid-countdown). Integer money (F3).
    /// </summary>
    private int _paidCost;

    public RespawnUpdate(GameObject gameObject, ISimContext context, RespawnUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _phase = RespawnPhase.Alive;

        // Idle until something happens to us. Unlike the SpecialPowerTemplate-gated update
        // family, this module has nothing to do on a living hero, and the shipped corpus puts
        // it on 291 objects - a permanent every-frame tick for all of them would be pure cost.
        // ClaimDeath re-arms the wake.
        SetWakeFrame(UpdateSleepTime.Forever);
    }

    /// <summary>The lifecycle phase. Read-only view for tests and for the order path.</summary>
    public RespawnPhase Phase => _phase;

    /// <summary>The anchor of the in-flight revive purchase; Invalid when none.</summary>
    public ObjectId ReviveAnchorId => _anchorObjectId;

    /// <summary>Gold withdrawn for the in-flight revive purchase; zero when none.</summary>
    public int PaidReviveCost => _paidCost;

    /// <summary>
    /// True while this object is dead, un-reaped and waiting to come back - the state that
    /// exists only because the reap was suppressed.
    /// </summary>
    public bool IsAwaitingRevive =>
        _phase is RespawnPhase.DeathAnimation or RespawnPhase.AwaitingRevive or RespawnPhase.Reviving;

    // ------------------------------------------------------------------
    // IReviveLifecycleModule
    // ------------------------------------------------------------------

    public bool ClaimDeath(in DamageInfoInput damageInput)
    {
        // Terminal: this object already died a death we refused, and the corpse path owns it.
        if (_phase == RespawnPhase.PermanentlyDead)
        {
            return false;
        }

        // Already dead and mid-lifecycle: a second lethal blow on an un-reaped corpse must not
        // restart the death. It is also not a death we want the ordinary path to handle - that
        // would destroy an object we are holding - so it is claimed and ignored.
        if (IsAwaitingRevive)
        {
            return true;
        }

        // Alive, or alive-and-still-playing-its-respawn-animation. Both are claimable: a hero
        // cut down the instant it comes back is as revivable as any other.

        // A RespawnUpdate on a non-RespawnBody object is a data error: there is no permanence
        // verdict to consult and no body-side revive path. Refuse the claim, so the object dies
        // normally - claiming a death we cannot undo would strand it forever. Conservative on
        // purpose (design §3.3).
        if (GameObject.BodyModule is not RespawnBody body)
        {
            _phase = RespawnPhase.PermanentlyDead;
            return false;
        }

        // H1: resolve permanence FROM THIS DAMAGE. At this instant we are inside
        // ActiveBody.AttemptDamage's base call, so no latched verdict exists yet.
        if (body.ResolvePermanenceForDeath(damageInput))
        {
            _phase = RespawnPhase.PermanentlyDead;
            return false;
        }

        _phase = RespawnPhase.DeathAnimation;
        _phaseEndFrame = Context.CurrentFrame + _data.DeathAnimationTime;
        _anchorObjectId = ObjectId.Invalid;
        _paidCost = 0;

        // Killed out of the respawn animation: drop it before the death animation starts, so
        // the two presentation flags can never both be held.
        if (_data.RespawnAnim != ModelConditionFlag.None)
        {
            GameObject.ModelConditionFlags.Set(_data.RespawnAnim, false);
        }

        if (_data.DeathAnim != ModelConditionFlag.None)
        {
            GameObject.ModelConditionFlags.Set(_data.DeathAnim, true);
        }

        if (!string.IsNullOrEmpty(_data.DeathFX))
        {
            Context.Events.FireFXAtObject(_data.DeathFX, GameObject.Id);
        }

        // Re-arm: we were sleeping forever as a living hero. Legal here because ClaimDeath runs
        // inside the damage pipeline, never inside our own Update().
        SetWakeFrame(UpdateSleepTime.None);
        return true;
    }

    // ------------------------------------------------------------------
    // The order-facing seam (design §5.4). Named methods on a named module, the same shape
    // InitiateIntentToDoSpecialPower established, invoked by ReviveApplicator.
    // ------------------------------------------------------------------

    /// <summary>
    /// Whether a revive purchase at <paramref name="anchor"/> is legal right now. A stale
    /// order (the hero already came back, the anchor died, someone else's fortress) is refused
    /// deterministically on every peer rather than half-applied.
    /// </summary>
    public bool CanBeRevivedAt(GameObject? anchor)
    {
        if (_phase != RespawnPhase.AwaitingRevive)
        {
            return false;
        }

        if (anchor is null || anchor.IsDestroyed)
        {
            return false;
        }

        // The revive is bought out of the hero owner's treasury at the owner's own building.
        return ReferenceEquals(anchor.Owner, GameObject.Owner);
    }

    /// <summary>
    /// The base (pre-modifier) gold cost of reviving this hero right now: the matching
    /// <c>RespawnEntry</c> for its level if the data declares one, else <c>RespawnRules</c>.
    /// The anchor's <c>ProductionModifier CostMultiplier</c> is applied OUTSIDE this module -
    /// that field is still <c>float</c> (ProductionUpdate), so the multiply happens on the
    /// order side through <c>ProductionMath.ApplyProductionMultiplier</c>'s pinned rounding.
    /// </summary>
    public int BaseReviveCost => SelectEntry()?.Cost ?? _data.RespawnRules?.Cost ?? 0;

    /// <summary>The base (pre-modifier) revive countdown, in whole logic frames.</summary>
    public LogicFrameSpan BaseReviveTime => SelectEntry()?.Time ?? _data.RespawnRules?.Time ?? LogicFrameSpan.Zero;

    /// <summary>
    /// Starts the countdown after the caller has taken the money. <paramref name="paidCost"/>
    /// is what was actually withdrawn, kept for the refund; <paramref name="reviveTime"/> is
    /// the (already modifier-adjusted) countdown. Returns false, changing nothing, when the
    /// purchase is not currently legal.
    /// </summary>
    public bool BeginRevive(GameObject? anchor, int paidCost, LogicFrameSpan reviveTime)
    {
        if (!CanBeRevivedAt(anchor))
        {
            return false;
        }

        _phase = RespawnPhase.Reviving;
        _phaseEndFrame = Context.CurrentFrame + reviveTime;
        _anchorObjectId = anchor!.Id;
        _paidCost = paidCost;

        SetWakeFrame(UpdateSleepTime.None);
        return true;
    }

    /// <summary>
    /// Cancels an in-flight revive and reports what should be refunded. Returns false (and
    /// refunds nothing) when no revive is in flight.
    /// </summary>
    /// <remarks>
    /// Driven-only for now: there is no cancel ORDER. The recovered BFME2 GameMessageType
    /// vocabulary has MSG_REVIVE = 1114 but no cancel-revive value (1115 is
    /// MSG_TOGGLE_NO_AUTO_ACQUIRE), and inventing a number for the ZH-numbered OrderType enum
    /// would fabricate a retail fact. Filed as an open routing gap rather than guessed.
    /// </remarks>
    public bool CancelRevive(out int refund)
    {
        refund = 0;
        if (_phase != RespawnPhase.Reviving)
        {
            return false;
        }

        refund = _paidCost;
        _paidCost = 0;
        _anchorObjectId = ObjectId.Invalid;
        _phase = RespawnPhase.AwaitingRevive;
        return true;
    }

    // ------------------------------------------------------------------
    // The phase machine
    // ------------------------------------------------------------------

    public override UpdateSleepTime Update()
    {
        var now = Context.CurrentFrame;

        switch (_phase)
        {
            case RespawnPhase.DeathAnimation:
                if (now >= _phaseEndFrame)
                {
                    EnterAwaitingRevive();
                }
                break;

            case RespawnPhase.Reviving:
                if (now >= _phaseEndFrame)
                {
                    CompleteRevive();
                }
                break;

            case RespawnPhase.RespawnAnimation:
                if (now >= _phaseEndFrame)
                {
                    if (_data.RespawnAnim != ModelConditionFlag.None)
                    {
                        GameObject.ModelConditionFlags.Set(_data.RespawnAnim, false);
                    }
                    _phase = RespawnPhase.Alive;
                }
                break;
        }

        // Sleep whenever there is no timer to run down. AwaitingRevive is woken by
        // BeginRevive; Alive by ClaimDeath; PermanentlyDead never wakes again.
        return _phase is RespawnPhase.DeathAnimation or RespawnPhase.Reviving or RespawnPhase.RespawnAnimation
            ? UpdateSleepTime.None
            : UpdateSleepTime.Forever;
    }

    /// <summary>
    /// The death presentation is over: the hero goes hidden (and stays unselectable, which
    /// OnDie already set) and either starts its own countdown (AutoSpawn:Yes - no order, no
    /// money) or waits for a purchase.
    /// </summary>
    private void EnterAwaitingRevive()
    {
        if (_data.DeathAnim != ModelConditionFlag.None)
        {
            GameObject.ModelConditionFlags.Set(_data.DeathAnim, false);
        }

        GameObject.Hidden = true;

        var rules = _data.RespawnRules;
        if (rules is { AutoSpawn: true })
        {
            _phase = RespawnPhase.Reviving;
            _phaseEndFrame = Context.CurrentFrame + rules.Time;
            _anchorObjectId = ObjectId.Invalid;
            _paidCost = 0;
            return;
        }

        _phase = RespawnPhase.AwaitingRevive;
    }

    private void CompleteRevive()
    {
        _paidCost = 0;
        _anchorObjectId = ObjectId.Invalid;

        // H4: the ONLY way out of the dead state. ActiveBody recomputes IsEffectivelyDead from
        // its Fix64 health ledger on every health change, so restoring health through the body
        // is what clears it - and the same call re-arms the body's permanence resolver so a
        // later death is judged on its own killing blow.
        if (GameObject.BodyModule is RespawnBody body)
        {
            body.Revive(_data.RespawnRules?.HealthPercent ?? 100);
        }

        GameObject.Hidden = false;

        // Restore the template's OWN selectability rather than forcing true: OnDie clears the
        // flag unconditionally, but a template that was never selectable must not become
        // selectable by dying and coming back.
        GameObject.SetSelectable(GameObject.Definition.KindOf?.Get(ObjectKinds.Selectable) ?? false);

        if (!string.IsNullOrEmpty(_data.RespawnFX))
        {
            Context.Events.FireFXAtObject(_data.RespawnFX, GameObject.Id);
        }

        if (_data.RespawnAnim != ModelConditionFlag.None)
        {
            GameObject.ModelConditionFlags.Set(_data.RespawnAnim, true);
        }

        if (_data.RespawnAnimationTime > LogicFrameSpan.Zero)
        {
            _phase = RespawnPhase.RespawnAnimation;
            _phaseEndFrame = Context.CurrentFrame + _data.RespawnAnimationTime;
            return;
        }

        if (_data.RespawnAnim != ModelConditionFlag.None)
        {
            GameObject.ModelConditionFlags.Set(_data.RespawnAnim, false);
        }
        _phase = RespawnPhase.Alive;
    }

    /// <summary>
    /// The <c>RespawnEntry</c> that prices this hero at its current level, or null when the
    /// data declares none (which is every shipped AotR object - see the ModuleData header's
    /// census note: all RespawnEntry lines in AotR 8.0 are commented out).
    /// </summary>
    /// <remarks>
    /// Level is read as <c>Rank + 1</c>, i.e. REGULAR is level 1. That reading is what the data
    /// itself indicates: the (commented) shipped entries run Level:2..Level:10 and never
    /// declare Level:1, which is exactly the shape of "level 1 is the RespawnRules default".
    /// The highest entry at or below the hero's level wins, so a sparse table degrades to the
    /// nearest lower tier rather than falling all the way back to the default.
    /// </remarks>
    private RespawnEntry? SelectEntry()
    {
        var level = (int)GameObject.Rank + 1;

        RespawnEntry? best = null;
        foreach (var entry in _data.RespawnEntries)
        {
            if (entry.Level > level)
            {
                continue;
            }

            if (best is null || entry.Level > best.Level)
            {
                best = entry;
            }
        }

        return best;
    }

    // ---- the single walk (S4/§3): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's.
    //
    // Tolerances (ruling A3): phase, anchor identity and the paid cost are lifecycle/identity/
    // integer-money facts, so Exact. The phase-end frame is a timer, so Quantum - XferFrame's
    // own default.
    //
    // H2: this walk is where the awaiting-revive state lives. The seam deliberately adds NO
    // field to GameObject's per-object walk, so the Objects channel changes only by the module
    // state below (plus the fact that a killed-to-respawn hero is still IN the walk at all,
    // which is the intended, CRC-visible consequence of suppressing the reap).

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferEnum("Phase", ref _phase);
        xfer.XferFrame("PhaseEndFrame", ref _phaseEndFrame);
        xfer.XferObjectId("AnchorObjectId", ref _anchorObjectId);
        xfer.XferInt("PaidCost", ref _paidCost);
    }
}

/// <summary>The revive lifecycle's phases. Sim state; folded into the Objects CRC channel.</summary>
public enum RespawnPhase
{
    /// <summary>Not dead (or never was). The module sleeps.</summary>
    Alive,

    /// <summary>Claimed death: the death presentation is running, object still visible.</summary>
    DeathAnimation,

    /// <summary>Hidden, un-reaped, waiting for a revive purchase (AutoSpawn:No).</summary>
    AwaitingRevive,

    /// <summary>Countdown to reappearing, whether bought or automatic.</summary>
    Reviving,

    /// <summary>Alive again; the respawn presentation is running.</summary>
    RespawnAnimation,

    /// <summary>
    /// The death was permanent (or unrevivable), so it was NOT claimed and the ordinary corpse
    /// path handled it. Terminal.
    /// </summary>
    PermanentlyDead,
}
