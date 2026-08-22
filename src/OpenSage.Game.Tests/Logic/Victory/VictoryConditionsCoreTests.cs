// VD-2 — VictoryConditionsCore against a fake world. No GameObject, no Player, no engine:
// the whole point of the [SimState] split (design-victory-defeat.md §4) is that the GPL
// semantics are testable without a game.
//
// Covers, in order: the multiplayer guard, 2p elimination end-to-end, latch-once (endFrame
// and the winners snapshot are written exactly once), OnPlayerEliminated firing once per
// slot, 2v2 alliances (mutual only; one-sided is not an alliance), GPL's first-live-player
// comparison declaring a winner early on a non-transitive graph, observers, the three flag
// variants, the mutual-wipe Draw, alliance-defeat vs personal-defeat readers, and the
// PersistVersion(1) round-trip (CRC equality + continuation).

using System.Collections.Generic;
using System.IO;
using OpenSage.Logic.Victory;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Victory;

public class VictoryConditionsCoreTests
{
    // ---- fake world ----

    private sealed class FakeVictoryWorld : IVictoryWorld
    {
        private readonly List<bool> _structures = new();
        private readonly List<bool> _units = new();
        private readonly List<(int From, int To)> _allyEdges = new();

        public LogicFrame Frame;

        /// <summary>Every OnPlayerEliminated call, in order. Duplicates are the bug we test for.</summary>
        public readonly List<int> Eliminated = new();

        public LogicFrame CurrentFrame => Frame;

        public int PlayerCount => _structures.Count;

        public int AddPlayer(bool structures = true, bool units = true)
        {
            _structures.Add(structures);
            _units.Add(units);
            return _structures.Count - 1;
        }

        public void SetStructures(int slot, bool value) => _structures[slot] = value;

        public void SetUnits(int slot, bool value) => _units[slot] = value;

        /// <summary>Wipe a slot: no structures, no units.</summary>
        public void Wipe(int slot)
        {
            _structures[slot] = false;
            _units[slot] = false;
        }

        /// <summary>A one-directional alliance declaration; GPL requires both directions.</summary>
        public void AllyOneWay(int from, int to) => _allyEdges.Add((from, to));

        public void Ally(int a, int b)
        {
            AllyOneWay(a, b);
            AllyOneWay(b, a);
        }

        public bool HasAnyVictoryStructures(int slot) => _structures[slot];

        public bool HasAnyVictoryUnits(int slot) => _units[slot];

        public bool HasAnyVictoryObjects(int slot) => _structures[slot] || _units[slot];

        public bool AreAllies(int slotA, int slotB) =>
            slotA != slotB && HasEdge(slotA, slotB) && HasEdge(slotB, slotA);

        public void OnPlayerEliminated(int slot) => Eliminated.Add(slot);

