using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

[AddedIn(SageGame.Bfme)]
[ParseOnly("Round-4 backlog; census: Behavior")]
public class GateProxyBehaviorModuleData : BehaviorModuleData
{
    internal static GateProxyBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal static readonly IniParseTable<GateProxyBehaviorModuleData> FieldParseTable = new IniParseTable<GateProxyBehaviorModuleData>();

}
