using System.Numerics;
using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.Diagnostics;

namespace OpenSage.Logic.Object;

internal sealed class ObjectCreationUpgrade : UpgradeModule
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    /// <summary>
    /// <see cref="DegradeLog"/> category for the unresolved-OCL guard in <see cref="OnUpgrade"/>.
    /// </summary>
    private const string UnresolvedUpgradeObjectCategory = "ObjectCreationUpgrade.UnresolvedUpgradeObject";

    private readonly ObjectCreationUpgradeModuleData _moduleData;

    internal ObjectCreationUpgrade(GameObject gameObject, IGameEngine gameEngine, ObjectCreationUpgradeModuleData moduleData)
        : base(gameObject, gameEngine, moduleData)
    {
        _moduleData = moduleData;
    }

    protected override void OnUpgrade()
    {
        // R15 L1-11: UpgradeObject is an ObjectCreationList reference that can fail to resolve
        // (the module block omits it, or names an OCL absent from the loaded INI corpus), in
        // which case .Value is null and the foreach below NREs. GrantUpgradeCreate.OnCreate
        // drives this during map load, so an unresolved OCL used to terminate the process
        // before the sim loop started (RivendellWell on "map sp good ettenmoors" in the R15
        // AotR sweep). Degrade: grant the upgrade, create nothing, report the gap once.
        var objectCreationList = _moduleData.UpgradeObject?.Value;
        if (objectCreationList == null)
        {
            var templateName = GameObject?.Definition?.Name;
            if (DegradeLog.ShouldReport(UnresolvedUpgradeObjectCategory, templateName))
            {
                Logger.Warn(
                    $"ObjectCreationUpgrade on object template '{DegradeLog.Normalize(templateName)}' has no " +
                    "resolvable UpgradeObject ObjectCreationList; the upgrade is applied but nothing is created.");
            }

            return;
        }

        foreach (var item in objectCreationList.Nuggets)
        {
            var createdObjects = item.Execute(GameObject, GameEngine);

            foreach (var createdObject in createdObjects)
            {
                createdObject.CreatedByObjectID = GameObject.Id;
                var slavedUpdateBehaviour = createdObject.FindBehavior<SlavedUpdateModule>();
                slavedUpdateBehaviour?.SetMaster(GameObject);
            }
        }
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
/// Allows an object to create/spawn a new object via upgrades.
/// </summary>
public sealed class ObjectCreationUpgradeModuleData : UpgradeModuleData
{
    internal static ObjectCreationUpgradeModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<ObjectCreationUpgradeModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<ObjectCreationUpgradeModuleData>
        {
            { "UpgradeObject", (parser, x) => x.UpgradeObject = parser.ParseObjectCreationListReference() },
            { "Delay", (parser, x) => x.Delay = parser.ParseFloat() },
            { "RemoveUpgrade", (parser, x) => x.RemoveUpgrade = parser.ParseAssetReference() },
            { "GrantUpgrade", (parser, x) => x.GrantUpgrade = parser.ParseAssetReference() },
            { "DestroyWhenSold", (parser, x) => x.DestroyWhenSold = parser.ParseBoolean() },
            { "DeathAnimAndDuration", (parser, x) => x.DeathAnimAndDuration = AnimAndDuration.Parse(parser) },
            { "Offset", (parser, x) => x.Offset = parser.ParseVector3() },
            { "ThingToSpawn", (parser, x) => x.ThingToSpawn = parser.ParseAssetReference() },
            { "FadeInTime", (parser, x) => x.FadeInTime = parser.ParseInteger() },
            { "UseBuildingProduction", (parser, x) => x.UseBuildingProduction = parser.ParseBoolean() }
        });

    public LazyAssetReference<ObjectCreationList> UpgradeObject { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public float Delay { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string RemoveUpgrade { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string GrantUpgrade { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public bool DestroyWhenSold { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public AnimAndDuration DeathAnimAndDuration { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public Vector3 Offset { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string ThingToSpawn { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public int FadeInTime { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public bool UseBuildingProduction { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new ObjectCreationUpgrade(gameObject, gameEngine, this);
    }
}
