using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

/// <summary>
/// Hardcoded to play the SabotageBuilding sound definition when triggered.
/// </summary>
[AddedIn(SageGame.CncGeneralsZeroHour)]
[ParseOnly("Round-4 backlog; census: Collide")]
public sealed class SabotageCommandCenterCrateCollideModuleData : CrateCollideModuleData
{
    internal static SabotageCommandCenterCrateCollideModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<SabotageCommandCenterCrateCollideModuleData> FieldParseTable = CrateCollideModuleData.FieldParseTable
        .Concat(new IniParseTable<SabotageCommandCenterCrateCollideModuleData>());
}
