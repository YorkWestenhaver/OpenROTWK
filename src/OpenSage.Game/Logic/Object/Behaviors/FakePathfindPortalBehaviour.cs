using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

[AddedIn(SageGame.Bfme2)]
[ParseOnly("Round-4 backlog; census: Behavior")]
public sealed class FakePathfindPortalBehaviourModuleData : BehaviorModuleData
{
    internal static FakePathfindPortalBehaviourModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<FakePathfindPortalBehaviourModuleData> FieldParseTable = new IniParseTable<FakePathfindPortalBehaviourModuleData>
    {
        { "AllowEnemies", (parser, x) => x.AllowEnemies = parser.ParseBoolean() },
        { "AllowNonSkirmishAIUnits", (parser, x) => x.AllowNonSkirmishAIUnits = parser.ParseBoolean() },
    };

    public bool AllowEnemies { get; private set; }
    public bool AllowNonSkirmishAIUnits { get; private set; }
}
