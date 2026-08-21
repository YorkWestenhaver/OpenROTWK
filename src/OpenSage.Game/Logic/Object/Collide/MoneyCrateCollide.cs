using System.IO;
using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.FileFormats;

namespace OpenSage.Logic.Object;

// R13.5 (crate-gate): the shared CrateCollide::isValidToExecute gate now lives on the
// CrateCollide base and this module inherits it; it has no OnCollide dispatch of its own
// yet, so the gate takes effect the moment the leaf's executeCrateBehavior is wired.
public sealed class MoneyCrateCollide : CrateCollide
{
    public MoneyCrateCollide(GameObject gameObject, IGameEngine gameEngine, MoneyCrateCollideModuleData moduleData)
        : base(gameObject, gameEngine, moduleData)
    {
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        base.Load(reader);
    }
}

public sealed class MoneyCrateCollideModuleData : CrateCollideModuleData
{
    internal static MoneyCrateCollideModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<MoneyCrateCollideModuleData> FieldParseTable = CrateCollideModuleData.FieldParseTable
        .Concat(new IniParseTable<MoneyCrateCollideModuleData>
        {
            { "MoneyProvided", (parser, x) => x.MoneyProvided = parser.ParseInteger() },
            { "UpgradedBoost", (parser, x) => x.UpgradedBoost = BoostUpgrade.Parse(parser) }
        });

    public int MoneyProvided { get; private set; }

    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public BoostUpgrade UpgradedBoost { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new MoneyCrateCollide(gameObject, gameEngine, this);
    }
}

public readonly record struct BoostUpgrade(LazyAssetReference<UpgradeTemplate> UpgradeType, int Boost)
{
    internal static BoostUpgrade Parse(IniParser parser)
    {
        return new BoostUpgrade
        {
            UpgradeType = parser.ParseAttribute("UpgradeType", parser.ParseUpgradeReference),
            Boost = parser.ParseAttributeInteger("Boost"),
        };
    }
}
