// DestroyDie - the Die batch's second port (experiment-round-4 §4, "near-stateless").
//
// Behavioral reference: generals-gpl GeneralsMD DestroyDie.cpp/.h (GPL semantics reference
// only; this is fresh code against the frozen contract). The whole class, as facts:
//   - ctor: nothing (no state, no RNG draw, no wake registration - it is not an UpdateModule).
//   - onDie(damageInfo): if the shared Die filter rejects this death, return; otherwise ask
//     GameLogic to destroy the owning object. That is the entire behavior.
//   - crc()/xfer(): version 1 plus the base walk, and the base walk carries nothing. So the
//     mutable-state inventory of this module is EMPTY, and the whole Xfer walk is the
//     version byte. That is not an oversight to be corrected by inventing a "hasFired" flag:
//     the object is gone the moment this module runs, and re-entry is prevented by the
//     health crossing in ActiveBody, not by module state.
//
// The INI branches this class actually has are all in the shared filter (DieLogicData on the
// base): DeathTypes / RequiredStatus / ExemptStatus. AotR's loose INI tree uses exactly one
// of them - DeathTypes, in 187 of 695 DestroyDie blocks - and the class carries no fields of
// its own in any title, so the audited parse table below is empty by construction.
//
// Every mutable sim field appears in Xfer exactly once (§3) - vacuously true here; tolerances
// are the field's conformance class at its declaration site (§4).

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class DestroyDie : DieModule
{
    // ---- mutable sim state: NONE. The inventory is empty and Xfer reflects that. ----

    public DestroyDie(GameObject gameObject, ISimContext context, DestroyDieModuleData data)
        : base(gameObject, context, data)
    {
    }

    /// <summary>
    /// Reached only for a death the shared filter accepted (DieModule applies
    /// DeathTypes/RequiredStatus/ExemptStatus before dispatching here).
    /// </summary>
    protected override void Die(in DamageInfoInput damageInput)
    {
        // GPL onDie: TheGameLogic->destroyObject(obj). The object is marked destroyed now and
        // reaped from the object list at end of frame; the request is idempotent, so a Die
        // list containing two DestroyDie modules destroys once.
        Context.GameLogic.DestroyObject(GameObject);
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9). There are no fields, so the walk is
    // the version byte alone - the batch's zero-state datum, and a real walk: the module
    // still enters the Objects channel, still round-trips, and a future field added without
    // a matching Xfer line fails the shadow-copy test.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
    }

    // ---- legacy retail-save reader (outside the contract, F9): kept and remapped per
    // template rule D-9, since this port replaces an existing module. The original stream is
    // version + the base module's version block, and no payload. ----
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

/// <summary>
/// DestroyDie adds no INI fields of its own in any SAGE title: its whole parse surface is
/// the inherited Die filter (DieModuleData -> DieLogicData: DeathTypes, RequiredStatus,
/// ExemptStatus), which is enum/bit-array data and therefore needs no S5 quantization -
/// there is no time, distance, percentage or angle to quantize. Audited on that basis.
/// </summary>
[SimDataAudited]
public sealed class DestroyDieModuleData : DieModuleData
{
    internal static DestroyDieModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<DestroyDieModuleData> FieldParseTable = DieModuleData.FieldParseTable
        .Concat(new IniParseTable<DestroyDieModuleData>());

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new DestroyDie(gameObject, gameEngine.SimContext, this);
    }
}
