// Regression test for a load-path NRE in Player.Persist's DefaultTeam block (Player.cs, in the
// Mode == Read branch): DefaultTeam was resolved via
// reader.Game.TeamFactory.FindTeamById(defaultTeamId) and then DefaultTeam.Template was
// dereferenced unconditionally. FindTeamById returns null when the id doesn't resolve to a
// known team (e.g. defaultTeamId == 0, or the team is simply absent from the save), which
// crashed every load with a NullReferenceException instead of completing.
//
// Retail (GPL Generals/GeneralsMD Player::xfer, Player.cpp) tolerates this: on load it assigns
// `m_defaultTeam = TheTeamFactory->findTeamByID(teamID)` with no null check afterwards, so a
// miss just leaves the default team null. The fix here matches that: only a *resolved* team
// with the wrong owner is treated as corrupt save data.

using System.IO;
using OpenSage.Logic;
using OpenSage.Mathematics;
using Xunit;

namespace OpenSage.Tests;

public class PlayerDefaultTeamLoadTests : MockedGameTest
{
    [Fact]
    public void Persist_Read_TeamFactoryMissesDefaultTeamId_DoesNotThrowAndLeavesDefaultTeamNull()
    {
        var writtenPlayer = new Player(1, null, new ColorRgb(255, 0, 0), Generals);
        Assert.Null(writtenPlayer.DefaultTeam);

        using var stream = new MemoryStream();
        using (var writer = new StateWriter(stream, Generals))
        {
            writtenPlayer.Persist(writer);
        }

        stream.Position = 0;

        var readPlayer = new Player(1, null, new ColorRgb(255, 0, 0), Generals);
        using var reader = new StateReader(stream, Generals);

        // Before the fix: Generals.TeamFactory.FindTeamById(0) returns null (no team with id 0
        // is registered) and the unconditional `DefaultTeam.Template` dereference threw an NRE
        // here, during what should have been a normal load.
        var exception = Record.Exception(() => readPlayer.Persist(reader));

        Assert.Null(exception);
        Assert.Null(readPlayer.DefaultTeam);
    }
}
