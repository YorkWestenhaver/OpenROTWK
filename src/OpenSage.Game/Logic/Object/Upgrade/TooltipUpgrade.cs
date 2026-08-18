using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

[AddedIn(SageGame.Bfme)]
[ParseOnly("Round-4 backlog; census: Upgrade")]
public sealed class ToolTipUpgradeModuleData : UpgradeModuleData
{
    internal static ToolTipUpgradeModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<ToolTipUpgradeModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<ToolTipUpgradeModuleData>
        {
            { "DisplayName", (parser, x) => x.DisplayName = parser.ParseLocalizedStringKey() }
        });

    public string DisplayName { get; private set; }
}
