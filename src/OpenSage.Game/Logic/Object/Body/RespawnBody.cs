// RespawnBody - Round-8 Body-batch port to the frozen module contract (api-freeze-v1 §3/§5,
// template v1.1 = pilot-autoheal §3/§6). Builds ON S1 (weapon/damage/armor): it consumes the
// landed ActiveBody kill-resolution surface and does NOT reimplement damage math.
//
// Behavioral reference: BFME/BFME2-only class - ABSENT from generals-gpl (no ZH ancestor).
// Semantics are therefore from the binary-derived behavioral spec only (facts, never code);
// clean-room, fresh code. The one determinism-relevant fact this Body owns:
//
//   On the killing blow, RespawnBody consults PermanentlyKilledByFilter against the KILLER
//   object. If the killer matches the filter, the death is PERMANENT (the hero cannot be
//   revived); otherwise the object is respawn-eligible. This is a pure S1-kill-resolution
//   rider: it reads the same lethal transition ActiveBody already computes and records a
//   single bool of intent that the (not-yet-landed) respawn/revival subsystem will consume.
//
// MUTABLE SIM STATE INVENTORY: exactly one field of its own, `_permanentlyKilled` (bool).
// The Fix64 health ledger lives in the base ActiveBody's BodyDamageCore (walked by the base).
// So RespawnBody adds one bool to the contract Xfer walk.
//
// SCOPE (was F-RSB-1, DISCHARGED in R14 by the respawn seam): the respawn LIFECYCLE now
// exists - RespawnUpdate implements IReviveLifecycleModule and drives the death/await/revive
// phases - and this Body owns two things for it: the permanence verdict, and the revive
// itself. `CanRespawn` (Rotwk data) is parsed and carried but its default/interaction is
// binary-unpinned (finding F-RSB-2); it is deliberately NOT folded into the permanence
// decision here to avoid inventing behavior for the common pre-Rotwk case where the field is
// absent yet the hero respawns. Whether ClaimDeath should consult it stays open (OQ-9).
//
// R14 CONTRACT CHANGE (wave-2a adversarial review, finding H1 - this reopens the landed R8
// contract on purpose, and the contract tests are updated with it):
//
//   The permanence verdict used to be resolved AFTER `base.AttemptDamage` returned. That is
//   too late for the seam: ActiveBody calls `obj.OnDie` from INSIDE that base call
//   (ActiveBody.AttemptDamage, at the health-crossed-zero check), so at OnDie time - the only
//   moment the reap can still be suppressed - `_permanentlyKilled` was still false for EVERY
//   death, and a claim predicate reading it would have claimed and stranded permanent deaths
//   too. The resolution therefore moved AHEAD of OnDie: `ResolvePermanenceForDeath` is the
//   public, damage-driven entry point, called by RespawnUpdate.ClaimDeath from within OnDie.
//   The post-base check in AttemptDamage is kept as the FALLBACK for a RespawnBody whose
//   object carries no revive-lifecycle module at all (a data shape the census does contain),
//   so the verdict is still latched for every lethal blow, exactly once per death.
//
//   The "exactly once per death" bookkeeping is `_permanenceResolved`, a second bool of sim
//   state. It is what makes a SECOND death resolve correctly: `Revive` clears it, so a hero
//   who dies non-permanently, is revived, and is then killed by a filter-matching source
//   latches the permanent verdict on that second death instead of being skipped by a stale
//   latch.

#nullable enable

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

/// <summary>
/// Body of a respawnable hero. Takes damage exactly like <see cref="ActiveBody"/>; its only
/// addition is that, on the killing blow, it records whether the death was permanent by
/// testing the killer against <see cref="RespawnBodyModuleData.PermanentlyKilledByFilter"/>.
/// </summary>
[SimState]
public sealed class RespawnBody : ActiveBody
{
    private readonly RespawnBodyModuleData _moduleData;

    /// <summary>
    /// Latched on the killing blow: true when the killer matched
    /// <see cref="RespawnBodyModuleData.PermanentlyKilledByFilter"/>, meaning the object may not
    /// be revived. Sim state (the revival subsystem's input) and folded into the Objects CRC
    /// channel.
    /// </summary>
    private bool _permanentlyKilled;

