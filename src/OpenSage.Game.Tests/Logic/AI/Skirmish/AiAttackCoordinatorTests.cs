#nullable enable

// S9-09 (R15 L3) gate tests: AiAttackCoordinator v1 - the dr-0039 M-d criterion
// (">= 1 wave launched, engagements > 0").
//
// THE TEST THAT MATTERS MOST IN THIS FILE
//
// ReadyTeam_LaunchesAWave_AndEmitsExplicitAttackOrders. It pins the packet's key design choice:
// the coordinator emits SetSelection + AttackObject ITSELF, on a cadence, rather than handing the
// team to the engine's attack-move state. The attack-move path is a shell on this fork (Part I of
// design-aiupdate.md is deferred), so a wave routed through it would walk somewhere and stop.
// If that test is ever "simplified" into asserting a MoveTo, the AI stops fighting and every
// symptom looks like a unit-AI problem instead of an order-shape problem.
//
// NOTHING HERE ASSERTS KILL ATTRIBUTION. These tests observe orders and coordinator state only;
// whether a target actually dies is the sim's business and is not a property of this manager.
//
// The rest pin: the re-issue cadence, retargeting off a dead target, the
// retreat -> muster -> relaunch / disband-and-re-recruit arc, the concurrent-wave cap, the
// one-wave-per-team rule, the data off switch, and difficulty scaling.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using OpenSage.Logic.AI;
using OpenSage.Logic.AI.Skirmish;
using OpenSage.Logic.Orders;
using Xunit;

namespace OpenSage.Tests.Logic.AI.Skirmish;

public class AiAttackCoordinatorTests
{
    private const uint NoHeartbeat = 1_000_003;

    private const int PlayerIndex = 0;

    private const int EnemyIndex = 1;

    private sealed class Fixture
    {
        public required FakeAiWorldView World { get; init; }

        public required SkirmishAIBrain Brain { get; init; }

        public required RecordingOrderSink Sink { get; init; }

        public required AiTeamManager Teams { get; init; }

        public required AiAttackCoordinator Coordinator { get; init; }

        public void Tick() => Brain.Update();

        /// <summary>Advances the fake clock one frame at a time, ticking the brain on each.</summary>
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
        uint waveInterval = 0,
        uint reissueInterval = 0,
        int maxWaves = 0,
        uint regroupFrames = AiTeamManager.DefaultRegroupFrames)
    {
        var world = new FakeAiWorldView { PlayerIndex = PlayerIndex };
        var sink = new RecordingOrderSink();
        var brain = new SkirmishAIBrain(
            world,
            sink,
            new AiTrace(world.PlayerIndex, new RecordingAiTraceSink()),
            NoHeartbeat);

        // Registration order mirrors SkirmishAIBrains.RegisterManagers: emitter first (it rolls
        // the frame budget), team manager, then the coordinator last so it sees this frame's teams.
        var emitter = new AiOrderEmitter(brain);
        brain.RegisterManager(emitter);

        var teams = new AiTeamManager(AiTeamManager.DefaultMaxTeams, regroupFrames, teamSize);
        brain.RegisterManager(teams);

        var coordinator = new AiAttackCoordinator(emitter, teams, waveInterval, reissueInterval, maxWaves);
        brain.RegisterManager(coordinator);

        return new Fixture
        {
            World = world,
            Brain = brain,
            Sink = sink,
            Teams = teams,
            Coordinator = coordinator,
        };
    }

    private static AiObjectView Unit(uint id, int owner = PlayerIndex, float x = 0f)
        => new(new ObjectId(id), "MordorFighter", new Vector3(x, 0f, 0f), owner, false, false, 1.0f);

    private static AiObjectView Structure(uint id, int owner = PlayerIndex, float x = 0f, float z = 0f)
        => new(new ObjectId(id), "MordorOrcPit", new Vector3(x, 0f, z), owner, true, false, 1.0f);

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

    private static SkirmishAIData MakeSkirmishAiData(bool disableTacticalAi)
    {
        var data = new SkirmishAIData();
        SetPrivate(data, nameof(SkirmishAIData.DisableTacticalAI), disableTacticalAi);
        return data;
    }

