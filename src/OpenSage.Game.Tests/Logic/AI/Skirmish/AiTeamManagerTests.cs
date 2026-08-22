#nullable enable

// S9-08 (R15 L3) gate tests: AiTeam + AiTeamManager v1 - the team half of the dr-0039 M-c
// criterion (">= 1 team Ready").
//
// THE TEST THAT MATTERS MOST IN THIS FILE
//
// HordeMembers_AreNeverRecruited. A ten-orc horde is eleven objects in the snapshot: the HORDE
// object plus ten members carrying ParentHorde. AIUpdate.SetTargetPoint early-outs on
// ParentHorde, so every move order addressed to a member is a silent no-op - no error, no log.
// An AI that recruited members would form full-looking teams, emit correct-looking orders and
// never move a unit. If that test ever goes red, nothing else in the lane is trustworthy.
//
// The rest pin: one team per unit, ascending-id recruitment that does not depend on snapshot
// order, the Building -> Ready -> Tasked -> Retreating -> Disbanded machine (including which
// transitions are refused), and order-preserving compaction.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using OpenSage.Logic.AI;
using OpenSage.Logic.AI.Skirmish;
using Xunit;

namespace OpenSage.Tests.Logic.AI.Skirmish;

public class AiTeamManagerTests
{
    private const uint NoHeartbeat = 1_000_003;

    private const int PlayerIndex = 2;

    private sealed class Fixture
    {
        public required FakeAiWorldView World { get; init; }

        public required SkirmishAIBrain Brain { get; init; }

        public required AiTeamManager Manager { get; init; }

        public void Tick() => Brain.Update();

        public void TickThrough(uint frame)
        {
            while (World.CurrentFrame < frame)
            {
                World.AdvanceFrame();
                Brain.Update();
            }
        }

        public int Count(string counter) => Brain.Trace.GetCount(counter);
    }

    private static Fixture NewFixture(
        int teamSize = 2,
        int maxTeams = AiTeamManager.DefaultMaxTeams,
        uint regroupFrames = AiTeamManager.DefaultRegroupFrames)
    {
        var world = new FakeAiWorldView { PlayerIndex = PlayerIndex };
        var brain = new SkirmishAIBrain(
            world,
            new RecordingOrderSink(),
            new AiTrace(world.PlayerIndex, new RecordingAiTraceSink()),
            NoHeartbeat);

        var manager = new AiTeamManager(maxTeams, regroupFrames, teamSize);
        brain.RegisterManager(manager);

        return new Fixture { World = world, Brain = brain, Manager = manager };
    }

    private static AiObjectView Unit(uint id, bool isHorde = false, bool isHordeMember = false, bool underConstruction = false)
        => new(new ObjectId(id), isHorde ? "MordorFighterHorde" : "MordorFighter", Vector3.Zero, PlayerIndex,
            false, underConstruction, 1.0f, isHorde, isHordeMember);

    private static AiObjectView Structure(uint id)
        => new(new ObjectId(id), "MordorOrcPit", Vector3.Zero, PlayerIndex, true, false, 1.0f);

    private static void SetPrivate(object target, string propertyName, object value)
    {
        var property = target.GetType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (property is null)
        {
            throw new InvalidOperationException($"Property '{propertyName}' not found on {target.GetType()}.");
        }

        property.SetValue(target, value);
    }

    private static AIData MakeAiData(int minInfantryForGroup)
    {
        var data = new AIData();
        SetPrivate(data, nameof(AIData.MinInfantryForGroup), minInfantryForGroup);
        return data;
    }

    private static SkirmishAIData MakeSkirmishAiData(bool disableTeamBuilding)
    {
        var data = new SkirmishAIData();
        SetPrivate(data, nameof(SkirmishAIData.DisableTeamBuilding), disableTeamBuilding);
        return data;
    }

    private static IReadOnlyList<uint> MemberIds(AiTeam team)
    {
        var ids = new List<uint>();

        for (var i = 0; i < team.Members.Count; i++)
        {
            ids.Add(team.Members[i].Index);
        }

        return ids;
    }

    // ---- identity ---------------------------------------------------------------------------

    [Fact]
    public void Name_IsTeam()
    {
        Assert.Equal("team", AiTeamManager.ManagerName);
        Assert.Equal("team", NewFixture().Manager.Name);
    }

