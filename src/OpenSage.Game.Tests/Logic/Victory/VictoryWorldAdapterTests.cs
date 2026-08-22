// VD-3 — VictoryWorldAdapter: the engine side of the victory seam.
//
// Three things are proved here, because three things could silently mis-grade the E2 endpoint:
//   1. the cached victory pool applies GPL cachePlayerPtrs' four exclusions in GPL's order
//      (neutral, template-less, civilian, observer) and resolves the local slot inside it;
//   2. the BFME2 sweep is the two GameData ObjectFilters over live objects — including the two
//      things ObjectFilter.Matches does NOT do that this lane needs: honouring ANY
//      (design-victory-defeat.md §2.3b, without which the unit filter matches nothing) and
//      honouring ExcludeThings by template name (§2.3c, without which inns and outposts count
//      as victory structures) — with the §6.1 any-live-object fallback when a filter is null;
//   3. alliances are MUTUAL only (§6.3), and elimination marks the player dead before it
//      destroys anything (§1.5).
//
// The filters are REAL: they come out of IniParseTestContext parsing an actual GameData block,
// so the tests break if ObjectFilter.Parse's shape changes under us rather than agreeing with a
// hand-built stand-in. Players and objects come from MockedGameTest's engine.

using System.Collections.Generic;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Logic.Victory;
using OpenSage.Mathematics;
using OpenSage.SimCore.Ticking;
using OpenSage.Tests.Data.Ini;
using Xunit;

namespace OpenSage.Tests.Logic.Victory;

public class VictoryWorldAdapterTests : MockedGameTest
{
    // The AotR shapes, reduced to what each clause proves: the structure filter opts structures
    // in and opts IGNORE_FOR_VICTORY and one named thing (an inn) out; the unit filter is the
    // ANY-with-exclusions shape that has no Include bits at all.
    private const string VictoryGameData =
        "GameData\n" +
        "  VictoryConditionStructureObjectFilter = NONE +STRUCTURE -IGNORE_FOR_VICTORY -VictoryTestInn\n" +
        "  VictoryConditionUnitObjectFilter = ANY -DOZER\n" +
        "End\n";

    private static (ObjectFilter Structures, ObjectFilter Units) ParseVictoryFilters()
    {
        var context = new IniParseTestContext();
        var parser = context.ParseFileText(VictoryGameData, @"Data\INI\GameData.ini");

        Assert.Empty(parser.ParseErrors);

        var gameData = context.AssetStore.GameData.Current;
        Assert.NotNull(gameData.VictoryConditionStructureObjectFilter);
        Assert.NotNull(gameData.VictoryConditionUnitObjectFilter);

        return (gameData.VictoryConditionStructureObjectFilter, gameData.VictoryConditionUnitObjectFilter);
    }

    // ---- fake world source ----

    private sealed class FakeSource : IVictoryWorldSource
    {
        public LogicFrame Frame = LogicFrame.Zero;
        public readonly List<Player> Players = new();
        public readonly List<GameObject> ObjectList = new();

        public Player Neutral;
        public Player Civilian;
        public Player Local;
        public ObjectFilter Structures;
        public ObjectFilter Units;

        public LogicFrame CurrentFrame => Frame;
        public IReadOnlyList<Player> AllPlayers => Players;
        public Player NeutralPlayer => Neutral;
        public Player CivilianPlayer => Civilian;
        public Player LocalPlayer => Local;
        public IEnumerable<GameObject> Objects => ObjectList;
        public ObjectFilter StructureFilter => Structures;
        public ObjectFilter UnitFilter => Units;
    }

    private uint _nextPlayerId;

    private Player NewPlayer(string name, string factionName = "FactionTest")
    {
        var template = factionName == null ? null : new PlayerTemplate { Name = factionName };
        return new Player(_nextPlayerId++, template, new ColorRgb(1, 2, 3), ZeroHour) { Name = name };
    }

    private static ObjectDefinition NewDefinition(string name, params ObjectKinds[] kinds)
    {
        var definition = new ObjectDefinition { Name = name };
        foreach (var kind in kinds)
        {
            definition.KindOf.Set(kind, true);
        }

        return definition;
    }