    /// <summary>Two own units and one enemy structure: the smallest world that produces a wave.</summary>
    private static void SeedOneWaveWorld(Fixture fixture, uint enemyId = 100)
    {
        fixture.World.Own.Add(Unit(1));
        fixture.World.Own.Add(Unit(2));
        fixture.World.Enemy.Add(Structure(enemyId, EnemyIndex, x: 500f));
    }

    // ---- M-d: a wave launches, and it launches as an explicit attack ------------------------

    [Fact]
    public void ReadyTeam_LaunchesAWave_AndEmitsExplicitAttackOrders()
    {
        var fixture = NewFixture();
        SeedOneWaveWorld(fixture);

        fixture.Tick();

        Assert.Equal(1, fixture.Coordinator.WavesLaunched);
        Assert.Equal(1, fixture.Count(AiAttackCoordinator.WaveLaunchedCounter));
        Assert.Equal(1, fixture.Count(AiAttackCoordinator.EngageCounter));

        // THE shape assertion: a selection naming the team, immediately followed by an
        // AttackObject naming the target. Not a MoveTo, not an attack-move.
        Assert.Equal(2, fixture.Sink.Count);
        Assert.Equal(OrderType.SetSelection, fixture.Sink.Orders[0].OrderType);
        Assert.Equal(OrderType.AttackObject, fixture.Sink.Orders[1].OrderType);
        Assert.Equal(new ObjectId(100), fixture.Sink.Orders[1].Arguments[0].Value.ObjectId);

        var selected = fixture.Sink.Orders[0].Arguments.Skip(1).Select(a => a.Value.ObjectId).ToArray();
        Assert.Equal(new[] { new ObjectId(1), new ObjectId(2) }, selected);

        var wave = Assert.Single(fixture.Coordinator.Waves);
        Assert.Equal(AiWaveState.Engaging, wave.State);
        Assert.Equal(new ObjectId(100), wave.TargetId);
        Assert.Equal(AiTeamState.Tasked, wave.Team.State);
    }

    [Fact]
    public void NeverForceAttacks()
    {
        // ForceAttackObject ignores alliance. An AI that used it would order waves onto neutrals
        // and allies the moment the scorer saw one, which is exactly the "the AI cheats/grief"
        // class of bug the order-shape discipline exists to prevent.
        var fixture = NewFixture();
        SeedOneWaveWorld(fixture);

        fixture.TickThrough(5);

        Assert.All(fixture.Sink.Orders, o => Assert.NotEqual(OrderType.ForceAttackObject, o.OrderType));
    }

    [Fact]
    public void NoWave_WhileNoTeamIsReady()
    {
        var fixture = NewFixture(teamSize: 3);
        fixture.World.Own.Add(Unit(1));
        fixture.World.Own.Add(Unit(2));
        fixture.World.Enemy.Add(Structure(100, EnemyIndex));

        fixture.TickThrough(20);

        Assert.Equal(0, fixture.Coordinator.WavesLaunched);
        Assert.Empty(fixture.Sink.Orders);
    }

    [Fact]
    public void NoLegalTarget_IsCountedAndDoesNotBurnTheCadence()
    {
        // Long cadence: if a target-less frame consumed it, the AI would sit out the next 1500
        // frames after merely glancing at an empty snapshot.
        var fixture = NewFixture(waveInterval: 1500);
        fixture.World.Own.Add(Unit(1));
        fixture.World.Own.Add(Unit(2));

        fixture.TickThrough(3);

        Assert.Equal(0, fixture.Coordinator.WavesLaunched);
        Assert.True(fixture.Count(AiAttackCoordinator.NoTargetCounter) > 0);

        fixture.World.Enemy.Add(Structure(100, EnemyIndex));
        fixture.TickThrough(4);

        Assert.Equal(1, fixture.Coordinator.WavesLaunched);
    }

