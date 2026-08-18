using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

[AddedIn(SageGame.Bfme2Rotwk)]
[ParseOnly("Round-4 backlog; census: Damage")]
public sealed class HordeTransportContainDamageModuleData : DamageModuleData
{
    internal static HordeTransportContainDamageModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<HordeTransportContainDamageModuleData> FieldParseTable = new IniParseTable<HordeTransportContainDamageModuleData>();
}
