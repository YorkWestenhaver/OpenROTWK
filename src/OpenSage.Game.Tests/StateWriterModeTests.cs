// Regression tests for a StateWriter ctor bug: the ctor passed StatePersistMode.Read to the
// base StatePersister ctor instead of .Write, so `writer.Mode` reported Read while writing.
// Any Persist() implementation that branches on `Mode == StatePersistMode.Read` (there are
// dozens across the codebase) would incorrectly run its read branch during a write pass.
//
// The concrete crash scenario this guards against is Player.Persist's DefaultTeam block
// (Player.cs, in the Mode == Read branch): when DefaultTeam is null, defaultTeamId is
// persisted as 0, and the buggy write pass would call
// reader.Game.TeamFactory.FindTeamById(0), get null back, and NullReferenceException on the
// unconditional `.Template` dereference - during what should have been a plain save.

using System.IO;
using OpenSage.Logic;
using OpenSage.Mathematics;
using Xunit;

namespace OpenSage.Tests;

public class StateWriterModeTests : MockedGameTest
{
    [Fact]
    public void Ctor_SetsModeToWrite()
    {
        using var stream = new MemoryStream();
        using var writer = new StateWriter(stream, Generals);

        Assert.Equal(StatePersistMode.Write, writer.Mode);
    }

    [Fact]
    public void Ctor_ReaderStillSetsModeToRead()
    {
        // Sibling sanity check - make sure the fix only touched StateWriter.
        using var stream = new MemoryStream();
        using var reader = new StateReader(stream, Generals);

        Assert.Equal(StatePersistMode.Read, reader.Mode);
    }

    [Fact]
    public void Persist_ThroughStateWriter_DoesNotExecuteReadBranchForDefaultTeam()
    {
        var player = new Player(1, null, new ColorRgb(255, 0, 0), Generals);
        Assert.Null(player.DefaultTeam);

        using var stream = new MemoryStream();
        using var writer = new StateWriter(stream, Generals);

        // Before the fix, writer.Mode reported Read, so Player.Persist's
        // `if (reader.Mode == StatePersistMode.Read)` DefaultTeam block would run during
        // this write, calling FindTeamById(0) -> null -> NRE on `.Template`.
        var exception = Record.Exception(() => player.Persist(writer));

        Assert.Null(exception);
        Assert.Null(player.DefaultTeam);
    }
}
