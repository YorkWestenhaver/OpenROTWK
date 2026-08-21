// SymbioticStructuresBody - R8 Body-batch port to the frozen module contract (api-freeze-v1
// §3/§5, template v1.1 = pilot-autoheal §3/§6). Builds ON S1 (weapon/damage/armor): it
// consumes the landed ActiveBody / BodyDamageCore health-application surface and does NOT
// reimplement damage math.
//
// Behavioral reference: BFME/BFME2-ONLY module, ABSENT from generals-gpl (no ZH ancestor).
// Binary-derived spec reference only, and no behavioral dump for this class exists yet, so
// the one interesting semantic - the "Symbiote" death link - has no evidenced specification.
// AotR INI usage (evidence): wall/keep segments (IsengardTowerOfOrthanc*, Gondor/Arnor camps
// and castles) declare `Body = SymbioticStructuresBody { Symbiote = KeepLeft }`, i.e. the
// Symbiote value is a *template/object handle* ("KeepLeft") the dying structure is coupled to.
//
// SCOPE DECISION (task packet, explicit rule): the death->Symbiote link resolves a coupled
// object and therefore requires object association / a partition-or-registry lookup that is
// OUTSIDE pure S1. The packet says verbatim: "Exclude if it turns out to need partition object
// lookup." It does. So the death-link BEHAVIOR is DEFERRED (finding F-SSB-1); this port lands
// the audited ModuleData, the F-R7-2 InitialHealth default fix, and a thin runtime body that is
// behaviorally an ActiveBody (health ledger only), giving the deferred hook a typed home.
//
// MUTABLE SIM STATE INVENTORY: none of its own. With the Symbiote link deferred, this body adds
// no field over ActiveBody's Fix64 health ledger (the BodyDamageCore in the base). So it adds
// nothing to the Xfer walk - it only re-versions and chains the base (ImmortalBody/Hive shape).

#nullable enable

using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

/// <summary>
/// A structure body (BFME/BFME2-only) that couples the structure to a named "symbiote" object
/// so their fates are linked. The coupling/death-propagation is DEFERRED (finding F-SSB-1: it
/// needs object association beyond S1); at runtime this behaves as an <see cref="ActiveBody"/>.
/// </summary>
public sealed class SymbioticStructuresBody : ActiveBody
{
    // Held for the deferred Symbiote death-link (F-SSB-1). Immutable ModuleData reference, so it
    // is NOT sim state and contributes nothing to the Xfer walk.
    private readonly SymbioticStructuresBodyModuleData _moduleData;

    internal SymbioticStructuresBody(GameObject gameObject, IGameEngine gameEngine, SymbioticStructuresBodyModuleData moduleData)
        : base(gameObject, gameEngine, moduleData)
    {
        _moduleData = moduleData;
    }

    // ---- the contract Xfer walk. With the Symbiote link deferred this body owns no mutable sim
    // state of its own, so there is no field to add to the walk - only a version wrapper (F9:
    // declaration order, ours) over the base ActiveBody walk. HasSimXfer is inherited (true) from
    // ActiveBody. The version NUMBER is the batch convention (1); no spec evidence pins the
    // BFME xfer version for this class (F-SSB-2), and self-diff (Target-A) is invariant to the
    // absolute version value. ----

    public override void Xfer(SimCore.Sync.IXfer xfer)
    {
        xfer.XferVersion(1);
        base.Xfer(xfer);
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

/// <summary>
/// Structure body that links its object to a named symbiote. Adds the single <c>Symbiote</c>
/// INI field over <see cref="ActiveBodyModuleData"/>; the field is parsed and retained but not
/// yet consumed (the death link is deferred - finding F-SSB-1).
/// </summary>
[SimDataAudited]
public sealed class SymbioticStructuresBodyModuleData : ActiveBodyModuleData
{
    internal static new SymbioticStructuresBodyModuleData Parse(IniParser parser)
    {
        var result = parser.ParseBlock(FieldParseTable);
        result.ApplyHealthDefaults(parser);   // F-R7-2 / F-HB-1: shadowing Parse must re-apply the BFME InitialHealth=MaxHealth default.
        return result;
    }

    private static new readonly IniParseTable<SymbioticStructuresBodyModuleData> FieldParseTable = ActiveBodyModuleData.FieldParseTable
        .Concat(new IniParseTable<SymbioticStructuresBodyModuleData>
        {
            // Object/template handle of the coupled symbiote (e.g. "KeepLeft"). Asset reference
            // vocabulary (S5): a plain named handle, no numeric quantization. Parsed and retained;
            // the death link that consumes it is deferred (F-SSB-1).
            { "Symbiote", (parser, x) => x.Symbiote = parser.ParseAssetReference() }
        });

    /// <summary>Template/object handle of the coupled symbiote; parsed, retained, not yet consumed (F-SSB-1).</summary>
    public string Symbiote { get; private set; } = "";

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new SymbioticStructuresBody(gameObject, gameEngine, this);
    }
}
