using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

[AddedIn(SageGame.Bfme2)]
[ParseOnly("Round-4 backlog; census: Upgrade")]
public sealed class AllowBannerSpawnUpgradeModuleData : UpgradeModuleData
{
    internal static AllowBannerSpawnUpgradeModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<AllowBannerSpawnUpgradeModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<AllowBannerSpawnUpgradeModuleData>());
}
