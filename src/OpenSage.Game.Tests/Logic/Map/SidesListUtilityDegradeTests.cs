using System;
using System.Collections.Generic;
using System.Reflection;
using OpenSage.Data.Map;
using OpenSage.Logic.Map;
using Xunit;

namespace OpenSage.Tests.Logic.Map;

/// <summary>
/// L1-04: SidesListUtility.SetupSkirmishGameSides used to NullReferenceException when a
/// PlayerSetting.SideName had no matching Faction anywhere in the map's SidesList.Players
/// (originalMapPlayers.FirstOrDefault(...) returning null, then dereferenced unconditionally a few
/// lines later). These tests exercise the extracted degrade helper
/// (SidesListUtility.ResolveFactionPlayerOrDegrade, private static) directly via reflection -- it
/// only needs a list of the map's SidesList players plus a requested side name, so no
/// MapFile/IGame/ContentManager plumbing is required to prove the degrade path.
/// </summary>
public class SidesListUtilityDegradeTests
{
    private static readonly MethodInfo ResolveMethod =
        typeof(SidesListUtility).GetMethod(
            "ResolveFactionPlayerOrDegrade",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "SidesListUtility.ResolveFactionPlayerOrDegrade was not found -- has the degrade helper " +
            "been renamed or inlined back into SetupSkirmishGameSides? Update this test's reflection " +
            "target (or restore the extracted helper) to match.");

    private static Player Resolve(IReadOnlyList<Player> originalMapPlayers, string sideName, int playerIndex)
    {
        return (Player)ResolveMethod.Invoke(null, [originalMapPlayers, sideName, playerIndex])!;
    }

    private static Player MakeMapPlayer(string faction, string name) => new()
    {
        Faction = faction,
        Name = name,
        DisplayName = name,
    };

    [Fact]
    public void MatchedFaction_ReturnsTheMapsExistingPlayerUnchanged()
    {
        var men = MakeMapPlayer("FactionMen", "PlyrMen");
        var mordor = MakeMapPlayer("FactionMordor", "PlyrMordor");
        var originalMapPlayers = new List<Player> { men, mordor };

        // L5-P0's exact ratified E2 tokens (FactionMen vs FactionMordor).
        var resolved = Resolve(originalMapPlayers, "FactionMordor", playerIndex: 1);

        Assert.Same(mordor, resolved);
        Assert.Equal("FactionMordor", resolved.Faction);
        Assert.Equal("PlyrMordor", resolved.Name);
    }

    [Fact]
    public void UnmatchedFaction_DoesNotThrow()
    {
        // Map only defines FactionMen (e.g. a --faction2 request for a side the target map never
        // ships). Pre-fix, this configuration NRE'd inside SetupSkirmishGameSides.
        var originalMapPlayers = new List<Player> { MakeMapPlayer("FactionMen", "PlyrMen") };

        var exception = Record.Exception(() => Resolve(originalMapPlayers, "FactionMordor", playerIndex: 1));

        Assert.Null(exception);
    }

    [Fact]
    public void UnmatchedFaction_DegradesToAPlaceholderCarryingTheRequestedSideNameAndAnEmptyBuildList()
    {
        var originalMapPlayers = new List<Player> { MakeMapPlayer("FactionMen", "PlyrMen") };

        var resolved = Resolve(originalMapPlayers, "FactionMordor", playerIndex: 1);

        Assert.NotNull(resolved);
        Assert.Equal("FactionMordor", resolved.Faction);
        Assert.Equal("FactionMordor", resolved.Name);
        Assert.Equal("FactionMordor", resolved.DisplayName);
        Assert.Empty(resolved.BuildList);
    }

    [Fact]
    public void EmptyMapPlayerList_StillDegradesRatherThanThrowing()
    {
        var originalMapPlayers = new List<Player>();

        var exception = Record.Exception(() => Resolve(originalMapPlayers, "FactionMen", playerIndex: 0));

        Assert.Null(exception);
    }
}