    /// <summary>
    /// Whether the CURRENT death has already had its permanence resolved. Guards the
    /// once-per-death rule now that the resolution has two entry points (the OnDie-time
    /// <see cref="ResolvePermanenceForDeath"/> and the post-base fallback in
    /// <see cref="AttemptDamage"/>), and is cleared by <see cref="Revive"/> so a later death
    /// resolves on its own merits. Sim state; folded into the Objects CRC channel.
    /// </summary>
    private bool _permanenceResolved;

    internal RespawnBody(GameObject gameObject, IGameEngine gameEngine, RespawnBodyModuleData moduleData)
        : base(gameObject, gameEngine, moduleData)
    {
        _moduleData = moduleData;
    }

    /// <summary>
    /// True once a killing blow matching <see cref="RespawnBodyModuleData.PermanentlyKilledByFilter"/>
    /// has landed. The revival subsystem reads this to decide eligibility. Terminal: a
    /// permanent death is never revived, so this is never cleared.
    /// </summary>
    public bool IsPermanentlyKilled => _permanentlyKilled;

    /// <summary>
    /// Whether the permanence verdict for the death in progress has been resolved yet.
    /// Diagnostic/test surface for the once-per-death rule; see the H1 note in the file header.
    /// </summary>
    public bool IsPermanenceResolved => _permanenceResolved;

    public override DamageInfoOutput AttemptDamage(in DamageInfoInput damageInput)
    {
        // Observe the lethal transition through S1's own resolution rather than predicting it:
        // let ActiveBody apply armor / scalar / health (all Fix64), then check whether THIS hit
        // is the one that crossed the object into death. Consuming the landed kill resolution is
        // the task's mandate ("depends only on S1 health/kill resolution"); we never re-derive
        // the health math.
        var wasDead = GameObject.IsEffectivelyDead;

        var damageOutput = base.AttemptDamage(damageInput);

        // FALLBACK ONLY (H1). ActiveBody calls GameObject.OnDie from inside the base call
        // above, so an object carrying a revive-lifecycle module has ALREADY resolved
        // permanence by now (through ResolvePermanenceForDeath) and _permanenceResolved is
        // set. This branch therefore fires only for a RespawnBody whose object has no such
        // module - the verdict is still latched, exactly once, so the persisted state is the
        // same either way. A body already at/over 1 HP that survives leaves the verdict
        // untouched; a body already dead never re-latches.
        if (!wasDead && GameObject.IsEffectivelyDead && !_permanenceResolved)
        {
            ResolvePermanenceForDeath(damageInput);
        }

        return damageOutput;
    }

    /// <summary>
    /// Resolves and latches whether the death caused by <paramref name="damageInput"/> is
    /// permanent, and returns the verdict. Idempotent within one death: a second call before
    /// a <see cref="Revive"/> returns the already-latched verdict without re-testing the
    /// filter (so the two entry points can never disagree, and no extra work happens on a
    /// double lethal blow).
    /// </summary>
    /// <remarks>
    /// This is the H1 seam. It exists so <c>IReviveLifecycleModule.ClaimDeath</c> - which runs
    /// inside <see cref="GameObject.OnDie"/>, i.e. inside <c>base.AttemptDamage</c> - can
    /// decide permanence from the killing blow it was handed, rather than from a latch that is
    /// still false at that instant for every death.
    /// </remarks>
    public bool ResolvePermanenceForDeath(in DamageInfoInput damageInput)
    {
        if (_permanenceResolved)
        {
            return _permanentlyKilled;
        }

        _permanenceResolved = true;

        var filter = _moduleData.PermanentlyKilledByFilter;
        if (filter == null)
        {
            // No filter => no source can make the death permanent (respawn always allowed).
            return _permanentlyKilled;
        }

        // The killer is the damage source. If it is gone/unresolved there is nothing to test
        // the filter against, so the death is not permanent.
        var killer = GameEngine.GameLogic.GetObjectById(damageInput.SourceID);
        if (killer == null)
        {
            return _permanentlyKilled;
        }

        _permanentlyKilled = filter.Matches(killer);
        return _permanentlyKilled;
    }

