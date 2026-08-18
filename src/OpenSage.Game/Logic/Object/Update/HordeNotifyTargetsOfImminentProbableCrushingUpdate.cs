using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

[AddedIn(SageGame.Bfme2)]
[ParseOnly("Round-4 backlog; census: Update")]
public sealed class HordeNotifyTargetsOfImminentProbableCrushingUpdateModuleData : UpdateModuleData
{
    internal static HordeNotifyTargetsOfImminentProbableCrushingUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<HordeNotifyTargetsOfImminentProbableCrushingUpdateModuleData> FieldParseTable =
        new IniParseTable<HordeNotifyTargetsOfImminentProbableCrushingUpdateModuleData>
    {
        { "ScanWidth", (parser, x) => x.ScanWidth = parser.ParseFloat() },
    };

    public float ScanWidth { get; private set; }
}