    [Fact]
    public void HordeMembers_AreNeverTargeted()
    {
        var fixture = NewFixture();
        fixture.World.Own.Add(Unit(1));
        fixture.World.Own.Add(Unit(2));

        // The horde object is FURTHER away than its members: only the legality rule can pick it.
        fixture.World.Enemy.Add(new AiObjectView(
            new ObjectId(200), "MordorFighter", new Vector3(100f, 0f, 0f), EnemyIndex,
            false, false, 1.0f, false, true));
        fixture.World.Enemy.Add(new AiObjectView(
            new ObjectId(201), "MordorFighterHorde", new Vector3(900f, 0f, 0f), EnemyIndex,
            false, false, 1.0f, true, false));

        fixture.Tick();

        var wave = Assert.Single(fixture.Coordinator.Waves);
        Assert.Equal(new ObjectId(201), wave.TargetId);
    }

    // ---- the coordinator-owned engage loop ---------------------------------------------------

    [Fact]
    public void EngagedWave_ReissuesItsAttackOnTheCadence_AndNotBefore()
    {
        var fixture = NewFixture(reissueInterval: 10);
        SeedOneWaveWorld(fixture);

        fixture.Tick();
        Assert.Equal(2, fixture.Sink.Count);

        fixture.TickThrough(9);
        Assert.Equal(2, fixture.Sink.Count);

        fixture.TickThrough(10);
        Assert.Equal(4, fixture.Sink.Count);
        Assert.Equal(2, fixture.Count(AiAttackCoordinator.EngageCounter));

        // A re-issue onto the same target is not a retarget.
        Assert.Equal(0, fixture.Count(AiAttackCoordinator.RetargetCounter));
        Assert.Equal(OrderType.AttackObject, fixture.Sink.Orders[3].OrderType);
        Assert.Equal(new ObjectId(100), fixture.Sink.Orders[3].Arguments[0].Value.ObjectId);
    }

    [Fact]
    public void TargetLeavingTheSnapshot_RetargetsImmediately_WithoutWaitingForTheCadence()
    {
        var fixture = NewFixture(reissueInterval: 10_000);
        SeedOneWaveWorld(fixture);

        fixture.Tick();
        var wave = Assert.Single(fixture.Coordinator.Waves);
        Assert.Equal(new ObjectId(100), wave.TargetId);

        fixture.World.Enemy.Clear();
        fixture.World.Enemy.Add(Structure(101, EnemyIndex, x: 700f));

        fixture.TickThrough(1);

        Assert.Equal(new ObjectId(101), wave.TargetId);
        Assert.Equal(1, fixture.Count(AiAttackCoordinator.RetargetCounter));
        Assert.Equal(2, fixture.Count(AiAttackCoordinator.EngageCounter));
    }

    [Fact]
    public void LastEnemyGone_EndsTheWaveAndReleasesTheTeam()
    {
        var fixture = NewFixture();
        SeedOneWaveWorld(fixture);

        fixture.Tick();
        var team = Assert.Single(fixture.Teams.Teams);

        fixture.World.Enemy.Clear();
        fixture.TickThrough(1);

        Assert.Empty(fixture.Coordinator.Waves);
        Assert.Equal(1, fixture.Count(AiAttackCoordinator.WaveEndedCounter));

        // Released, not dissolved: the team goes back through Retreating so AiTeamManager's own
        // regroup timer returns it to Ready and it can be tasked again.
        Assert.False(team.IsDisbanded);
        Assert.Equal(AiTeamState.Retreating, team.State);
    }

    [Fact]
    public void WipedTeam_EndsItsWave()
    {
        var fixture = NewFixture();
        SeedOneWaveWorld(fixture);

        fixture.Tick();
        Assert.Single(fixture.Coordinator.Waves);

        fixture.World.Own.Clear();
        fixture.TickThrough(1);

        Assert.Empty(fixture.Coordinator.Waves);
        Assert.Equal(1, fixture.Count(AiAttackCoordinator.WaveEndedCounter));
        Assert.Equal(1, fixture.Coordinator.WavesEnded);
    }

    // ---- retreat -> muster -> relaunch / disband ----------------------------------------------

