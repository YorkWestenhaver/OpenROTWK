// CreateObjectDie - Die-batch task 4, ported to the frozen module contract.
//
// Behavioral reference: generals-gpl GeneralsMD CreateObjectDie.cpp/.h (GPL semantics
// reference only; this is fresh code against the frozen contract). Behavior facts used:
//   - onDie(): applicability filter first (the DieModule family runs it), then look up the
//     damage dealer by the damage's source id, then run the CreationList with THIS object
//     as primary and the damage dealer as secondary.
//   - the list's FIRST created object is the one the module keeps looking at; everything
//     else it spawned is on its own.
//   - TransferPreviousHealth (only when something was created), in this order:
//       1. donor's current subdual damage, as unresistable subdual damage from no source;
//       2. donor's (max health - PREVIOUS health), as unresistable damage credited to
//          whoever last damaged the donor. Previous, not current: the donor is at zero by
//          the time a Die module runs, so current health would transfer a full-health
//          deficit onto a healthy replacement.
//       Each leg is applied only when its amount is positive.
//       3. re-point every attacker from the old object to the new one - NOT PORTED, see
//          finding F-CODIE-2: that is AIUpdate::transferAttack, and the AIUpdate
//          sub-surface is deliberately unfrozen (experiment-round-4 §5) so there is no
//          contract to call through. It is a doc note, not an invention.
//   - the module keeps NO mutable state: the original's xfer is a version stamp over an
//     empty base, and its crc is the base's. Ours says the same thing (see Xfer below).
// BFME-only INI additions (DebrisPortionOfSelf) and BFME2-only ones (UpgradeRequired) have
// no GPL reference and no written behavioral spec: they are parsed (audited vocabulary) but
// deliberately not acted on - see research/die/CreateObjectDie.md, "behavior-fact gaps".
//
// Every mutable sim field appears in Xfer exactly once (§3) - here that is the empty set.

using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class CreateObjectDie : DieModule
{
    private readonly CreateObjectDieModuleData _data;

    // ---- mutable sim state: NONE. ----
    // The module is a pure reaction to OnDie: it reads the module data and the world, acts,
    // and remembers nothing. Nothing to xfer, nothing to diverge, nothing to restore.

    public CreateObjectDie(GameObject gameObject, ISimContext context, CreateObjectDieModuleData data)
        : base(gameObject, context, data)
    {
        _data = data;
    }

    protected override void Die(in DamageInfoInput damageInput)
    {
        // The applicability filter (DeathTypes / RequiredStatus / ExemptStatus) already ran
        // in DieModule's OnDie dispatch, so reaching here means this death counts.

        // GPL findObjectByID: a source that is invalid, or that already left the world,
        // is simply no secondary object.
        var damageDealer = damageInput.SourceID.IsValid
            ? Context.GameLogic.GetObjectById(damageInput.SourceID)
            : null;

        var created = Context.GameLogic.CreateFromObjectCreationList(
            _data.CreationList?.Value,
            GameObject,
            damageDealer);

        if (created.Count == 0 || !_data.TransferPreviousHealth)
        {
            return;
        }

        // "The new object" is the list's first creation, matching the original, whose
        // create() returns the first object made.
        var newObject = created[0];

        // Float boundary (D-7): the module owns the branch structure and the ordering; the
        // amounts live behind GameObject's transfer facades because Body is unmigrated.
        if (GameObject.HasSubdualDamage)
        {
            newObject.TransferSubdualDamageFrom(GameObject);
        }

        if (GameObject.HasPreviousHealthDeficit)
        {
            newObject.TransferPreviousHealthFrom(GameObject);
        }

        // Attacker hand-off (GPL's third leg) is not portable yet - F-CODIE-2.
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9). The state inventory is empty, so
    // the walk is the version stamp alone - which is exactly what the original writes too.
    // The module still declares HasSimXfer: an object carrying it must appear in the Objects
    // channel with a stable, non-empty contribution, so that the object's ARRIVAL and
    // DEPARTURE from the walk are observable to the harness. A stateless module that opted
    // out of the walk would make its own death invisible.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
    }

    // ---- legacy retail-save reader (outside the contract, F9): kept and remapped per the
    // template's replace-an-existing-module rule (D-9). The original's xfer is a version
    // stamp over the base, and so is this. ----
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
// Vocabulary note (S5): this class has no numeric fields at all - no duration, no
// distance, no percentage - so no quantizing parse function applies. The audit is
// therefore a statement that the absence was checked, not that a conversion happened.
// ============================================================================
[SimDataAudited]
public sealed class CreateObjectDieModuleData : DieModuleData
{
    internal static CreateObjectDieModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<CreateObjectDieModuleData> FieldParseTable = DieModuleData.FieldParseTable
        .Concat(new IniParseTable<CreateObjectDieModuleData>
        {
            { "CreationList", (parser, x) => x.CreationList = parser.ParseObjectCreationListReference() },
            { "TransferPreviousHealth", (parser, x) => x.TransferPreviousHealth = parser.ParseBoolean() },
            { "DebrisPortionOfSelf", (parser, x) => x.DebrisPortionOfSelf = parser.ParseAssetReference() },
            { "UpgradeRequired", (parser, x) => x.UpgradeRequired = parser.ParseAssetReferenceArray() }
        });

    /// <summary>The list of objects to create on death; null means "create nothing".</summary>
    public LazyAssetReference<ObjectCreationList> CreationList { get; private set; }

    /// <summary>Whether the first created object inherits the dying object's damage state.</summary>
    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public bool TransferPreviousHealth { get; private set; }

    /// <summary>BFME-only; no GPL reference and no behavioral spec, so unconsumed.</summary>
    [AddedIn(SageGame.Bfme)]
    public string DebrisPortionOfSelf { get; private set; }

    /// <summary>BFME2-only; no GPL reference and no behavioral spec, so unconsumed.</summary>
    [AddedIn(SageGame.Bfme2)]
    public string[] UpgradeRequired { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new CreateObjectDie(gameObject, gameEngine.SimContext, this);
    }
}