    private GameObject NewObject(FakeSource source, Player owner, string definitionName, params ObjectKinds[] kinds)
    {
        var gameObject = new GameObject(NewDefinition(definitionName, kinds), ZeroHour.GameEngine, owner);
        source.ObjectList.Add(gameObject);
        return gameObject;
    }

    /// <summary>
    /// A two-player pool behind a neutral, a civilian, a template-less player and an observer —
    /// the shape every sweep test wants, and the shape the pool test picks apart.
    /// </summary>
    private (FakeSource Source, VictoryWorldAdapter Adapter, Player A, Player B) NewTwoPlayerWorld(
        bool withFilters = true,
        bool localIsObserver = false)
    {
        var source = new FakeSource();

        if (withFilters)
        {
            (source.Structures, source.Units) = ParseVictoryFilters();
        }

        var neutral = NewPlayer("plyrNeutral", null);
        var civilian = NewPlayer("plyrCivilian");
        var templateless = NewPlayer("plyrNoTemplate", null);
        var observer = NewPlayer("plyrObserver", "FactionObserver");
        var a = NewPlayer("PlayerA");
        var b = NewPlayer("PlayerB");

        source.Neutral = neutral;
        source.Civilian = civilian;
        source.Local = localIsObserver ? observer : a;
        source.Players.AddRange(new[] { neutral, civilian, templateless, observer, a, b });

        var adapter = new VictoryWorldAdapter(source);
        adapter.CachePlayers();

        return (source, adapter, a, b);
    }

    // ---- cachePlayerPtrs (§1.6) ----

    [Fact]
    public void CachePlayers_AppliesGplsFourExclusions()
    {
        var (_, adapter, a, b) = NewTwoPlayerWorld();

        Assert.True(adapter.IsCached);
        Assert.Equal(2, adapter.PlayerCount);
        Assert.Same(a, adapter.PlayerAt(0));
        Assert.Same(b, adapter.PlayerAt(1));
        Assert.Equal(0, adapter.SlotOf(a));
        Assert.Equal(1, adapter.SlotOf(b));
    }

    [Fact]
    public void CachePlayers_ResolvesTheLocalSlotInsideThePool()
    {
        var (_, adapter, a, _) = NewTwoPlayerWorld();

        // Slot 0, not index 4 in the raw player list: the pool is its own index space.
        Assert.Equal(0, adapter.LocalSlot);
        Assert.Same(a, adapter.PlayerAt(adapter.LocalSlot));
    }

    /// <summary>
    /// GPL's cachePlayerPtrs tail: nothing cached as local means observer, and
    /// <c>VictoryConditionsCore.Reset(-1)</c> is what turns that into the observer latches.
    /// </summary>
    [Fact]
    public void CachePlayers_LocalPlayerExcludedFromPool_LocalSlotIsMinusOne()
    {
        var (_, adapter, _, _) = NewTwoPlayerWorld(localIsObserver: true);

        Assert.Equal(-1, adapter.LocalSlot);
        Assert.Equal(2, adapter.PlayerCount);
    }

    [Fact]
    public void PlayerAtAndSlotOf_AreOutOfPoolSafe()
    {
        var (_, adapter, _, _) = NewTwoPlayerWorld();

        Assert.Null(adapter.PlayerAt(-1));
        Assert.Null(adapter.PlayerAt(2));
        Assert.Equal(-1, adapter.SlotOf(null));
        Assert.Equal(-1, adapter.SlotOf(NewPlayer("NotInThePool")));
    }

    [Fact]
    public void CurrentFrame_ComesFromTheWorld()
    {
        var (source, adapter, _, _) = NewTwoPlayerWorld();

        source.Frame = new LogicFrame(4242);

        Assert.Equal(new LogicFrame(4242), adapter.CurrentFrame);
    }

    // ---- the sweep: structures (§2) ----

