using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.Mathematics;

namespace OpenSage.Logic.Object;

// R13.5 (crate-gate): GPL's CrateCollide::isValidToExecute lives HERE now, once, instead of
// being inlined into each leaf. Reference: generals-gpl / generals-community
// GeneralsMD/Code/GameEngine/Source/GameLogic/Object/Collide/CrateCollide/CrateCollide.cpp.
// Six leaves carried near-identical private copies of the gate (each with its own drift) and
// seven had no gate at all; every leaf now calls IsValidToExecute (this base implementation
// plus, where the leaf overrides, its own extension) exactly the way GPL's leaves open with
// `if (!CrateCollide::isValidToExecute(other)) return false;`.
public abstract class CrateCollide : CollideModule
{
    private readonly CrateCollideModuleData _crateModuleData;

    protected CrateCollide(GameObject gameObject, IGameEngine gameEngine, CrateCollideModuleData moduleData)
        : base(gameObject, gameEngine)
    {
        _crateModuleData = moduleData;
    }

    /// <summary>
    /// The frozen contract ctor (api-freeze-v1 §3 item 1), grown for the UnitCrateCollide
    /// port (R12): the first CrateCollide subclass whose OnCollide logic is Fix64/ISimContext
    /// throughout and needs the module-facing sim seam. Legacy CrateCollide siblings
    /// (MoneyCrateCollide, SalvageCrateCollide, etc.) keep using the IGameEngine ctor above
    /// unchanged.
    /// </summary>
    protected CrateCollide(GameObject gameObject, ISimContext context, CrateCollideModuleData moduleData)
        : base(gameObject, context)
    {
        _crateModuleData = moduleData;
    }

    /// <summary>The inherited crate fields the shared gate reads (GPL getCrateCollideModuleData()).</summary>
    protected CrateCollideModuleData CrateModuleData => _crateModuleData;

