// HordeTransportContainDamage - R13 port (task packet modules-r13/specs/
// HordeTransportContainDamageModuleData.md). No GPL reference exists (Generals/ZH has no
// horde-transport concept; this is a BFME-only mechanic) - the spec is data-derivation, not a
// GPL citation: this Damage-family module's own FieldParseTable is legitimately empty (zero
// INI keys, nothing to parse), and its behavioral role - propagate a percentage of the actual
// damage the transport takes to every seated passenger - is inferred from (a) the sibling
// HordeTransportContainModuleData already carrying a parsed-but-unconsumed
// DamagePercentToUnits field with no other module that could plausibly consume it, and (b) the
// landed ProductionQueueHordeContain precedent doing exactly this for the same field name on a
// structurally identical Contain-family pairing.
//
// SEQUENCING GAP (spec §5, not a Ghidra-only unknown; UPDATE: HordeTransportContain has since
// landed its own runtime port - see HordeTransportContain.cs - so the compile-time blocker
// described below no longer applies; the OnDamage body itself is simply not written yet, a
// follow-up port task, not a sequencing dependency). At the time of this port, HordeTransportContain
// itself was still [ParseOnly] - it had no runtime class, so there was nothing this module could
// call GameObject.FindBehavior<HordeTransportContain>() against. This port therefore landed
// only the conservative, verifiable half of the spec: [ParseOnly] removal, CreateModule wiring,
// and the version-only Xfer walk (this module owns no mutable state of its own - the spec is
// explicit that the passenger list and DamagePercentToUnits both live on the sibling). The
// OnDamage BODY - the actual damage-propagation logic - remains unimplemented (falls through
// to DamageModule's no-op virtual); now that HordeTransportContain exposes a passenger-
// enumeration surface + Fix64-typed DamagePercentToUnits, a FindBehavior<HordeTransportContain>()
// call against it is possible and this is ready to pick up as a follow-up port task.

using OpenSage.Data.Ini;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

/// <summary>
/// Reacts to damage taken by a horde-transport object by splashing a configured percentage of
/// the actual damage dealt onto every seated passenger (per the sibling
/// <see cref="HordeTransportContainModuleData.DamagePercentToUnits"/>). Owns no INI-configured
/// data and no mutable sim state of its own - see the file header for why
/// <see cref="DamageModule.OnDamage"/> is still at its inherited no-op (a follow-up port task;
/// the sibling HordeTransportContain module has already landed its own runtime port).
/// </summary>
public sealed class HordeTransportContainDamage : DamageModule
{
    internal HordeTransportContainDamage(GameObject gameObject, ISimContext context, HordeTransportContainDamageModuleData data)
        : base(gameObject, context)
    {
        // `data` kept for signature symmetry with every other ported Damage module (matches
        // TransitionDamageFX's pattern) even though this module reads nothing off it today -
        // it carries zero INI-configured fields (empty FieldParseTable, see ModuleData below).
    }

    // ---- mutable sim state: NONE ----
    // Per spec §2: the passenger list and DamagePercentToUnits both live on the sibling
    // HordeTransportContain instance, which owns the [SimState] responsibility for them (the
    // ProductionQueueHordeContain precedent keeps the mutable slot list on the Contain-side
    // type). This class adds no field into the Xfer walk.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
    }
}

[AddedIn(SageGame.Bfme2Rotwk)]
public sealed class HordeTransportContainDamageModuleData : DamageModuleData
{
    internal static HordeTransportContainDamageModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    // Legitimately empty: this module carries zero INI-configured parameters (spec §0/§1).
    private static readonly IniParseTable<HordeTransportContainDamageModuleData> FieldParseTable = new IniParseTable<HordeTransportContainDamageModuleData>();

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new HordeTransportContainDamage(gameObject, gameEngine.SimContext, this);
    }
}
