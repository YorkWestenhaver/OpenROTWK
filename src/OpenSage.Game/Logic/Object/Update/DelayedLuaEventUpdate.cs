using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

[AddedIn(SageGame.Bfme)]
[ParseOnly("Round-4 backlog; census: Update")]
public sealed class DelayedLuaEventUpdateModuleData : UpdateModuleData
{
    internal static DelayedLuaEventUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<DelayedLuaEventUpdateModuleData> FieldParseTable = new IniParseTable<DelayedLuaEventUpdateModuleData>();

}