        private bool HasEdge(int from, int to)
        {
            foreach (var edge in _allyEdges)
            {
                if (edge.From == from && edge.To == to)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private static void Advance(VictoryConditionsCore core, FakeVictoryWorld world, uint frames = 1)
    {
        for (var i = 0u; i < frames; i++)
        {
            core.Update();
            world.Frame = new LogicFrame(world.Frame.Value + 1);
        }
    }

    private static uint CrcOf(VictoryConditionsCore core)
    {
        var visitor = new XferCrcVisitor();
        core.Xfer(visitor);
        return visitor.Value;
    }

    private static (FakeVictoryWorld World, VictoryConditionsCore Core) NewMatch(
        int playerCount,
        int localSlot = 0,
        bool multiplayer = true)
    {
        var world = new FakeVictoryWorld();
        for (var i = 0; i < playerCount; i++)
        {
            world.AddPlayer();
        }

        var core = new VictoryConditionsCore(world, multiplayer);
        core.Reset(localSlot);
        return (world, core);
    }

    // ---- the guard (GPL :149-150) ----

    [Fact]
    public void SinglePlayerMatch_SweepIsInert()
    {
        var (world, core) = NewMatch(2, localSlot: 0, multiplayer: false);
        world.Wipe(1);

        Advance(core, world, 5);

        Assert.False(core.SingleAllianceRemaining);
        Assert.False(core.IsDefeatedLatched(1));
        Assert.Empty(world.Eliminated);
        Assert.Equal(MatchOutcome.Undecided, core.CurrentOutcome);
    }

    // ---- 2p elimination ----

    [Fact]
    public void TwoPlayers_OpponentWiped_LatchesVictoryAtThatFrame()
    {
        var (world, core) = NewMatch(2);

        Advance(core, world, 3);
        Assert.False(core.SingleAllianceRemaining);
        Assert.Equal(MatchOutcome.Undecided, core.CurrentOutcome);
        Assert.Equal(0u, core.EndFrame.Value);

        // Frame 3: slot 1 loses its last victory object.
        world.Wipe(1);
        Advance(core, world);

        Assert.True(core.SingleAllianceRemaining);
        Assert.Equal(3u, core.EndFrame.Value);
        Assert.True(core.IsDefeatedLatched(1));
        Assert.False(core.IsDefeatedLatched(0));
        Assert.Equal(new[] { 0 }, core.Winners);
        Assert.Equal(new[] { 1 }, core.DefeatedSlots);
        Assert.Equal(new[] { 1 }, world.Eliminated);

        Assert.True(core.HasAchievedVictory(0));
        Assert.True(core.HasBeenDefeated(1));
        Assert.True(core.IsLocalAlliedVictory);
        Assert.False(core.IsLocalAlliedDefeat);
        Assert.False(core.IsLocalDefeat);
        Assert.Equal(MatchOutcome.LocalVictory, core.CurrentOutcome);
    }

    [Fact]
    public void TwoPlayers_LocalWiped_ReportsLocalDefeatAndPersonalDefeat()
    {
        var (world, core) = NewMatch(2, localSlot: 0);

        world.Wipe(0);
        Advance(core, world);

        Assert.Equal(MatchOutcome.LocalDefeat, core.CurrentOutcome);
        Assert.True(core.IsLocalAlliedDefeat);
        Assert.True(core.IsLocalDefeat);
        Assert.False(core.IsLocalAlliedVictory);
        Assert.Equal(new[] { 1 }, core.Winners);
    }

    [Fact]
    public void PlayerWithNothingOnFrameZero_IsLatchedSilentlyOnFrameZero()
    {
        // GPL's getFrame() > 1 guard gates only the presentation block, not the latch.
        var world = new FakeVictoryWorld();
        world.AddPlayer();
        world.AddPlayer(structures: false, units: false);
        var core = new VictoryConditionsCore(world, isMultiplayerMatch: true);
        core.Reset(0);

        Advance(core, world);

        Assert.Equal(0u, core.EndFrame.Value);
        Assert.True(core.SingleAllianceRemaining);
        Assert.Equal(new[] { 1 }, world.Eliminated);
    }

    // ---- latch-once ----

    [Fact]
    public void EndFrameAndWinners_AreWrittenExactlyOnce()
    {
        var (world, core) = NewMatch(3);
        world.Ally(0, 1);

        Advance(core, world, 2);
        world.Wipe(2);
        Advance(core, world); // decided on frame 2 with winners {0,1}

        Assert.Equal(2u, core.EndFrame.Value);
        Assert.Equal(new[] { 0, 1 }, core.Winners);

        // A winner dies afterwards: endFrame is never revised and the winner stays a winner,
        // but Phase B keeps latching, so DefeatedSlots grows.
        world.Wipe(1);
        Advance(core, world, 4);

        Assert.Equal(2u, core.EndFrame.Value);
        Assert.Equal(new[] { 0, 1 }, core.Winners);
        Assert.Equal(new[] { 1, 2 }, core.DefeatedSlots);
        Assert.True(core.HasAchievedVictory(0));
    }

    [Fact]
    public void OnPlayerEliminated_FiresExactlyOncePerSlot_EvenAcrossManyFrames()
    {
        var (world, core) = NewMatch(2);

        world.Wipe(1);
        Advance(core, world, 10);

        Assert.Equal(new[] { 1 }, world.Eliminated);

        // Even if the world says the slot came back, the one-way latch suppresses a re-fire.
        world.SetUnits(1, true);
        Advance(core, world, 5);
        Assert.Equal(new[] { 1 }, world.Eliminated);
    }

    // ---- alliances (§6.3) ----

    [Fact]
    public void TwoVsTwo_MutualAllies_MatchRunsUntilOneAllianceRemains()
    {
        var (world, core) = NewMatch(4, localSlot: 0);
        world.Ally(0, 1);
        world.Ally(2, 3);

        world.Wipe(0);
        Advance(core, world, 2);

        // Half of one team is gone; the match is NOT decided.
        Assert.False(core.SingleAllianceRemaining);
        Assert.True(core.IsDefeatedLatched(0));
        Assert.Equal(MatchOutcome.Undecided, core.CurrentOutcome);

        world.Wipe(1);
        Advance(core, world);

        Assert.True(core.SingleAllianceRemaining);
        Assert.Equal(2u, core.EndFrame.Value);
        Assert.Equal(new[] { 2, 3 }, core.Winners);
        Assert.True(core.HasAchievedVictory(2));
        Assert.True(core.HasAchievedVictory(3));
        Assert.True(core.HasBeenDefeated(0));
        Assert.True(core.HasBeenDefeated(1));
        Assert.Equal(MatchOutcome.LocalDefeat, core.CurrentOutcome);
    }

    [Fact]
    public void SurvivingAlly_GivesTheDeadLocalPlayerAnAlliedVictory()
    {
        var (world, core) = NewMatch(3, localSlot: 0);
        world.Ally(0, 1);

        world.Wipe(0);
        Advance(core, world, 2);
        world.Wipe(2);
        Advance(core, world);

        // Personally dead, alliance won: GPL reports allied victory AND personal defeat.
        Assert.True(core.IsDefeatedLatched(0));
        Assert.True(core.IsLocalDefeat);
        Assert.True(core.IsLocalAlliedVictory);
        Assert.False(core.IsLocalAlliedDefeat);
        Assert.Equal(MatchOutcome.LocalVictory, core.CurrentOutcome);
    }

    [Fact]
    public void OneSidedAllianceDeclaration_IsNotAnAlliance()
    {
        var (world, core) = NewMatch(2);
        world.AllyOneWay(0, 1); // slot 1 does not reciprocate

        Advance(core, world, 3);

        Assert.False(core.SingleAllianceRemaining);
        Assert.Equal(MatchOutcome.Undecided, core.CurrentOutcome);
    }

    [Fact]
    public void NonTransitiveGraph_GplComparesAgainstTheFirstLiveSlotOnly_AndDeclaresEarly()
    {
        // 0 is allied with both 1 and 2; 1 and 2 are enemies of each other. GPL compares
        // every later live slot against the FIRST live slot only, so it declares a single
        // alliance while two mutual enemies are still standing. Ported as-is (§6.3).
        var (world, core) = NewMatch(3);
        world.Ally(0, 1);
        world.Ally(0, 2);

        Advance(core, world);

        Assert.True(core.SingleAllianceRemaining);
        Assert.Equal(new[] { 0, 1, 2 }, core.Winners);
    }

    // ---- observers (§1.6, §1.7) ----

    [Fact]
    public void NoLocalPlayer_IsObserver_AndPersonalDefeatIsPreLatched()
    {
        var (world, core) = NewMatch(2, localSlot: -1);

        Assert.True(core.IsObserver);
        Assert.True(core.LocalPlayerDefeatedLatched);
        Assert.False(core.IsLocalDefeat); // isLocalDefeat() is FALSE for an observer
        Assert.False(core.IsLocalAlliedDefeat);
        Assert.False(core.IsLocalAlliedVictory);
        Assert.Equal(MatchOutcome.Undecided, core.CurrentOutcome);

        world.Wipe(1);
        Advance(core, world);

        Assert.True(core.SingleAllianceRemaining);
        Assert.True(core.IsLocalAlliedDefeat); // the observer "loses" when the match ends
        Assert.False(core.IsLocalDefeat);
        Assert.False(core.IsLocalAlliedVictory);
        Assert.Equal(MatchOutcome.ObserverEnd, core.CurrentOutcome);
        Assert.Equal(-1, core.LocalSlot);
    }

    // ---- flag variants (§1.2, §6.1) ----

    [Fact]
    public void DefaultFlags_AreBothConditions()
    {
        var (_, core) = NewMatch(2);
        Assert.Equal(VictoryFlags.NoBuildings | VictoryFlags.NoUnits, core.VictoryConditions);
        Assert.Equal(VictoryConditionsCore.DefaultVictoryFlags, core.VictoryConditions);
    }

    [Fact]
    public void BothFlags_RequireEveryVictoryObjectGone()
    {
        var (world, core) = NewMatch(2);

        world.SetStructures(1, false); // units remain
        Advance(core, world, 3);
        Assert.False(core.SingleAllianceRemaining);

        world.SetUnits(1, false);
        Advance(core, world);
        Assert.True(core.SingleAllianceRemaining);
        Assert.Equal(3u, core.EndFrame.Value);
    }

    [Fact]
    public void NoBuildingsOnly_EliminatesWhileTheArmyStillStands()
    {
        var (world, core) = NewMatch(2);
        core.VictoryConditions = VictoryFlags.NoBuildings;

        world.SetStructures(1, false); // army untouched — retail ZH's "raze their base"
        Advance(core, world);

        Assert.True(core.SingleAllianceRemaining);
        Assert.True(core.IsDefeatedLatched(1));
    }

    [Fact]
    public void NoUnitsOnly_IgnoresStructures()
    {
        var (world, core) = NewMatch(2);
        core.VictoryConditions = VictoryFlags.NoUnits;

        world.SetStructures(1, false);
        Advance(core, world);
        Assert.False(core.SingleAllianceRemaining); // units still alive

        world.SetUnits(1, false);
        Advance(core, world);
        Assert.True(core.SingleAllianceRemaining);
    }

    [Fact]
    public void NoFlags_NobodyIsEverEliminated()
    {
        var (world, core) = NewMatch(2);
        core.VictoryConditions = VictoryFlags.None;

        world.Wipe(0);
        world.Wipe(1);
        Advance(core, world, 5);

        Assert.False(core.IsDefeatedLatched(0));
        Assert.False(core.IsDefeatedLatched(1));
        Assert.Empty(world.Eliminated);
        // Nobody is defeated, so both slots are live and non-allied: two alliances remain.
        Assert.False(core.SingleAllianceRemaining);
    }

    // ---- the mutual wipe (§5.2 Draw) ----

    [Fact]
    public void EveryoneDiesOnTheSameFrame_IsADrawWithNoWinners()
    {
        var (world, core) = NewMatch(2);

        world.Wipe(0);
        world.Wipe(1);
        Advance(core, world);

        Assert.True(core.SingleAllianceRemaining);
        Assert.True(core.IsMutualWipe);
        Assert.Empty(core.Winners);
        Assert.Equal(new[] { 0, 1 }, core.DefeatedSlots);
        Assert.Equal(MatchOutcome.Draw, core.CurrentOutcome);

        // The local player's own defeat-shaped state is still set, per §5.2.
        Assert.True(core.IsLocalAlliedDefeat);
        Assert.True(core.IsLocalDefeat);
        Assert.False(core.IsLocalAlliedVictory);
    }

    // ---- MatchVerdict (§5) ----

    [Fact]
    public void Verdict_CarriesTheFrozenSchemaAndTheCoresState()
    {
        var (world, core) = NewMatch(2);
        Advance(core, world, 4);
        world.Wipe(1);
        Advance(core, world);

        var verdict = MatchVerdict.From(core, MatchEndReason.Elimination, new[] { "player_1", "player_2" });

        Assert.Equal("MATCH-VERDICT-V1", verdict.Schema);
        Assert.Equal(MatchVerdict.SchemaId, verdict.Schema);
        Assert.Equal(MatchOutcome.LocalVictory, verdict.Outcome);
        Assert.Equal(MatchEndReason.Elimination, verdict.Reason);
        Assert.Equal(4u, verdict.EndFrame);
        Assert.Equal(0, verdict.LocalSlot);
        Assert.Equal(new[] { 0 }, verdict.Winners);
        Assert.Equal(new[] { 1 }, verdict.Defeated);
        Assert.Equal(new[] { "player_1", "player_2" }, verdict.PlayerNames);
        Assert.False(verdict.Observer);
    }

    [Fact]
    public void Verdict_UndecidedMatchHasEndFrameZero()
    {
        var (world, core) = NewMatch(2);
        Advance(core, world, 3);

        var verdict = MatchVerdict.From(core, MatchEndReason.NotEnded, null);

        Assert.Equal(MatchOutcome.Undecided, verdict.Outcome);
        Assert.Equal(0u, verdict.EndFrame);
        Assert.Empty(verdict.Winners);
        Assert.Equal(2, verdict.PlayerNames.Count);
    }

    [Fact]
    public void Verdict_MapExitIsAlwaysUndecided()
    {
        var (world, core) = NewMatch(2);
        world.Wipe(1);
        Advance(core, world);

        var verdict = MatchVerdict.From(core, MatchEndReason.MapExit, null);

        Assert.Equal(MatchEndReason.MapExit, verdict.Reason);
        Assert.Equal(MatchOutcome.Undecided, verdict.Outcome);
    }

    // ---- persistence (§4) ----

    [Fact]
    public void Xfer_RoundTrip_RestoresEveryLatchAndIsCrcIdentical()
    {
        var (world, core) = NewMatch(4, localSlot: 1);
        core.VictoryConditions = VictoryFlags.NoBuildings;
        world.Ally(0, 1);
        world.Ally(2, 3);

        Advance(core, world, 2);
        world.SetStructures(2, false);
        Advance(core, world, 2);
        world.SetStructures(3, false);
        Advance(core, world);

        Assert.True(core.SingleAllianceRemaining);
        Assert.Equal(new[] { 0, 1 }, core.Winners);

        var stream = new MemoryStream();
        using (var save = new XferSave(stream, leaveOpen: true))
        {
            core.Xfer(save);
        }

        var restored = new VictoryConditionsCore(world, isMultiplayerMatch: true);
        stream.Position = 0;
        using (var load = new XferLoad(stream, leaveOpen: true))
        {
            restored.Xfer(load);
        }

        Assert.Equal(CrcOf(core), CrcOf(restored));
        Assert.Equal(core.VictoryConditions, restored.VictoryConditions);
        Assert.Equal(core.LocalSlot, restored.LocalSlot);
        Assert.Equal(core.EndFrame.Value, restored.EndFrame.Value);
        Assert.Equal(core.SingleAllianceRemaining, restored.SingleAllianceRemaining);
        Assert.Equal(core.LocalPlayerDefeatedLatched, restored.LocalPlayerDefeatedLatched);
        Assert.Equal(core.IsObserver, restored.IsObserver);
        Assert.Equal(core.PlayerCount, restored.PlayerCount);
        Assert.Equal(core.DefeatedSlots, restored.DefeatedSlots);
        Assert.Equal(core.Winners, restored.Winners);
        Assert.Equal(core.CurrentOutcome, restored.CurrentOutcome);
    }

    [Fact]
    public void Xfer_MidMatchSaveLoad_ContinuationMatchesAnUnperturbedRun()
    {
        // Reference run: slot 1 wiped on frame 5, decided on frame 5.
        var (worldA, reference) = NewMatch(2);
        Advance(reference, worldA, 5);
        worldA.Wipe(1);
        Advance(reference, worldA, 3);
        Assert.Equal(5u, reference.EndFrame.Value);

        // Same run, saved at frame 3 into a fresh core, then continued.
        var (worldB, original) = NewMatch(2);
        Advance(original, worldB, 3);

        var stream = new MemoryStream();
        using (var save = new XferSave(stream, leaveOpen: true))
        {
            original.Xfer(save);
        }

        var restored = new VictoryConditionsCore(worldB, isMultiplayerMatch: true);
        stream.Position = 0;
        using (var load = new XferLoad(stream, leaveOpen: true))
        {
            restored.Xfer(load);
        }

        Advance(restored, worldB, 2);
        worldB.Wipe(1);
        Advance(restored, worldB, 3);

        Assert.Equal(reference.EndFrame.Value, restored.EndFrame.Value);
        Assert.Equal(reference.Winners, restored.Winners);
        Assert.Equal(reference.DefeatedSlots, restored.DefeatedSlots);
        Assert.Equal(CrcOf(reference), CrcOf(restored));
    }

    [Fact]
    public void Reset_RejectsALocalSlotOutsideTheCachedPool()
    {
        var world = new FakeVictoryWorld();
        world.AddPlayer();
        world.AddPlayer();
        var core = new VictoryConditionsCore(world, isMultiplayerMatch: true);

        Assert.Throws<System.ArgumentOutOfRangeException>(() => core.Reset(2));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => core.Reset(-2));
    }
}
