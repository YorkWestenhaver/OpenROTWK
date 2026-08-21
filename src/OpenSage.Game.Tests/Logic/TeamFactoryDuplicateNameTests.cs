// Regression tests for TeamFactory.AddTeamTemplate's handling of duplicate team template names.
//
// Before this fix, TeamFactory.Initialize crashed on the first map with two <Team> entries
// sharing a name (Dictionary<string, TeamTemplate>.Add throws ArgumentException on a duplicate
// key). AotR ships several such maps (duplicate keys observed: teamPlyrNeutral x6,
// teamPlayer_1, teamPlyrAngmar, Frodo), so every one of them failed to load.
//
// Retail (GPL Generals/Code/GameEngine/Source/Common/RTS/Team.cpp) tolerates this by design:
// TeamFactory::addTeamPrototypeToList (Team.cpp:255-266) looks up the new TeamPrototype's name
// key in its m_prototypes map and, if an entry is already registered under that key, returns
// without adding the new one (a DEBUG_ASSERTCRASH-gated diagnostic fires in debug builds only;
// retail never throws or crashes). The first-registered prototype for a given name is therefore
// the one every later name-based lookup resolves to ("first wins"). The fix here replicates that:
// a duplicate name logs a Warn and keeps the first registration instead of throwing.

using System.Linq;
using Xunit;
using MapTeam = OpenSage.Data.Map.Team;

namespace OpenSage.Tests.Logic;

public class TeamFactoryDuplicateNameTests : MockedGameTest
{
    private static MapTeam MakeMapTeam(string name, string owner, bool isSingleton = false) => new()
    {
        Name = name,
        Owner = owner,
        IsSingleton = isSingleton,
    };

    [Fact]
    public void Initialize_DuplicateTeamName_DoesNotThrow()
    {
        var mapTeams = new[]
        {
            MakeMapTeam("teamPlyrNeutral", "plyrNeutral"),
            MakeMapTeam("teamPlyrNeutral", "plyrNeutral"),
        };

        var exception = Record.Exception(() => Generals.TeamFactory.Initialize(mapTeams));

        Assert.Null(exception);
    }

    [Fact]
    public void Initialize_DuplicateTeamName_NameLookupResolvesToFirstRegistration()
    {
        var mapTeams = new[]
        {
            MakeMapTeam("teamPlyrNeutral", "plyrNeutral"),
            MakeMapTeam("teamPlyrNeutral", "plyrNeutral"),
        };

        Generals.TeamFactory.Initialize(mapTeams);

        var resolved = Generals.TeamFactory.FindTeamTemplateByName("teamPlyrNeutral");

        Assert.NotNull(resolved);
        // First-wins: the name lookup must resolve to the first-registered template (id 1),
        // never the second (id 2), matching GPL TeamFactory::addTeamPrototypeToList.
        Assert.Equal(1u, resolved.ID);
    }

    [Fact]
    public void Initialize_DuplicateTeamNameFollowedByUniqueName_IdCounterStaysMonotonicAndCollisionFree()
    {
        var mapTeams = new[]
        {
            MakeMapTeam("teamPlyrNeutral", "plyrNeutral"),
            MakeMapTeam("teamPlyrNeutral", "plyrNeutral"),
            MakeMapTeam("teamPlyrAngmar", "plyrNeutral"),
        };

        Generals.TeamFactory.Initialize(mapTeams);

        // Every constructed template -- including the shadowed duplicate that lost the name-map
        // slot -- must still get a unique, monotonically-assigned id: the id counter must not
        // stall or collide just because a duplicate name was skipped in the by-name map.
        var firstNeutral = Generals.TeamFactory.FindTeamTemplateById(1);
        var secondNeutral = Generals.TeamFactory.FindTeamTemplateById(2);
        var angmar = Generals.TeamFactory.FindTeamTemplateById(3);

        Assert.NotNull(firstNeutral);
        Assert.NotNull(secondNeutral);
        Assert.NotNull(angmar);

        Assert.Equal("teamPlyrNeutral", firstNeutral.Name);
        Assert.Equal("teamPlyrNeutral", secondNeutral.Name);
        Assert.Equal("teamPlyrAngmar", angmar.Name);

        Assert.NotEqual(firstNeutral.ID, secondNeutral.ID);
        Assert.Equal(3u, angmar.ID);

        // FindTeamTemplateByName must still resolve to the first registration, not the second.
        Assert.Equal(1u, Generals.TeamFactory.FindTeamTemplateByName("teamPlyrNeutral").ID);
    }

    [Fact]
    public void Initialize_NoDuplicateNames_AllTemplatesNameAddressable()
    {
        var mapTeams = new[]
        {
            MakeMapTeam("teamPlayer_1", "plyrNeutral"),
            MakeMapTeam("teamPlyrAngmar", "plyrNeutral"),
            MakeMapTeam("Frodo", "plyrNeutral"),
        };

        Generals.TeamFactory.Initialize(mapTeams);

        Assert.All(
            mapTeams.Select(t => t.Name),
            name => Assert.NotNull(Generals.TeamFactory.FindTeamTemplateByName(name)));
    }
}