    [Fact]
    public void HasAnyVictoryStructures_CountsAStructureThatMatchesTheFilter()
    {
        var (source, adapter, a, _) = NewTwoPlayerWorld();

        NewObject(source, a, "VictoryTestKeep", ObjectKinds.Structure);

        Assert.True(adapter.HasAnyVictoryStructures(0));
        Assert.False(adapter.HasAnyVictoryStructures(1));
    }

    [Fact]
    public void HasAnyVictoryStructures_IgnoresAnExcludedKindOf()
    {
        var (source, adapter, a, _) = NewTwoPlayerWorld();

        NewObject(source, a, "VictoryTestWall", ObjectKinds.Structure, ObjectKinds.IgnoreForVictory);

        Assert.False(adapter.HasAnyVictoryStructures(0));
    }

    /// <summary>
    /// §2.3(c): the filter's non-KindOf tokens are template names, and <c>ObjectFilter.Matches</c>
    /// never reads them. Without the adapter's own check an inn would keep a razed player alive.
    /// Name comparison is case-insensitive, as INI identifiers are everywhere else.
    /// </summary>
    [Fact]
    public void HasAnyVictoryStructures_HonoursExcludeThingsByTemplateName()
    {
        var (source, adapter, a, b) = NewTwoPlayerWorld();

        NewObject(source, a, "VictoryTestInn", ObjectKinds.Structure);
        NewObject(source, b, "victorytestinn", ObjectKinds.Structure);

        Assert.False(adapter.HasAnyVictoryStructures(0));
        Assert.False(adapter.HasAnyVictoryStructures(1));
    }

    // ---- the sweep: units (§2.3b) ----

    /// <summary>
    /// The unit filter is <c>ANY -DOZER …</c>: it sets no Include bits at all, so
    /// <c>ObjectFilter.Matches</c> alone returns false for every object and hasAnyUnits would be
    /// permanently false. The adapter treats ANY the way Matches already treats ALL.
    /// </summary>
    [Fact]
    public void HasAnyVictoryUnits_AnyRuleFilterCountsOrdinaryUnits()
    {
        var (source, adapter, a, _) = NewTwoPlayerWorld();

        var soldier = NewObject(source, a, "VictoryTestSoldier", ObjectKinds.Infantry);

        Assert.False(source.Units.Matches(soldier));   // the defect this branch exists for
        Assert.True(adapter.HasAnyVictoryUnits(0));
    }

    [Fact]
    public void HasAnyVictoryUnits_StillHonoursTheFiltersExclusions()
    {
        var (source, adapter, a, _) = NewTwoPlayerWorld();

        NewObject(source, a, "VictoryTestWorker", ObjectKinds.Infantry, ObjectKinds.Dozer);

        Assert.False(adapter.HasAnyVictoryUnits(0));
    }

    // ---- the sweep: liveness and ownership ----

    [Fact]
    public void Sweep_IgnoresEffectivelyDeadObjects()
    {
        var (source, adapter, a, _) = NewTwoPlayerWorld();

        var keep = NewObject(source, a, "VictoryTestKeep", ObjectKinds.Structure);
        Assert.True(adapter.HasAnyVictoryStructures(0));

        keep.IsEffectivelyDead = true;

        Assert.False(adapter.HasAnyVictoryStructures(0));
    }

    [Fact]
    public void Sweep_IgnoresObjectsOwnedByOtherPlayers()
    {
        var (source, adapter, _, b) = NewTwoPlayerWorld();

        NewObject(source, b, "VictoryTestKeep", ObjectKinds.Structure);

        Assert.False(adapter.HasAnyVictoryStructures(0));
        Assert.True(adapter.HasAnyVictoryStructures(1));
    }

    [Fact]
    public void Sweep_OutOfPoolSlotHasNothing()
    {
        var (source, adapter, a, _) = NewTwoPlayerWorld();

        NewObject(source, a, "VictoryTestKeep", ObjectKinds.Structure);

        Assert.False(adapter.HasAnyVictoryStructures(-1));
        Assert.False(adapter.HasAnyVictoryUnits(7));
        Assert.False(adapter.HasAnyVictoryObjects(7));
    }

