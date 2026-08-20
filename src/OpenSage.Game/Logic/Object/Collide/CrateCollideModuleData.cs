using OpenSage.Data.Ini;
using OpenSage.Mathematics;

namespace OpenSage.Logic.Object;

public abstract class CrateCollide : CollideModule
{
    protected CrateCollide(GameObject gameObject, IGameEngine gameEngine) : base(gameObject, gameEngine)
    {
    }

    /// <summary>
    /// The frozen contract ctor (api-freeze-v1 §3 item 1), grown for the UnitCrateCollide
    /// port (R12): the first CrateCollide subclass whose OnCollide logic is Fix64/ISimContext
    /// throughout and needs the module-facing sim seam. Legacy CrateCollide siblings
    /// (MoneyCrateCollide, SalvageCrateCollide, etc.) keep using the IGameEngine ctor above
    /// unchanged.
    /// </summary>
    protected CrateCollide(GameObject gameObject, ISimContext context) : base(gameObject, context)
    {
    }

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
        { "RequiredKindOf", (parser, x) => x.RequiredKindOf = parser.ParseEnum<ObjectKinds>() },
        { "ForbiddenKindOf", (parser, x) => x.ForbiddenKindOf = parser.ParseEnumBitArray<ObjectKinds>() },
        { "ForbidOwnerPlayer", (parser, x) => x.ForbidOwnerPlayer = parser.ParseBoolean() },
        { "BuildingPickup", (parser, x) => x.BuildingPickup = parser.ParseBoolean() },
        { "HumanOnly", (parser, x) => x.HumanOnly = parser.ParseBoolean() },
        { "FXList", (parser, x) => x.FXList = parser.ParseAssetReference() },
        { "ExecuteAnimation", (parser, x) => x.ExecuteAnimation = parser.ParseAssetReference() },
        { "ExecuteAnimationTime", (parser, x) => x.ExecuteAnimationTime = parser.ParseFloat() },
        { "ExecuteAnimationZRise", (parser, x) => x.ExecuteAnimationZRise = parser.ParseFloat() },
        { "ExecuteAnimationFades", (parser, x) => x.ExecuteAnimationFades = parser.ParseBoolean() },
    };

    public ObjectKinds RequiredKindOf { get; private set; }
    public BitArray<ObjectKinds> ForbiddenKindOf { get; private set; }
    public bool ForbidOwnerPlayer { get; private set; }
    public bool BuildingPickup { get; private set; }
    public bool HumanOnly { get; private set; }
    public string FXList { get; private set; }
    public string ExecuteAnimation { get; private set; }
    public float ExecuteAnimationTime { get; private set; }
    public float ExecuteAnimationZRise { get; private set; }
    public bool ExecuteAnimationFades { get; private set; }
}