    /// <summary>
    /// Brings this body back from a non-permanent death, restoring health to
    /// <paramref name="healthPercent"/> percent of <c>InitialHealth</c>
    /// (<c>RespawnRules Health:</c>).
    /// </summary>
    /// <remarks>
    /// OQ-1 was decided in favour of IN-PLACE survival (dr-0033), so the exit from the dead
    /// state has to be specified rather than assumed - that is the review's H4 finding. It is
    /// specified HERE, through the body, because <c>GameObject.IsEffectivelyDead</c> is not an
    /// independently settable flag in this engine: <c>ActiveBody</c> recomputes it from the
    /// Fix64 health ledger on every health change
    /// (<c>ApplyHealthChangeSideEffects</c>: <c>IsEffectivelyDead = CurrentHealth &lt;= 0</c>).
    /// Restoring health through the landed <see cref="ActiveBody.SetInitialHealth"/> path is
    /// therefore what clears the dead state, and it clears the damage state and re-evaluates
    /// the visual condition in the same pass - all of it exact Fix64 (the percent is applied
    /// by <c>BodyDamageCore.SetInitialHealthPercent</c>'s Int128 mul-div, never a float
    /// ratio). Setting a bit directly would leave the health ledger at zero and the object
    /// would read as dead again on its next damage.
    /// <para>
    /// The permanence resolver is re-armed first, so a LATER death - the second-death latch
    /// the review asked for - is resolved on its own killing blow instead of being skipped.
    /// A permanently-killed body is never revived, and asserts loudly if one is asked to be.
    /// </para>
    /// <para>
    /// A <paramref name="healthPercent"/> of zero leaves the body dead. That is not a guarded
    /// case: it is what the arithmetic says, and the shipped corpus has no such data (all 547
    /// AotR <c>RespawnRules</c> declarations read <c>Health:100%</c>), so clamping it would be
    /// inventing a rule for input that does not exist.
    /// </para>
    /// </remarks>
    public void Revive(int healthPercent)
    {
        DebugUtility.AssertCrash(!_permanentlyKilled, "Reviving a permanently-killed RespawnBody");

        _permanenceResolved = false;
        SetInitialHealth(healthPercent);
    }

    // ---- contract Xfer walk: own version, then the base ActiveBody walk (Fix64 ledger +
    // crush/indestructible/armor flags), then our one bool. Declaration order is ours (F9). ----

    public override void Xfer(IXfer xfer)
    {
        // Version 2 (R14): adds the per-death permanence-resolution guard. Both bools are
        // mutable sim state and both must fold into the Objects CRC channel, or a peer that
        // resolved a death and a peer that has not would compare equal.
        xfer.XferVersion(2);
        base.Xfer(xfer);
        xfer.XferBool("PermanentlyKilled", ref _permanentlyKilled);   // Exact (A3): a bool has no quantum gap
        xfer.XferBool("PermanenceResolved", ref _permanenceResolved); // Exact (A3)
    }

    internal override void Load(StatePersister reader)
    {
        // Retail .sav layout for this BFME2-only class is not recoverable from GPL; we keep the
        // legacy reader base-faithful (version + base) and persist our own latches after it. The
        // contract Xfer above is the authoritative persistence for our engine (F9).
        reader.PersistVersion(2);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        reader.PersistBoolean(ref _permanentlyKilled);
        reader.PersistBoolean(ref _permanenceResolved);
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class RespawnBodyModuleData : ActiveBodyModuleData
{
    internal static new RespawnBodyModuleData Parse(IniParser parser)
    {
        var result = parser.ParseBlock(FieldParseTable);
        result.ApplyHealthDefaults(parser);   // F-HB-1 / F-R7-2: the shadowing Parse must keep the base InitialHealth defaulting.
        return result;
    }

    private static new readonly IniParseTable<RespawnBodyModuleData> FieldParseTable = ActiveBodyModuleData.FieldParseTable
        .Concat(new IniParseTable<RespawnBodyModuleData>
        {
            { "PermanentlyKilledByFilter", (parser, x) => x.PermanentlyKilledByFilter = ObjectFilter.Parse(parser) },
            { "CanRespawn", (parser, x) => x.CanRespawn = parser.ParseBoolean() }
        });

    /// <summary>Objects whose killing blow makes this body's death permanent (no revival).</summary>
    public ObjectFilter? PermanentlyKilledByFilter { get; private set; }

    /// <summary>
    /// Rotwk data gate for the respawn subsystem. Parsed and carried; its default and exact
    /// interaction with the permanence decision are binary-unpinned (finding F-RSB-2) and are
    /// therefore not consumed by <see cref="RespawnBody"/> yet.
    /// </summary>
    [AddedIn(SageGame.Bfme2Rotwk)]
    public bool CanRespawn { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new RespawnBody(gameObject, gameEngine, this);
    }
}