    // ---- the both-flags branch (§6.1) ----

    [Fact]
    public void HasAnyVictoryObjects_IsTheUnionOfTheTwoFilters()
    {
        var (source, adapter, a, b) = NewTwoPlayerWorld();

        NewObject(source, a, "VictoryTestKeep", ObjectKinds.Structure);
        NewObject(source, b, "VictoryTestSoldier", ObjectKinds.Infantry);

        Assert.True(adapter.HasAnyVictoryObjects(0));
        Assert.True(adapter.HasAnyVictoryObjects(1));

        // ...and a player holding only an opted-out structure has neither.
        var (source2, adapter2, c, _) = NewTwoPlayerWorld();
        NewObject(source2, c, "VictoryTestWall", ObjectKinds.Structure, ObjectKinds.IgnoreForVictory);
        Assert.False(adapter2.HasAnyVictoryObjects(0));
    }

    // ---- the null-filter fallback (§6.1, the state on disk today per §2.3a) ----

    [Fact]
    public void NullFilters_FallBackToAnyLiveObject()
    {
        var (source, adapter, a, _) = NewTwoPlayerWorld(withFilters: false);

        // Nothing about this object matches either (absent) filter; the fallback still counts it.
        NewObject(source, a, "VictoryTestSomething", ObjectKinds.Infantry);

        Assert.True(adapter.HasAnyVictoryObjects(0));
        Assert.True(adapter.HasAnyVictoryStructures(0));
        Assert.True(adapter.HasAnyVictoryUnits(0));
        Assert.False(adapter.HasAnyVictoryObjects(1));
    }

    [Theory]
    [InlineData(ObjectKinds.Projectile)]
    [InlineData(ObjectKinds.Inert)]
    [InlineData(ObjectKinds.Mine)]
    public void NullFilters_FallbackSkipsGplsThreeKindOfs(ObjectKinds kind)
    {
        var (source, adapter, a, _) = NewTwoPlayerWorld(withFilters: false);

        NewObject(source, a, "VictoryTestDebris", kind);

        Assert.False(adapter.HasAnyVictoryObjects(0));
    }

    [Fact]
    public void NullFilters_FallbackStillHonoursLiveness()
    {
        var (source, adapter, a, _) = NewTwoPlayerWorld(withFilters: false);

        var thing = NewObject(source, a, "VictoryTestSomething", ObjectKinds.Infantry);
        thing.IsEffectivelyDead = true;

        Assert.False(adapter.HasAnyVictoryObjects(0));
    }

    /// <summary>
    /// One filter present, the other missing: the both-flags branch takes the fallback rather
    /// than answering from half the data. Answering from half would under-count and eliminate a
    /// player who still has an army.
    /// </summary>
    [Fact]
    public void HalfLoadedFilters_BothFlagsBranchTakesTheFallback()
    {
        var (source, adapter, a, _) = NewTwoPlayerWorld();
        source.Units = null;

        NewObject(source, a, "VictoryTestWall", ObjectKinds.Structure, ObjectKinds.IgnoreForVictory);

        Assert.False(adapter.HasAnyVictoryStructures(0));   // the present filter is still honoured
        Assert.True(adapter.HasAnyVictoryUnits(0));         // the missing one falls back
        Assert.True(adapter.HasAnyVictoryObjects(0));
    }

    // ---- alliances (§6.3) ----

    [Fact]
    public void AreAllies_MutualMembershipOnly()
    {
        var (_, adapter, a, b) = NewTwoPlayerWorld();

        Assert.False(adapter.AreAllies(0, 1));

        a.AddAlly(b);
        Assert.False(adapter.AreAllies(0, 1));   // one-sided is not an alliance — GPL's rule

        b.AddAlly(a);
        Assert.True(adapter.AreAllies(0, 1));
        Assert.True(adapter.AreAllies(1, 0));
    }

    [Fact]
    public void AreAllies_ASlotIsNeverItsOwnAlly()
    {
        var (_, adapter, a, _) = NewTwoPlayerWorld();

        a.AddAlly(a);

        Assert.False(adapter.AreAllies(0, 0));
    }