    [Fact]
    public void MauledWave_PullsBackToTheMusterPoint()
    {
        var fixture = NewFixture(teamSize: 4);
        fixture.World.Own.Add(Structure(9, PlayerIndex, x: 10f, z: 20f));

        for (uint i = 1; i <= 4; i++)
        {
            fixture.World.Own.Add(Unit(i + 100, PlayerIndex, x: 500f));
        }

        fixture.World.Enemy.Add(Structure(200, EnemyIndex, x: 2000f));

        fixture.Tick();
        var wave = Assert.Single(fixture.Coordinator.Waves);
        Assert.Equal(4, wave.PeakSize);

        // Three of the four die: below RetreatAtPercentOfPeak.
        fixture.World.Own.RemoveAll(o => o.Id.Index is 102 or 103 or 104);
        fixture.TickThrough(1);

        Assert.Equal(AiWaveState.Mustering, wave.State);
        Assert.Equal(1, fixture.Count(AiAttackCoordinator.MusterCounter));
        Assert.Equal(AiTeamState.Retreating, wave.Team.State);

        // The pull-back order is a MoveTo onto the base centre - the AI's one finished structure.
        var move = fixture.Sink.Orders[^1];
        Assert.Equal(OrderType.MoveTo, move.OrderType);
        Assert.Equal(new Vector3(10f, 0f, 20f), move.Arguments[0].Value.Position);
    }

    [Fact]
    public void MusteredWave_WithEnoughSurvivors_IsSentBackIn()
    {
        var fixture = NewFixture(teamSize: 4, regroupFrames: 3);
        fixture.World.Own.Add(Structure(9, PlayerIndex));

        for (uint i = 1; i <= 4; i++)
        {
            fixture.World.Own.Add(Unit(i + 100, PlayerIndex, x: 500f));
        }

        fixture.World.Enemy.Add(Structure(200, EnemyIndex, x: 2000f));

        fixture.Tick();
        var wave = Assert.Single(fixture.Coordinator.Waves);

        fixture.World.Own.RemoveAll(o => o.Id.Index is 102 or 103 or 104);
        fixture.TickThrough(1);
        Assert.Equal(AiWaveState.Mustering, wave.State);

        // One survivor is still >= MinimumRelaunchSize for a team of four, so the wave regroups
        // and goes back in rather than dissolving. This is the arc RelaunchAtPercentOfTargetSize
        // being strictly below RetreatAtPercentOfPeak exists to keep reachable.
        Assert.Equal(1, AiAttackCoordinator.MinimumRelaunchSize(wave.Team));

        fixture.TickThrough(10);

        Assert.Equal(AiWaveState.Engaging, wave.State);
        Assert.Equal(1, fixture.Count(AiAttackCoordinator.RelaunchCounter));
        Assert.Equal(AiTeamState.Tasked, wave.Team.State);
        Assert.Equal(new ObjectId(200), wave.TargetId);
    }

    [Fact]
    public void MusteredWave_TooSmallToFight_IsDissolvedAndItsSurvivorsReRecruited()
    {
        var fixture = NewFixture(teamSize: 6, regroupFrames: 3);
        fixture.World.Own.Add(Structure(9, PlayerIndex));

        for (uint i = 1; i <= 6; i++)
        {
            fixture.World.Own.Add(Unit(i + 100, PlayerIndex, x: 500f));
        }

        fixture.World.Enemy.Add(Structure(200, EnemyIndex, x: 2000f));

        fixture.Tick();
        var wave = Assert.Single(fixture.Coordinator.Waves);
        var firstTeamId = wave.Team.Id;

        fixture.World.Own.RemoveAll(o => o.Id.Index is 102 or 103 or 104 or 105 or 106);
        fixture.TickThrough(1);
        Assert.Equal(AiWaveState.Mustering, wave.State);
        Assert.Equal(2, AiAttackCoordinator.MinimumRelaunchSize(wave.Team));

        fixture.TickThrough(10);

        Assert.Empty(fixture.Coordinator.Waves);
        Assert.Equal(1, fixture.Count(AiAttackCoordinator.WaveDisbandedCounter));

        // Re-recruit: the survivor is back in the pool and AiTeamManager has folded it into a
        // fresh Building team, which is how a mauled army becomes a whole one again.
        var team = Assert.Single(fixture.Teams.Teams);
        Assert.NotEqual(firstTeamId, team.Id);
        Assert.Equal(AiTeamState.Building, team.State);
        Assert.Single(team.Members);
    }

