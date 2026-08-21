// HordeMemberCollide - R11 Track B port. The retail module parses an EMPTY block and marks
// member-side horde handling (spec-hordes.md §2); the runtime role is exactly the landed
// SimHordeMember (S6): hold the back-reference to the owning horde and forward member damage
// to the horde's flank test. This entry retires the [ParseOnly] marker by routing the REAL
// INI name onto that runtime, so authored member templates (e.g. AotR MordorFighter's
// "Behavior = HordeMemberCollide") produce the live module; the interim "SimHordeMember"
// vocabulary stays registered for the existing harness content.

using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class HordeMemberCollideModuleData : BehaviorModuleData
{
    internal static HordeMemberCollideModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal static readonly IniParseTable<HordeMemberCollideModuleData> FieldParseTable = new IniParseTable<HordeMemberCollideModuleData>();

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new Horde.SimHordeMember(gameObject, gameEngine.SimContext, this);
    }
}
