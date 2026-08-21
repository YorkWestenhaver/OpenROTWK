// KeepObjectDie - Die-batch port #3 (experiment-round-4 §4, replace-an-existing-module path).
//
// Behavioral reference: generals-gpl GeneralsMD KeepObjectDie.cpp/.h (GPL semantics reference
// only; this is fresh code against the frozen contract). Behavior facts used:
//   - MUTABLE STATE INVENTORY: empty. The GPL class declares no members at all; its whole
//     footprint is the DieModule base plus the module-data flyweight.
//   - onDie(): evaluates isDieApplicable(damageInfo) and then does NOTHING. The early return
//     on a filtered-out death and the fall-through on an applicable one are the same no-op.
//     That is not an unimplemented stub in the reference - it is the class's entire purpose,
//     stated by its own file header: an object that wants to leave rubble in the world needs
//     SOME die module, because a template with none falls back to DestroyDie, which removes
//     the object outright. KeepObjectDie is the "keep the corpse" marker: by occupying the
//     Die slot it displaces the destroying module, so the observable effect of a death is
//     that the object is still there afterwards.
//   - xfer(): version 1, then the base class. crc() and loadPostProcess() extend the base and
//     add nothing. So our walk is the version tag and nothing else, and that is complete.
//
// Because the state inventory is empty, the ONLY way this module can regress is by acquiring
// state, by failing to run, or by running when the die filter said it should not - which is
// what KeepObjectDieContractTests asserts (the corpse survives; DestroyDie in the same slot
// does not leave one; the DeathTypes/ExemptStatus filter is honored) and what the die-batch-v1
// scenario asserts across two engine processes.
//
// BFME2-only INI additions (CollapsingTime, StayOnRadar) have no GPL reference and no written
// behavioral spec: they are parsed (audited vocabulary) but deliberately not acted on - see
// die/KeepObjectDie.md, "behavior-fact gaps". Inventing rubble-collapse timing or radar
// persistence here would be exactly the invention the clean-room rule forbids.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class KeepObjectDie : DieModule
{
    // No fields: the mutable sim-state inventory is empty (see the header). The module data
    // is held by the DieModule base, which owns the DieLogicData filter; there is deliberately
    // no typed copy here because nothing in this class reads one.

    public KeepObjectDie(GameObject gameObject, ISimContext context, KeepObjectDieModuleData data)
        : base(gameObject, context, data)
    {
    }

    /// <summary>
    /// The die callback, reached only for a death the <see cref="DieLogicData"/> filter
    /// accepts. Deliberately empty: "keep the object" is what happens when no Die module
    /// destroys it, so the effect of this module is that it occupies the Die slot without
    /// removing anything. GPL <c>KeepObjectDie::onDie</c> is the same no-op.
    /// </summary>
    protected override void Die(in DamageInfoInput damageInput)
    {
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OURS (F9). The inventory is empty, so the walk is the
    // version tag alone. This is a complete walk, not an omission: a field appearing here
    // later means this class grew state, which is the review question, not a tolerance one.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
    }

    // ---- legacy retail-save reader (outside the contract, F9): kept and remapped per
    // template v1.1 D-9, because this port REPLACES an existing module that had one. Its
    // layout is version + base, unchanged - there were no fields to remap. ----
    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================
[SimDataAudited]
public sealed class KeepObjectDieModuleData : DieModuleData
{
    internal static KeepObjectDieModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<KeepObjectDieModuleData> FieldParseTable = DieModuleData.FieldParseTable
        .Concat(new IniParseTable<KeepObjectDieModuleData>
        {
            { "CollapsingTime", (parser, x) => x.CollapsingTime = parser.ParseInteger() },
            { "StayOnRadar", (parser, x) => x.StayOnRadar = parser.ParseBoolean() }
        });

    /// <summary>
    /// BFME2-only. Milliseconds, kept as time-as-int (F3) rather than quantized through
    /// <c>ParseDurationLogicFrames</c>: the field is unconsumed, and its unit and rounding
    /// are unproven - AotR writes it as <c>CollapsingTime = 10000</c>, which reads as ms but
    /// has no GPL formula behind it. Quantizing an unconsumed field would pin a rounding
    /// choice that no behavior fact supports. Same disposition as the pilot's
    /// <c>RespawnMinimumDelay</c>; it becomes a <c>LogicFrameSpan</c> in the commit that
    /// implements the behavior, per the S5 rule that rounding is pinned per formula.
    /// </summary>
    [AddedIn(SageGame.Bfme2)]
    public int CollapsingTime { get; private set; }

    /// <summary>
    /// BFME2-only, unconsumed: no GPL reference and no written behavioral spec exists for what
    /// the kept corpse does to the radar. Parsed so the INI corpus loads; not acted on.
    /// </summary>
    [AddedIn(SageGame.Bfme2)]
    public bool StayOnRadar { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new KeepObjectDie(gameObject, gameEngine.SimContext, this);
    }
}