    // ---- scheduling ------------------------------------------------------------------------

    [Fact]
    public void ConcurrentWaveCap_IsRespected()
    {
        var fixture = NewFixture(teamSize: 2, waveInterval: 1, maxWaves: 1);

        for (uint i = 1; i <= 4; i++)
        {
            fixture.World.Own.Add(Unit(i));
        }

        fixture.World.Enemy.Add(Structure(100, EnemyIndex, x: 500f));

        fixture.TickThrough(20);

        Assert.Equal(2, fixture.Teams.Teams.Count);
        Assert.Equal(1, fixture.Coordinator.WavesLaunched);
        Assert.Single(fixture.Coordinator.Waves);
    }

    [Fact]
    public void RaisingTheCap_LetsASecondTeamLaunchOnTheNextCadenceTick()
    {
        var fixture = NewFixture(teamSize: 2, waveInterval: 1, maxWaves: 2);

        for (uint i = 1; i <= 4; i++)
        {
            fixture.World.Own.Add(Unit(i));
        }

        fixture.World.Enemy.Add(Structure(100, EnemyIndex, x: 500f));

        fixture.TickThrough(5);

        Assert.Equal(2, fixture.Coordinator.WavesLaunched);
        Assert.Equal(2, fixture.Coordinator.Waves.Count);

        // One wave per team, always: two waves must not be holding the same team.
        Assert.NotSame(fixture.Coordinator.Waves[0].Team, fixture.Coordinator.Waves[1].Team);
    }

    [Fact]
    public void WaveCadence_IsEnforcedBetweenLaunches()
    {
        var fixture = NewFixture(teamSize: 2, waveInterval: 50, maxWaves: 4);

        for (uint i = 1; i <= 4; i++)
        {
            fixture.World.Own.Add(Unit(i));
        }

        fixture.World.Enemy.Add(Structure(100, EnemyIndex, x: 500f));

        fixture.Tick();
        Assert.Equal(1, fixture.Coordinator.WavesLaunched);
        Assert.Equal(50u, fixture.Coordinator.NextWaveFrame);

        fixture.TickThrough(49);
        Assert.Equal(1, fixture.Coordinator.WavesLaunched);

        fixture.TickThrough(50);
        Assert.Equal(2, fixture.Coordinator.WavesLaunched);
    }

    [Fact]
    public void DisableTacticalAi_StopsTheLaneDead()
    {
        var fixture = NewFixture();
        fixture.World.SkirmishAIData = MakeSkirmishAiData(disableTacticalAi: true);
        SeedOneWaveWorld(fixture);

        fixture.TickThrough(50);

        Assert.Equal(0, fixture.Coordinator.WavesLaunched);
        Assert.Empty(fixture.Coordinator.Waves);
        Assert.Empty(fixture.Sink.Orders);
    }

    // ---- determinism and tuning ---------------------------------------------------------------

    [Fact]
    public void TargetChoice_DoesNotDependOnSnapshotOrder()
    {
        var enemies = new List<AiObjectView>
        {
            Structure(300, EnemyIndex, x: 100f),
            Unit(301, EnemyIndex, x: 2000f),
            Structure(302, EnemyIndex, x: 50f),
            Unit(303, EnemyIndex, x: 1500f),
        };

        var forwards = NewFixture();
        var backwards = NewFixture();

        foreach (var fixture in new[] { forwards, backwards })
        {
            fixture.World.Own.Add(Unit(1));
            fixture.World.Own.Add(Unit(2));
        }

        forwards.World.Enemy.AddRange(enemies);
        enemies.Reverse();
        backwards.World.Enemy.AddRange(enemies);

        forwards.Tick();
        backwards.Tick();

        Assert.Equal(
            forwards.Coordinator.Waves[0].TargetId,
            backwards.Coordinator.Waves[0].TargetId);

        Assert.Equal(new ObjectId(303), forwards.Coordinator.Waves[0].TargetId);
    }

