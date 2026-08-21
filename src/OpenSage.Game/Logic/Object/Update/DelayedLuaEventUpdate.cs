using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

[AddedIn(SageGame.Bfme)]
[ParseOnly("DROPPED-R15; census §3.3: one carrier, GenericDamageWarning in " +
    "object/system/system.ini; its only would-be reference is commented out " +
    "(';  GenericDamageWarningName = GenericDamageWarning' in gamedata.ini:11333). Never " +
    "instantiated. Parse retained.")]
public sealed class DelayedLuaEventUpdateModuleData : UpdateModuleData
{
    internal static DelayedLuaEventUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<DelayedLuaEventUpdateModuleData> FieldParseTable = new IniParseTable<DelayedLuaEventUpdateModuleData>();

}
