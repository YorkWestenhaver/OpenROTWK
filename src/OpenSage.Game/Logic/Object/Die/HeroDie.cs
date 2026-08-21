using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

[AddedIn(SageGame.Bfme)]
[ParseOnly("DROPPED-R15; census §3.3: zero module-position uses in AotR (the 26 raw grep hits " +
    "are FX list names like FX_HeroDieToRespawn and audio event names, not INI Die= " +
    "declarations); retail ROTWK/BFME2 INI.big do use it but AotR shadows every one of those " +
    "files, so the token is unreachable under -mod aotr. Parse retained: revisit only if the " +
    "sufficiency corpus widens to vanilla ROTWK skirmish.")]
public sealed class HeroDieModuleData : DieModuleData
{
    internal static HeroDieModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<HeroDieModuleData> FieldParseTable = DieModuleData.FieldParseTable
        .Concat(new IniParseTable<HeroDieModuleData>
        {
            { "SpecialPowerTemplate", (parser, x) => x.SpecialPowerTemplate = parser.ParseAssetReference() }
        });

    public string SpecialPowerTemplate { get; private set; }
}
