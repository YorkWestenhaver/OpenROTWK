using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

[AddedIn(SageGame.Bfme)]
[ParseOnly("Round-4 backlog; census: Update")]
public sealed class WallUpgradeUpdateModuleData : UpdateModuleData
{
    internal static WallUpgradeUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<WallUpgradeUpdateModuleData> FieldParseTable = new IniParseTable<WallUpgradeUpdateModuleData>();
}
