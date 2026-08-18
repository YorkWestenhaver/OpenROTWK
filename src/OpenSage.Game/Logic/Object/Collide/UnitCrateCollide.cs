using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

[ParseOnly("Round-4 backlog; census: Collide")]
public sealed class UnitCrateCollideModuleData : CrateCollideModuleData
{
    internal static UnitCrateCollideModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<UnitCrateCollideModuleData> FieldParseTable = CrateCollideModuleData.FieldParseTable
        .Concat(new IniParseTable<UnitCrateCollideModuleData>
        {
            { "UnitCount", (parser, x) => x.UnitCount = parser.ParseInteger() },
            { "UnitName", (parser, x) => x.UnitName = parser.ParseAssetReference() }
        });

    public int UnitCount { get; private set; }
    public string UnitName { get; private set; }
}
