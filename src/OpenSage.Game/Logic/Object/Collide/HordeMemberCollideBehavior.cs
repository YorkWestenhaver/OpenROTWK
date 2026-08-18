using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

[AddedIn(SageGame.Bfme)]
[ParseOnly("Round-4 backlog; census: Behavior")]
public class HordeMemberCollideModuleData : BehaviorModuleData
{
    internal static HordeMemberCollideModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal static readonly IniParseTable<HordeMemberCollideModuleData> FieldParseTable = new IniParseTable<HordeMemberCollideModuleData>();
}