    /// <summary>
    /// GPL <c>CrateCollide::isValidToExecute</c> - the shared pickup gate every crate collide
    /// runs before its own leaf checks. Overriding leaves must call
    /// <c>base.IsValidToExecute(other)</c> first, mirroring the GPL leaves' own opening line.
    /// </summary>
    public virtual bool IsValidToExecute(GameObject other)
    {
        // "The ground never picks up a crate."
        if (other is null)
        {
            return false;
        }

        // "Nothing Neutral can pick up any type of crate."
        if (other.Owner == NeutralPlayer)
        {
            return false;
        }

        // "Building exception flag for Drop Zone."
        var validBuildingAttempt = _crateModuleData.BuildingPickup && other.IsKindOf(ObjectKinds.Structure);

        // "Must be a 'Unit' type thing. Real Game Object, not just Object."
        if (other.AIUpdate is null && !validBuildingAttempt)
        {
            return false;
        }

        // "must match our kindof flags (if any)" - GPL isKindOfMulti(m_kindof, m_kindofnot):
        // EVERY RequiredKindOf bit present, NO ForbiddenKindOf bit present. RequiredKindOf was
        // parsed as a single ObjectKinds until R13.5, so a multi-kind authored line silently
        // kept only the last token and nothing enforced it at all.
        if (!MatchesKindOf(other))
        {
            return false;
        }

        if (other.IsEffectivelyDead)
        {
            return false;
        }

        // "crates cannot be claimed while in the air, except by buildings"
        if (GameObject.IsAboveTerrain && !validBuildingAttempt)
        {
            return false;
        }

        if (_crateModuleData.ForbidOwnerPlayer && GameObject.Owner == other.Owner)
        {
            // "Design has decreed this to not be picked up by the dead guy's team."
            return false;
        }

        if (_crateModuleData.HumanOnly && other.Owner is { IsHuman: false })
        {
            // "Human only mission crate."
            return false;
        }

        // "Science required to pick this up."
        if (_crateModuleData.PickupScience?.Value is { } pickupScience
            && other.Owner is { } collectorOwner
            && !collectorOwner.HasScience(pickupScience))
        {
            return false;
        }

        if (other.IsKindOf(ObjectKinds.Parachute))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// GPL <c>Object::isKindOfMulti(mustBeSet, mustBeClear)</c> for the crate's own two masks.
    /// An unauthored (null) mask constrains nothing, matching GPL's all-clear default.
    /// </summary>
    private bool MatchesKindOf(GameObject other)
    {
        var kindOf = other.Definition.KindOf;

        if (_crateModuleData.RequiredKindOf is { AnyBitSet: true } required
            && (kindOf is null || kindOf.CountIntersectionBits(required) != required.NumBitsSet))
        {
            return false;
        }

        if (_crateModuleData.ForbiddenKindOf is { AnyBitSet: true } forbidden
            && kindOf is not null
            && kindOf.Intersects(forbidden))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// GPL <c>ThePlayerList-&gt;getNeutralPlayer()</c>. Read through the frozen seam for a
    /// ported leaf (UnitCrateCollide) and through the legacy bridge for the rest, so one gate
    /// serves both module vintages.
    /// </summary>
    private Player NeutralPlayer => Context is not null
        ? Context.Players.NeutralPlayer
        : GameEngine.Game.PlayerManager.NeutralPlayer;

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

public abstract class CrateCollideModuleData : CollideModuleData
{
    internal static readonly IniParseTable<CrateCollideModuleData> FieldParseTable = new IniParseTable<CrateCollideModuleData>
    {
        { "RequiredKindOf", (parser, x) => x.RequiredKindOf = parser.ParseEnumBitArray<ObjectKinds>() },
        { "ForbiddenKindOf", (parser, x) => x.ForbiddenKindOf = parser.ParseEnumBitArray<ObjectKinds>() },
        { "ForbidOwnerPlayer", (parser, x) => x.ForbidOwnerPlayer = parser.ParseBoolean() },
        { "BuildingPickup", (parser, x) => x.BuildingPickup = parser.ParseBoolean() },
        { "HumanOnly", (parser, x) => x.HumanOnly = parser.ParseBoolean() },
        { "PickupScience", (parser, x) => x.PickupScience = parser.ParseScienceReference() },
        { "FXList", (parser, x) => x.FXList = parser.ParseAssetReference() },
        { "ExecuteAnimation", (parser, x) => x.ExecuteAnimation = parser.ParseAssetReference() },
        { "ExecuteAnimationTime", (parser, x) => x.ExecuteAnimationTime = parser.ParseFloat() },
        { "ExecuteAnimationZRise", (parser, x) => x.ExecuteAnimationZRise = parser.ParseFloat() },
        { "ExecuteAnimationFades", (parser, x) => x.ExecuteAnimationFades = parser.ParseBoolean() },
    };

    /// <summary>
    /// GPL <c>m_kindof</c>: a MASK, enforced through isKindOfMulti - every set bit must be
    /// present on the collector. No bits set means "no requirement".
    /// </summary>
    public BitArray<ObjectKinds> RequiredKindOf { get; private set; }

    /// <summary>GPL <c>m_kindofnot</c>: any set bit present on the collector rejects it.</summary>
    public BitArray<ObjectKinds> ForbiddenKindOf { get; private set; }

    public bool ForbidOwnerPlayer { get; private set; }
    public bool BuildingPickup { get; private set; }
    public bool HumanOnly { get; private set; }

    /// <summary>
    /// GPL <c>m_pickupScience</c>: only a unit whose controlling player holds this science may
    /// take the crate. Unset (null) means "no science required", GPL's SCIENCE_INVALID default.
    /// </summary>
    public LazyAssetReference<Science> PickupScience { get; private set; }

    public string FXList { get; private set; }
    public string ExecuteAnimation { get; private set; }
    public float ExecuteAnimationTime { get; private set; }
    public float ExecuteAnimationZRise { get; private set; }
    public bool ExecuteAnimationFades { get; private set; }
}
