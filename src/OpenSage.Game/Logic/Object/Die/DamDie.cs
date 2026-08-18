// DamDie - Die-batch class 8 (experiment-round-4 §4, "smallest file in the dir").
//
// Behavioral reference: generals-gpl GeneralsMD .../Object/Die/DamDie.cpp|.h (GPL semantics
// reference only; this is fresh code against the frozen contract). Behavior facts used:
//   - DamDieModuleData adds NO fields of its own: buildFieldParse forwards to
//     DieModuleData::buildFieldParse and its own field table is commented out in the GPL
//     source. So the whole parse surface is the shared Die gate (DeathTypes /
//     RequiredStatus / ExemptStatus).
//   - onDie(): returns immediately unless isDieApplicable(damageInfo); then walks the whole
//     object list and, for every object that isKindOf(KINDOF_WAVEGUIDE), calls
//     clearDisabled(DISABLED_DEFAULT). Nothing else - no FX, no spawn, no self-effect.
//     ("The big water dam dying": the dam's death releases the map's pre-placed, initially
//     disabled water-wave objects, which then run their own WaveGuideUpdate.)
//   - The class has NO mutable member state: its xfer is version 1 plus the base walk, and
//     its crc/loadPostProcess are pure base forwards. That is the entire state inventory,
//     and it is why the Xfer below writes a version and nothing else.
//   - The header comments the ordering constraint that shows up in INI, not in code: the
//     module "must be applied after any other death modules" - a data-authoring rule
//     (module declaration order), not a runtime one; we neither enforce nor need it,
//     because ModuleIndex order is already the deterministic dispatch order.
//
// Determinism notes (api-freeze-v1 §3/§6):
//   - The whole-world walk uses Context.GameLogic.ObjectsAscendingId, the one blessed
//     iteration (design-module-api §6). The effect is order-independent anyway (an
//     idempotent per-object bit clear), but the blessed iterator is the contract.
//   - No RNG, no floats, no timers, no partition query, no client output.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class DamDie : DieModule
{
    // ---- mutable sim state: NONE. The GPL class declares no members, and this port adds
    // none, so the Xfer walk below is complete by construction. ----

    public DamDie(GameObject gameObject, ISimContext context, DamDieModuleData data)
        : base(gameObject, context, data)
    {
    }

    /// <summary>
    /// GPL onDie(), past the shared applicability gate (which <see cref="DieModule"/> applies
    /// before calling us): re-enable every water-wave object on the map.
    /// </summary>
    protected override void Die(in DamageInfoInput damageInput)
    {
        foreach (var candidate in Context.GameLogic.ObjectsAscendingId)
        {
            // Only care about water waves.
            if (!candidate.Definition.KindOf.Get(ObjectKinds.WaveGuide))
            {
                continue;
            }

            // Clear any disabled status of the water wave.
            candidate.ClearDisabled(DisabledType.Default);
        }
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9). There are no fields; the version
    // byte alone is the walk, exactly as the GPL xfer is version-plus-base. The module still
    // joins the Objects channel (HasSimXfer true) so its PRESENCE - creation, survival, and
    // disappearance with its object - is folded into the CRC.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
    }

    // ---- legacy retail-save reader (outside the contract, F9): kept and remapped per the
    // template's replace-an-existing-module rule (pilot-autoheal D-9). DamDie's retail
    // layout is version byte + base object, and there is no field to remap onto. ----
    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight (design-module-api §2.2).
//
// S5 vocabulary audit: DamDie declares no fields of its own, so there is no numeric,
// duration, angle or vector field to quantize here - the audit is vacuously satisfied and
// [SimDataAudited] records that it was performed, not that it was empty. The inherited
// DieModuleData table (DeathTypes / RequiredStatus / ExemptStatus) is enum/bitfield data:
// already exact, never float, and shared by the whole Die category.
// ============================================================================

/// <summary>
/// Allows object to continue to exist as an obstacle but allowing water terrain to move
/// through. The module must be applied after any other death modules.
/// </summary>
[SimDataAudited]
public sealed class DamDieModuleData : DieModuleData
{
    internal static DamDieModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<DamDieModuleData> FieldParseTable = DieModuleData.FieldParseTable
        .Concat(new IniParseTable<DamDieModuleData>());

    internal override DamDie CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new DamDie(gameObject, gameEngine.SimContext, this);
    }
}