    [Fact]
    public void AreAllies_OutOfPoolSlotsAreNotAllies()
    {
        var (_, adapter, _, _) = NewTwoPlayerWorld();

        Assert.False(adapter.AreAllies(0, 5));
        Assert.False(adapter.AreAllies(-1, 0));
    }

    /// <summary>
    /// Tier 2 is dormant until PlayerManager's "Setup player relationships" TODO is done —
    /// SetRelationship is one-directional and there is no team to hang it on here, so a pair
    /// with no mutual Allies membership reads as not allied. This test pins the degradation so
    /// nobody mistakes it for a bug when a 2v2 lobby lands.
    /// </summary>
    [Fact]
    public void AreAllies_RelationshipOverrideTableIsDormantWithoutTeams()
    {
        var (_, adapter, a, b) = NewTwoPlayerWorld();

        a.SetRelationship(b, RelationshipType.Allies);
        b.SetRelationship(a, RelationshipType.Allies);

        Assert.Null(a.DefaultTeam);
        Assert.False(adapter.AreAllies(0, 1));
    }

    // ---- elimination (§1.5) ----

    [Fact]
    public void OnPlayerEliminated_LatchesIsDefeatedOnThatPlayerOnly()
    {
        var (_, adapter, a, b) = NewTwoPlayerWorld();

        Assert.False(a.IsDefeated);

        adapter.OnPlayerEliminated(0);

        Assert.True(a.IsDefeated);
        Assert.False(b.IsDefeated);
    }

    [Fact]
    public void OnPlayerEliminated_OutOfPoolSlotIsANoOp()
    {
        var (_, adapter, a, b) = NewTwoPlayerWorld();

        adapter.OnPlayerEliminated(9);
        adapter.OnPlayerEliminated(-1);

        Assert.False(a.IsDefeated);
        Assert.False(b.IsDefeated);
    }

    /// <summary>
    /// The real thing, on a real GameLogic: killing a player marks them dead and destroys every
    /// object they still own. The ordering (dead first, destruction second) is GPL's and is why
    /// nothing spawned by a die handler can be handed to a player who has already lost.
    /// </summary>
    [Fact]
    public void KillPlayer_MarksDefeatedAndDestroysEveryOwnedObject()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xB0B0u);
        game.LoadIniText(
            "Object VictoryTestGrunt\n" +
            "  KindOf = INFANTRY\n" +
            "  Body = ActiveBody ModuleTag_Body\n" +
            "    MaxHealth = 100\n" +
            "  End\n" +
            "End\n");

        var owner = game.CivilianPlayer;
        var first = game.SpawnObject("VictoryTestGrunt", owner, new Vector3(0, 0, 0));
        var second = game.SpawnObject("VictoryTestGrunt", owner, new Vector3(20, 0, 0));

        Assert.True(first.Id.Index < second.Id.Index);

        owner.KillPlayer();

        Assert.True(owner.IsDefeated);
        Assert.True(first.IsDestroyed);
        Assert.True(second.IsDestroyed);
    }

    [Fact]
    public void KillPlayer_WithNothingOwnedStillLatches()
    {
        var (_, _, a, _) = NewTwoPlayerWorld();

        a.KillPlayer();

        Assert.True(a.IsDefeated);
    }

    // ---- Player.IsPlayerObserver (§1.6) ----

    [Fact]
    public void IsPlayerObserver_IsDerivedFromTheObserverFaction()
    {
        Assert.True(NewPlayer("plyrObserver", "FactionObserver").IsPlayerObserver);
        Assert.False(NewPlayer("PlayerA").IsPlayerObserver);
        Assert.False(NewPlayer("plyrNoTemplate", null).IsPlayerObserver);
    }

    [Fact]
    public void IsPlayerObserver_ExplicitValueWinsOverTheDerivedOne()
    {
        var player = NewPlayer("PlayerA");
        player.IsPlayerObserver = true;

        Assert.True(player.IsPlayerObserver);
    }
}
