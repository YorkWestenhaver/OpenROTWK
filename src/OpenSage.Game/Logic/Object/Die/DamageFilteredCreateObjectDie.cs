// DamageFilteredCreateObjectDie - the instant leg of a split port (research/modules-r13/
// specs/DamageFilteredCreateObjectDieModuleData.md). The creation action is CreateObjectDie's
// (Die/CreateObjectDie.cs:56-65) verbatim, gated by one comparison the killing blow's
// DamageInfoInput already carries. The DeathTypes/RequiredStatus/ExemptStatus applicability
// filter runs upstream in DieModule's OnDie dispatch and is not reimplemented here.
//
// Two fields are parsed-and-held, not modelled (spec §4): DamageTypeTriggersForDuration and
// PostFilterTriggeredDuration describe a pre-death "arm a window" mechanic that has no hook on
// a Die-kind module (no Update(), no damage-received callback) and that every one of the nine
// live object.ini placements sets identically to DamageTypeTriggersInstantly, so no shipped
// placement can ever distinguish the window from the instant trigger. Held, not invented.
//
// The module keeps NO mutable state: the gate is a pure comparison on the already-in-scope
// killing blow, so the Xfer walk is the base walk - a version stamp over nothing, same as the
// CreateObjectDie sibling.

using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class DamageFilteredCreateObjectDie : DieModule
{
    private readonly DamageFilteredCreateObjectDieModuleData _data;

    // ---- mutable sim state: NONE. ----
    // The gate is a comparison on the killing blow already handed to Die(); nothing is
    // remembered between deaths, so the walk is the base walk.

    public DamageFilteredCreateObjectDie(GameObject gameObject, ISimContext context, DamageFilteredCreateObjectDieModuleData data)
        : base(gameObject, context, data)
    {
        _data = data;
    }

    protected override void Die(in DamageInfoInput damageInput)
    {
        // DeathTypes / RequiredStatus / ExemptStatus already ran in DieModule's OnDie.
        if (damageInput.DamageType != _data.DamageTypeTriggersInstantly)
        {
            return;
        }

        // Same creation action as CreateObjectDie: an invalid source, or one that already
        // left the world, is simply no secondary object.
        var damageDealer = damageInput.SourceID.IsValid
            ? Context.GameLogic.GetObjectById(damageInput.SourceID)
            : null;

        Context.GameLogic.CreateFromObjectCreationList(
            _data.CreationList?.Value,
            GameObject,
            damageDealer);
    }

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
    }

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
[AddedIn(SageGame.Bfme2)]
[SimDataAudited]
public sealed class DamageFilteredCreateObjectDieModuleData : DieModuleData
{
    internal static DamageFilteredCreateObjectDieModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<DamageFilteredCreateObjectDieModuleData> FieldParseTable = DieModuleData.FieldParseTable
        .Concat(new IniParseTable<DamageFilteredCreateObjectDieModuleData>
        {
            { "DamageTypeTriggersInstantly", (parser, x) => x.DamageTypeTriggersInstantly = parser.ParseEnum<DamageType>() },
            { "DamageTypeTriggersForDuration", (parser, x) => x.DamageTypeTriggersForDuration = parser.ParseEnum<DamageType>() },
            { "PostFilterTriggeredDuration", (parser, x) => x.PostFilterTriggeredDuration = parser.ParseInteger() },
            { "CreationList", (parser, x) => x.CreationList = parser.ParseObjectCreationListReference() },
        });

    /// <summary>The killing blow's damage type that runs <see cref="CreationList"/>.</summary>
    public DamageType DamageTypeTriggersInstantly { get; private set; }

    // held: no pre-death damage-received hook exists on a Die-kind module, and every live
    // block sets this to the same value as DamageTypeTriggersInstantly, so no shipped
    // placement distinguishes the two. Parsed, not modelled.
    public DamageType DamageTypeTriggersForDuration { get; private set; }

    // held: the window's unit and anchor are unrecovered, and every live block sets 10000,
    // so no placement pins them. Parsed raw - deliberately NOT quantized, since a
    // frames-from-ms conversion would assert a unit no evidence supports.
    public int PostFilterTriggeredDuration { get; private set; }

    /// <summary>The list to create on a matching death; null means "create nothing".</summary>
    public LazyAssetReference<ObjectCreationList> CreationList { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new DamageFilteredCreateObjectDie(gameObject, gameEngine.SimContext, this);
    }
}
