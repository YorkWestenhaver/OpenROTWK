using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

[AddedIn(SageGame.Bfme)]
[ParseOnly("Round-4 backlog; census: Update")]
public sealed class AttributeModifierPoolUpdateModuleData : UpdateModuleData
{
    internal static AttributeModifierPoolUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<AttributeModifierPoolUpdateModuleData> FieldParseTable = new IniParseTable<AttributeModifierPoolUpdateModuleData>();
}