    [Fact]
    public void DifficultyScaling_IsMonotonic()
    {
        // Harder AIs attack sooner, re-scan sooner and run more waves at once. The exact numbers
        // are v1 heuristics (TODO S9-11); the ORDERING is the contract.
        Assert.True(AiAttackCoordinator.WaveIntervalFrames(Difficulty.Easy)
            > AiAttackCoordinator.WaveIntervalFrames(Difficulty.Normal));
        Assert.True(AiAttackCoordinator.WaveIntervalFrames(Difficulty.Normal)
            > AiAttackCoordinator.WaveIntervalFrames(Difficulty.Hard));
        Assert.True(AiAttackCoordinator.WaveIntervalFrames(Difficulty.Hard)
            > AiAttackCoordinator.WaveIntervalFrames(Difficulty.Brutal));

        Assert.True(AiAttackCoordinator.ReissueIntervalFrames(Difficulty.Easy)
            > AiAttackCoordinator.ReissueIntervalFrames(Difficulty.Brutal));

        Assert.True(AiAttackCoordinator.MaxConcurrentWaves(Difficulty.Brutal)
            > AiAttackCoordinator.MaxConcurrentWaves(Difficulty.Easy));
        Assert.True(AiAttackCoordinator.MaxConcurrentWaves(Difficulty.Easy) >= 1);
    }

    [Fact]
    public void DifficultyDrivesTheCadenceWhenNoOverrideIsGiven()
    {
        var fixture = NewFixture();
        fixture.World.Difficulty = Difficulty.Brutal;
        SeedOneWaveWorld(fixture);

        fixture.Tick();

        Assert.Equal(AiAttackCoordinator.WaveIntervalFrames(Difficulty.Brutal), fixture.Coordinator.NextWaveFrame);
    }

    [Fact]
    public void RelaunchFloor_StaysStrictlyBelowTheRetreatThreshold()
    {
        // If these two ever meet, every mustered wave dissolves and the relaunch arc becomes
        // unreachable code that still looks implemented. See the constant's remarks.
        Assert.True(AiAttackCoordinator.RelaunchAtPercentOfTargetSize
            < AiAttackCoordinator.RetreatAtPercentOfPeak);
    }

    [Fact]
    public void MinimumRelaunchSize_IsNeverZero()
    {
        Assert.Equal(1, AiAttackCoordinator.MinimumRelaunchSize(new AiTeam(1, 1, 0)));
        Assert.Equal(1, AiAttackCoordinator.MinimumRelaunchSize(new AiTeam(2, 2, 0)));
    }

    [Fact]
    public void ShouldRetreat_UsesIntegerCrossMultiplicationOfPeak()
    {
        var fixture = NewFixture(teamSize: 4);
        fixture.World.Own.Add(Structure(9, PlayerIndex));

        for (uint i = 1; i <= 4; i++)
        {
            fixture.World.Own.Add(Unit(i + 100, PlayerIndex));
        }

        fixture.World.Enemy.Add(Structure(200, EnemyIndex, x: 900f));
        fixture.Tick();

        var wave = Assert.Single(fixture.Coordinator.Waves);

        // Peak 4, still 4 members: not mauled. Half of peak is the boundary and is NOT below it.
        Assert.False(AiAttackCoordinator.ShouldRetreat(wave));

        wave.Team.RemoveMember(new ObjectId(104));
        wave.Team.RemoveMember(new ObjectId(103));
        Assert.False(AiAttackCoordinator.ShouldRetreat(wave));

        wave.Team.RemoveMember(new ObjectId(102));
        Assert.True(AiAttackCoordinator.ShouldRetreat(wave));
    }

    [Fact]
    public void NullArguments_AreRejectedAtConstruction()
    {
        var fixture = NewFixture();

        Assert.Throws<ArgumentNullException>(
            () => new AiAttackCoordinator(null!, fixture.Teams));

        Assert.Throws<ArgumentNullException>(
            () => new AiAttackCoordinator(new AiOrderEmitter(fixture.Brain), null!));
    }
}