    [Fact]
    public void GradingCounterName_IsStable()
        => Assert.Equal("team.ready", AiTeamManager.TeamReadyCounter);

    // ---- THE horde rule -----------------------------------------------------------------------

    [Fact]
    public void HordeMembers_AreNeverRecruited()
    {
        // One horde (id 1) containing ten members (ids 2..11), plus one standalone unit (id 12).
        // The team must hold exactly {1, 12}: ordering a horde member is a silent no-op, so a
        // team of members is an army that never moves.
        var fixture = NewFixture(teamSize: 2);
        fixture.World.Own.Add(Unit(1, isHorde: true));

        for (uint member = 2; member <= 11; member++)
        {
            fixture.World.Own.Add(Unit(member, isHordeMember: true));
        }

        fixture.World.Own.Add(Unit(12));

        fixture.Tick();

        var team = Assert.Single(fixture.Manager.Teams);
        Assert.Equal(new uint[] { 1, 12 }, MemberIds(team));
        Assert.Equal(AiTeamState.Ready, team.State);
    }

    [Fact]
    public void StructuresAndUnfinishedUnits_AreNeverRecruited()
    {
        var fixture = NewFixture(teamSize: 1);
        fixture.World.Own.Add(Structure(1));
        fixture.World.Own.Add(Unit(2, underConstruction: true));

        fixture.Tick();

        Assert.Empty(fixture.Manager.Teams);
        Assert.Equal(0, fixture.Manager.UnitsRecruited);
    }

    // ---- recruitment ---------------------------------------------------------------------------

    [Fact]
    public void Recruitment_IsIndependentOfSnapshotOrder()
    {
        // The shuffled-input determinism assert: the same SET of units must produce the same
        // teams with the same members in the same order, whatever order the snapshot listed them.
        var ascending = RecruitInto(new uint[] { 1, 2, 3, 4, 5 });
        var shuffled = RecruitInto(new uint[] { 4, 1, 5, 3, 2 });
        var descending = RecruitInto(new uint[] { 5, 4, 3, 2, 1 });

        Assert.Equal(ascending, shuffled);
        Assert.Equal(ascending, descending);

        // And the content is the ascending-id partition, not merely a stable arbitrary one.
        Assert.Equal(new[] { "1,2", "3,4", "5" }, ascending);
    }

    private static IReadOnlyList<string> RecruitInto(uint[] ids)
    {
        var fixture = NewFixture(teamSize: 2);

        foreach (var id in ids)
        {
            fixture.World.Own.Add(Unit(id));
        }

        fixture.Tick();

        var shapes = new List<string>();

        foreach (var team in fixture.Manager.Teams)
        {
            shapes.Add(string.Join(',', MemberIds(team)));
        }

        return shapes;
    }

