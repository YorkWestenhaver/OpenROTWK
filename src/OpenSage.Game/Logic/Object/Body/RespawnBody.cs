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
// SCOPE / DEFERRAL (finding F-RSB-1, recorded in modules-r8/RespawnBody.md): the full respawn
// LIFECYCLE - suppressing the reap, the revive timer, the fortress purchase cost, hidden
// "dead-but-respawning" state - is driven by systems not landed on this tip (a RespawnUpdate
// special-power / hero-revival path). This port implements only the piece the task packet
// scopes ("respawn-on-death gated by PermanentlyKilledByFilter ... depends only on S1
// health/kill resolution"): the deterministic permanence DECISION at kill time, persisted so
// the future lifecycle reads a save-stable verdict. `CanRespawn` (Rotwk data) is parsed and
// carried but its default/interaction is binary-unpinned (finding F-RSB-2); it is deliberately
// NOT folded into the permanence decision here to avoid inventing behavior for the common
// pre-Rotwk case where the field is absent yet the hero respawns.

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

    internal RespawnBody(GameObject gameObject, IGameEngine gameEngine, RespawnBodyModuleData moduleData)
        : base(gameObject, gameEngine, moduleData)
    {
        _moduleData = moduleData;
    }

    /// <summary>
    /// True once a killing blow matching <see cref="RespawnBodyModuleData.PermanentlyKilledByFilter"/>
    /// has landed. The revival subsystem reads this to decide eligibility.
    /// </summary>
    public bool IsPermanentlyKilled => _permanentlyKilled;

    public override DamageInfoOutput AttemptDamage(in DamageInfoInput damageInput)
    {
        // Observe the lethal transition through S1's own resolution rather than predicting it:
        // let ActiveBody apply armor / scalar / health (all Fix64), then check whether THIS hit
        // is the one that crossed the object into death. Consuming the landed kill resolution is
        // the task's mandate ("depends only on S1 health/kill resolution"); we never re-derive
        // the health math.
        var wasDead = GameObject.IsEffectivelyDead;

        var damageOutput = base.AttemptDamage(damageInput);

        // Only the killing blow resolves permanence, and only once. A body already at/over 1 HP
        // that survives leaves the verdict untouched; a body already dead never re-latches.
        if (!wasDead && GameObject.IsEffectivelyDead && !_permanentlyKilled)
        {
            ResolvePermanence(damageInput);
        }

        return damageOutput;
    }

    private void ResolvePermanence(in DamageInfoInput damageInput)
    {
        var filter = _moduleData.PermanentlyKilledByFilter;
        if (filter == null)
        {
            // No filter => no source can make the death permanent (respawn always allowed).
            return;
        }

        // The killer is the damage source. If it is gone/unresolved there is nothing to test
        // the filter against, so the death is not permanent.
        var killer = GameEngine.GameLogic.GetObjectById(damageInput.SourceID);
        if (killer == null)
        {
            return;
        }

        _permanentlyKilled = filter.Matches(killer);
    }

    // ---- contract Xfer walk: own version, then the base ActiveBody walk (Fix64 ledger +
    // crush/indestructible/armor flags), then our one bool. Declaration order is ours (F9). ----

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        base.Xfer(xfer);
        xfer.XferBool("PermanentlyKilled", ref _permanentlyKilled); // Exact (A3): a bool has no quantum gap
    }

    internal override void Load(StatePersister reader)
    {
        // Retail .sav layout for this BFME2-only class is not recoverable from GPL; we keep the
        // legacy reader base-faithful (version + base) and persist our own latch after it. The
        // contract Xfer above is the authoritative persistence for our engine (F9).
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        reader.PersistBoolean(ref _permanentlyKilled);
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
