#nullable enable

using OpenSage.Data.Ini;
using OpenSage.SimCore;

namespace OpenSage.Logic.Object;

/// <summary>
/// An active body specifically for structures that are built,
/// and/or interactable with the player.
/// </summary>
/// <remarks>
/// Fresh code from GPL semantics (generals-gpl GeneralsMD
/// GameLogic/Object/Body/StructureBody.cpp: ctor / setConstructorObject / xfer /
/// loadPostProcess). A thin <see cref="ActiveBody"/> subclass that adds exactly one
/// field - the id of the object that built the structure - and folds it into the same
/// Objects CRC channel as the base body (GPL StructureBody::xfer chains
/// ActiveBody::xfer then xfers the constructor id). All health/damage/armor arithmetic
/// is inherited from ActiveBody's Fix64 core (S1) untouched.
/// </remarks>
[SimState]
public sealed class StructureBody : ActiveBody
{
    /// <summary>
    /// Object that built this structure.
    /// </summary>
    private ObjectId _constructorObjectID;

    internal StructureBody(GameObject gameObject, IGameEngine gameEngine, StructureBodyModuleData moduleData)
        : base(gameObject, gameEngine, moduleData)
    {
        // GPL StructureBody ctor: m_constructorObjectID = INVALID_ID. (default(ObjectId)
        // is already ObjectId.Invalid; assigned explicitly to mirror the reference.)
        _constructorObjectID = ObjectId.Invalid;
    }

    /// <summary>Id of the object that built this structure (GPL getConstructorObjectID).</summary>
    public ObjectId ConstructorObjectId => _constructorObjectID;

    // This method is in the original code, but isn't actually used anywhere.
    public void SetConstructorObject(GameObject? obj)
    {
        // GPL setConstructorObject only writes when obj is non-null; a null argument
        // leaves the existing id untouched (it does NOT clear to INVALID_ID).
        if (obj != null)
        {
            _constructorObjectID = obj.Id;
        }
    }

    // ---- the contract Xfer walk (S1/F7/F8): extends ActiveBody's Fix64 combat-state
    // walk with the one extra structure field, folded into the SAME Objects CRC channel.
    // Field/version order = GPL StructureBody::xfer (own version, then base, then the
    // constructor id), which is also declaration order (F9). HasSimXfer stays true
    // (inherited from ActiveBody). ----

    public override void Xfer(SimCore.Sync.IXfer xfer)
    {
        // StructureBody's own version byte (GPL currentVersion = 1), folded independently
        // ahead of the base walk - the original nests ActiveBody's version inside this one.
        xfer.XferVersion(1);

        // base class (ActiveBody: its own version + the Fix64 health ledger + crush/
        // indestructible bools).
        base.Xfer(xfer);

        // The one field StructureBody adds - into the same Objects channel (Exact).
        xfer.XferObjectId("ConstructorObjectId", ref _constructorObjectID);
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        reader.PersistObjectId(ref _constructorObjectID);
    }
}

/// <summary>
/// Used by objects with STRUCTURE and IMMOBILE KindOfs defined.
/// </summary>
/// <remarks>
/// GPL StructureBodyModuleData adds an empty field-parse block over ActiveBodyModuleData
/// ({ 0, 0, 0, 0 } terminator only) - it introduces no data of its own. The audit here
/// is therefore trivially complete for this class's own fields; the inherited float
/// healths (MaxHealth/InitialHealth/...) live on <see cref="ActiveBodyModuleData"/> and
/// await the Body-category ModuleData audit that S1 deferred (see StructureBody.md
/// finding F-SB-1).
/// </remarks>
[SimDataAudited]
public sealed class StructureBodyModuleData : ActiveBodyModuleData
{
    internal static new StructureBodyModuleData Parse(IniParser parser)
    {
        var result = parser.ParseBlock(FieldParseTable);
        result.ApplyHealthDefaults(parser);   // F-HB-1: the shadowing Parse must keep the base defaulting.
        return result;
    }

    private static new readonly IniParseTable<StructureBodyModuleData> FieldParseTable = ActiveBodyModuleData.FieldParseTable
        .Concat(new IniParseTable<StructureBodyModuleData>());

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new StructureBody(gameObject, gameEngine, this);
    }
}