    [Fact]
    public void OneTeamPerUnit_IsEnforced()
    {
        var fixture = NewFixture(teamSize: 2);
        fixture.World.Own.Add(Unit(1));
        fixture.World.Own.Add(Unit(2));
        fixture.World.Own.Add(Unit(3));

        // Tick twice: the second pass sees the same units and must not re-recruit any of them.
        fixture.Tick();
        fixture.World.AdvanceFrame();
        fixture.Tick();

        Assert.Equal(3, fixture.Manager.UnitsRecruited);

        var seen = new List<uint>();

        foreach (var team in fixture.Manager.Teams)
        {
            foreach (var id in MemberIds(team))
            {
                Assert.DoesNotContain(id, seen);
                seen.Add(id);
            }
        }

        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public void TeamCap_BoundsTheNumberOfTeams()
    {
        var fixture = NewFixture(teamSize: 1, maxTeams: 2);

        for (uint id = 1; id <= 5; id++)
        {
            fixture.World.Own.Add(Unit(id));
        }

        fixture.Tick();

        Assert.Equal(2, fixture.Manager.Teams.Count);
        Assert.Equal(2, fixture.Manager.UnitsRecruited);
    }

    // ---- the M-c signal --------------------------------------------------------------------------

    [Fact]
    public void FullTeam_ReachesReady_AndBumpsTheGradingCounter()
    {
        var fixture = NewFixture(teamSize: 3);
        fixture.World.Own.Add(Unit(1));
        fixture.World.Own.Add(Unit(2));

        fixture.Tick();

        var team = Assert.Single(fixture.Manager.Teams);
        Assert.Equal(AiTeamState.Building, team.State);
        Assert.Equal(0, fixture.Count(AiTeamManager.TeamReadyCounter));

        fixture.World.Own.Add(Unit(3));
        fixture.World.AdvanceFrame();
        fixture.Tick();

        Assert.Equal(AiTeamState.Ready, team.State);
        Assert.Equal(1, fixture.Count(AiTeamManager.TeamReadyCounter));
        Assert.Equal(1, fixture.Manager.TeamsReady);
        Assert.Same(team, fixture.Manager.NextReadyTeam());
    }

    // ---- group-size seed ---------------------------------------------------------------------------

    [Fact]
    public void GroupSize_ComesFromAiData_AndDegradesToTheDefault()
    {
        Assert.Equal(4, AiTeamManager.GroupSize(MakeAiData(4)));
        Assert.Equal(AiTeamManager.DefaultTeamSize, AiTeamManager.GroupSize(null));
        Assert.Equal(AiTeamManager.DefaultTeamSize, AiTeamManager.GroupSize(MakeAiData(0)));
        Assert.Equal(AiTeamManager.MaxTeamSize, AiTeamManager.GroupSize(MakeAiData(9999)));
    }

    [Fact]
    public void GroupSizeSeed_IsUsed_WhenNoOverrideIsGiven()
    {
        var world = new FakeAiWorldView { PlayerIndex = PlayerIndex, AIData = MakeAiData(3) };
        var brain = new SkirmishAIBrain(world, new RecordingOrderSink(), new AiTrace(PlayerIndex), NoHeartbeat);
        var manager = new AiTeamManager();
        brain.RegisterManager(manager);

        world.Own.Add(Unit(1));
        brain.Update();

        Assert.Equal(3, manager.TeamSize);
        Assert.Equal(3, Assert.Single(manager.Teams).TargetSize);
    }

    // ---- lifecycle ------------------------------------------------------------------------------

    [Fact]
    public void DeadMembers_AreDropped_AndAWipedTeamDisbandsAndCompactsAway()
    {
        var fixture = NewFixture(teamSize: 2);
        fixture.World.Own.Add(Unit(1));
        fixture.World.Own.Add(Unit(2));

        fixture.Tick();
        Assert.Single(fixture.Manager.Teams);

        fixture.World.Own.RemoveAt(1);
        fixture.World.AdvanceFrame();
        fixture.Tick();

        Assert.Equal(1, fixture.Count(AiTeamManager.TeamMemberLostCounter));
        Assert.Single(fixture.Manager.Teams);

        fixture.World.Own.Clear();
        fixture.World.AdvanceFrame();
        fixture.Tick();

        Assert.Equal(1, fixture.Count(AiTeamManager.TeamDisbandedCounter));
        Assert.Empty(fixture.Manager.Teams);
    }

    [Fact]
    public void Compaction_PreservesTheOrderOfSurvivingTeams()
    {
        var fixture = NewFixture(teamSize: 1, maxTeams: 4);
        fixture.World.Own.Add(Unit(1));
        fixture.World.Own.Add(Unit(2));
        fixture.World.Own.Add(Unit(3));

        fixture.Tick();
        Assert.Equal(new[] { 1, 2, 3 }, TeamIds(fixture));

        // Kill the middle team's unit: teams 1 and 3 must stay in that relative order.
        fixture.World.Own.RemoveAt(1);
        fixture.World.AdvanceFrame();
        fixture.Tick();

        Assert.Equal(new[] { 1, 3 }, TeamIds(fixture));
    }

    private static int[] TeamIds(Fixture fixture)
    {
        var ids = new List<int>();

        foreach (var team in fixture.Manager.Teams)
        {
            ids.Add(team.Id);
        }

        return ids.ToArray();
    }

    [Fact]
    public void MauledTaskedTeam_Retreats_ThenRegroupsToReady()
    {
        var fixture = NewFixture(teamSize: 4, regroupFrames: 5);

        for (uint id = 1; id <= 4; id++)
        {
            fixture.World.Own.Add(Unit(id));
        }

        fixture.Tick();

        var team = Assert.Single(fixture.Manager.Teams);
        Assert.Equal(AiTeamState.Ready, team.State);
        Assert.True(fixture.Manager.TaskTeam(fixture.Brain, team, fixture.World.CurrentFrame));
        Assert.Equal(AiTeamState.Tasked, team.State);

        // Half the team dies: below 50% of peak, so it is pulled back.
        fixture.World.Own.RemoveRange(2, 2);
        fixture.World.AdvanceFrame();
        fixture.Tick();
        Assert.Equal(AiTeamState.Tasked, team.State); // exactly 50% is not BELOW 50%

        fixture.World.Own.RemoveAt(1);
        fixture.World.AdvanceFrame();
        fixture.Tick();

        Assert.Equal(AiTeamState.Retreating, team.State);
        Assert.Equal(1, fixture.Count(AiTeamManager.TeamRetreatCounter));

        var retreatFrame = team.StateSinceFrame;

        // T+1: a 5-frame regroup is still running on frame retreatFrame + 5.
        fixture.TickThrough(retreatFrame + 5);
        Assert.Equal(AiTeamState.Retreating, team.State);

        fixture.TickThrough(retreatFrame + 6);
        Assert.Equal(AiTeamState.Ready, team.State);
    }

    [Fact]
    public void DisableTeamBuilding_StopsTheManagerDead()
    {
        var fixture = NewFixture(teamSize: 1);
        fixture.World.SkirmishAIData = MakeSkirmishAiData(disableTeamBuilding: true);
        fixture.World.Own.Add(Unit(1));

        fixture.TickThrough(10);

        Assert.Empty(fixture.Manager.Teams);
        Assert.Equal(0, fixture.Manager.UnitsRecruited);
    }

    // ---- AiTeam, on its own ------------------------------------------------------------------------

    [Fact]
    public void Team_HoldsMembersInAscendingId_WhateverOrderTheyArrivedIn()
    {
        var team = new AiTeam(1, 4, 0);

        Assert.True(team.TryAddMember(new ObjectId(9)));
        Assert.True(team.TryAddMember(new ObjectId(3)));
        Assert.True(team.TryAddMember(new ObjectId(7)));

        Assert.Equal(new uint[] { 3, 7, 9 }, MemberIds(team));
    }

    [Fact]
    public void Team_RefusesDuplicatesAndInvalidIds()
    {
        var team = new AiTeam(1, 4, 0);

        Assert.True(team.TryAddMember(new ObjectId(3)));
        Assert.False(team.TryAddMember(new ObjectId(3)));
        Assert.False(team.TryAddMember(ObjectId.Invalid));
        Assert.Single(team.Members);
    }

    [Fact]
    public void Team_RefusesIllegalTransitions()
    {
        var team = new AiTeam(1, 2, 0);

        // Building, not full: cannot be Ready, and a non-Ready team cannot be tasked.
        Assert.False(team.MarkReady(1));
        Assert.False(team.MarkTasked(1));
        Assert.False(team.MarkRetreating(1));

        team.TryAddMember(new ObjectId(1));
        team.TryAddMember(new ObjectId(2));

        Assert.True(team.MarkReady(2));
        Assert.False(team.MarkRetreating(3)); // Ready -> Retreating is not a transition
        Assert.True(team.MarkTasked(4));
        Assert.True(team.MarkRetreating(5));
        Assert.Equal(5u, team.StateSinceFrame);
    }

    [Fact]
    public void Team_DisbandIsTerminal_AndDropsItsMembers()
    {
        var team = new AiTeam(1, 1, 0);
        team.TryAddMember(new ObjectId(1));

        Assert.True(team.Disband(3));
        Assert.Empty(team.Members);
        Assert.True(team.IsDisbanded);
        Assert.False(team.Disband(4));
        Assert.False(team.TryAddMember(new ObjectId(2)));
        Assert.False(team.MarkReady(5));
    }

    [Fact]
    public void Team_TargetSizeMustBePositive()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new AiTeam(1, 0, 0));

    [Fact]
    public void ShouldRetreat_UsesIntPercentOfPeak()
    {
        var team = new AiTeam(1, 4, 0);

        for (uint id = 1; id <= 4; id++)
        {
            team.TryAddMember(new ObjectId(id));
        }

        Assert.False(AiTeamManager.ShouldRetreat(team));

        team.RemoveMember(new ObjectId(4));
        team.RemoveMember(new ObjectId(3));
        Assert.False(AiTeamManager.ShouldRetreat(team)); // 2 of 4 is exactly 50%

        team.RemoveMember(new ObjectId(2));
        Assert.True(AiTeamManager.ShouldRetreat(team));
    }
}
