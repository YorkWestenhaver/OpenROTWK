using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

[AddedIn(SageGame.CncGeneralsZeroHour)]
[ParseOnly("Round-4 backlog; census: Upgrade")]
public sealed class ReplaceObjectUpgradeModuleData : UpgradeModuleData
{
    internal static ReplaceObjectUpgradeModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<ReplaceObjectUpgradeModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<ReplaceObjectUpgradeModuleData>
        {
            { "ReplaceObject", (parser, x) => x.ReplaceObject = parser.ParseAssetReference() },
        });

    public string ReplaceObject { get; private set; }
}
