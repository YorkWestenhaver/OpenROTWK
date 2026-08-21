// Mocked-game unit tests for the LeafletDropBehavior port (api-freeze-v1 §6 fitness item 4):
// one test per behavior branch from the R12 task packet, [create -> tick -> observable
// effect], plus the shadow-copy base test and a mid-behavior save/load round-trip.
//
// The observables are: the DisabledType.Emp flag on candidates within AffectRadius, and the
// recorded FireParticleSystemAtObject events (ISimEvents, F-LDB-2).
//
// Frame accounting (shared with EmpUpdateContractTests, api-freeze-v1's sleepy-update
// convention): a [SimState] UpdateModule wakes with UpdateSleepTime.None (a 1-frame delay)
// from whatever CurrentFrame it was constructed at (0, for every object here - none of these
// scenarios Step() before spawning). GameLogic.Update() reads CurrentFrame BEFORE
// incrementing it, so the tick that observes CurrentFrame == F runs on the (F+1)-th Step()
// call. Concretely: the very first Update() tick any of these modules ever gets sees
// CurrentFrame == 1, which is the 2nd Step() call - that is "first update()" for the
// particle-system-lifecycle testcase (6).
//
// Relationship setup mirrors SabotageSupplyCenterCrateCollideContractTests: PlayerManager.
// OnNewGame does not establish player-to-player relationships (its own "TODO: Setup player
// relationships" note), and GameObject.GetRelationship resolves through the CANDIDATE's own
// Team/Player - so every scenario here registers extra players via OnNewGame, points the
// override the right direction (candidate owner -> dropper owner), and gives every object a
// real (singleton) Team, matching EmpUpdate's AirborneAlliedTransport_IsSpared note that a
// null Team always resolves to Neutral regardless of any SetRelationship call.

using System.Linq;
using System.Numerics;
using OpenSage.Data.Map;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;
using Player = OpenSage.Logic.Player;
using Team = OpenSage.Logic.Team;

namespace OpenSage.Tests.Logic.Object.Behaviors;

public class LeafletDropBehaviorContractTests
{
    private static readonly Vector3 Origin = new(0, 0, 0);

    private const string Definitions = @"
GameData
  Gravity = -1.0
End

FXParticleSystem PS_Leaflet
End

Object LeafletDropper
  KindOf = IMMOBILE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = LeafletDropBehavior ModuleTag_Leaflet
    DisabledDuration = 20
    Delay = 6
    AffectRadius = 100
    LeafletFXParticleSystem = PS_Leaflet
  End
End

Object LeafletDropperInstant
  KindOf = IMMOBILE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = LeafletDropBehavior ModuleTag_Leaflet
    DisabledDuration = 20
    Delay = 0
    AffectRadius = 100
    LeafletFXParticleSystem = PS_Leaflet
  End
End

Object LeafletDropperSlow
  KindOf = IMMOBILE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = LeafletDropBehavior ModuleTag_Leaflet
    DisabledDuration = 20
    Delay = 1000
    AffectRadius = 100
    LeafletFXParticleSystem = PS_Leaflet
  End
End

Object LeafletTestTank
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object LeafletTestGrunt
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object LeafletTestChopper
  KindOf = AIRCRAFT
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object LeafletTestTurret
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private sealed record Scenario(HeadlessSimGame Game, Player DropperOwner, Player EnemyOwner, Player AlliedOwner);

    /// <summary>
    /// Three real, registered players: the dropper's owner, an enemy owner (Enemies toward
    /// the dropper), and an allied owner (Allies toward the dropper) - see the class doc
    /// comment on why relationships and teams are wired explicitly rather than relying on
    /// defaults.
    /// </summary>
    private static Scenario NewScenario(uint seed = 0x1EAF)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);

        var mapDropperPlayer = new OpenSage.Data.Map.Player { Name = "DropperPlayer", Faction = "FactionOne", DisplayName = "DropperPlayer" };
        var mapEnemyPlayer = new OpenSage.Data.Map.Player { Name = "EnemyPlayer", Faction = "FactionTwo", DisplayName = "EnemyPlayer" };
        var mapAlliedPlayer = new OpenSage.Data.Map.Player { Name = "AlliedPlayer", Faction = "FactionThree", DisplayName = "AlliedPlayer" };

        game.PlayerManager.OnNewGame(
            [
                OpenSage.Data.Map.Player.CreateNeutralPlayer(),
                OpenSage.Data.Map.Player.CreateCivilianPlayer(),
                mapDropperPlayer,
                mapEnemyPlayer,
                mapAlliedPlayer,
            ],
            GameType.Skirmish);

        var dropperOwner = game.PlayerManager.GetPlayerByIndex(2);
        var enemyOwner = game.PlayerManager.GetPlayerByIndex(3);
        var alliedOwner = game.PlayerManager.GetPlayerByIndex(4);

        // GetRelationship resolves through the CANDIDATE's own player, pointed at the
        // dropper's player (same direction EmpUpdateContractTests uses).
        enemyOwner.SetRelationship(dropperOwner, RelationshipType.Enemies);
        alliedOwner.SetRelationship(dropperOwner, RelationshipType.Allies);

        return new Scenario(game, dropperOwner, enemyOwner, alliedOwner);
    }

    private static GameObject Spawn(Scenario scenario, string definitionName, Player owner, in Vector3 position, uint teamId)
    {
        var obj = scenario.Game.SpawnObject(definitionName, owner, position);
        obj.Team = new Team(new TeamTemplate(new TeamFactory(scenario.Game), teamId, $"Team{teamId}", owner, isSingleton: true), teamId);
        return obj;
    }

    private static LeafletDropUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<LeafletDropUpdate>().Single();

    // ---- testcase 1: Delay timing ----

    [Fact]
    public void DelayTiming_FxFiresOnFirstUpdate_DisableDeferredUntilDelayElapses()
    {
        var scenario = NewScenario();
        var game = scenario.Game;
        var dropper = Spawn(scenario, "LeafletDropper", scenario.DropperOwner, Origin, teamId: 101); // Delay = 6
        var enemyTank = Spawn(scenario, "LeafletTestTank", scenario.EnemyOwner, new Vector3(50, 0, 0), teamId: 102);
        var events = RecordingSimEvents.InstallOn(game);

        game.Step(); // call #1: NextCallFrame(1) > now(0) - module still asleep, nothing runs.
        Assert.Empty(events.ParticleSystems);
        Assert.False(enemyTank.IsDisabledByType(DisabledType.Emp));

        game.Step(); // call #2: first real tick, CurrentFrame == 1 - FX fires; 1 < 6, no disable yet.
        Assert.Single(events.ParticleSystems);
        Assert.False(enemyTank.IsDisabledByType(DisabledType.Emp), "must not fire before Delay elapses");

        for (var i = 0; i < 4; i++) // calls #3-#6: ticks see CurrentFrame 2..5, still < 6.
        {
            game.Step();
        }
        Assert.False(enemyTank.IsDisabledByType(DisabledType.Emp));
        Assert.Single(events.ParticleSystems); // FX must fire exactly once, not every tick.

        game.Step(); // call #7: tick sees CurrentFrame == 6 == Delay - disable fires.
        Assert.True(enemyTank.IsDisabledByType(DisabledType.Emp), "must fire exactly at Delay");
    }

    // ---- testcase 2: Radius filtering ----

    [Fact]
    public void RadiusFiltering_OnlyDisablesEnemiesWithinAffectRadius()
    {
        var scenario = NewScenario();
        var game = scenario.Game;
        Spawn(scenario, "LeafletDropperInstant", scenario.DropperOwner, Origin, teamId: 101); // AffectRadius = 100

        var enemyInner = Spawn(scenario, "LeafletTestTank", scenario.EnemyOwner, new Vector3(50, 0, 0), teamId: 102);
        var enemyNearEdge = Spawn(scenario, "LeafletTestTank", scenario.EnemyOwner, new Vector3(99, 0, 0), teamId: 103);
        var enemyOutside = Spawn(scenario, "LeafletTestTank", scenario.EnemyOwner, new Vector3(101, 0, 0), teamId: 104);
        var alliedInner = Spawn(scenario, "LeafletTestTank", scenario.AlliedOwner, new Vector3(50, 0, 0), teamId: 105);

        game.Step();
        game.Step(); // first real tick: Delay = 0, so the disable scan runs alongside the FX.

        Assert.True(enemyInner.IsDisabledByType(DisabledType.Emp));
        Assert.True(enemyNearEdge.IsDisabledByType(DisabledType.Emp));
        Assert.False(enemyOutside.IsDisabledByType(DisabledType.Emp), "outside AffectRadius");
        Assert.False(alliedInner.IsDisabledByType(DisabledType.Emp), "allied units are immune");
    }

    // ---- testcase 3: Kind filtering ----

    [Fact]
    public void KindFiltering_OnlyDisablesInfantryAndVehicleKinds()
    {
        var scenario = NewScenario();
        var game = scenario.Game;
        Spawn(scenario, "LeafletDropperInstant", scenario.DropperOwner, Origin, teamId: 101);

        var enemyTank = Spawn(scenario, "LeafletTestTank", scenario.EnemyOwner, new Vector3(30, 0, 0), teamId: 102);
        var enemyGrunt = Spawn(scenario, "LeafletTestGrunt", scenario.EnemyOwner, new Vector3(-30, 0, 0), teamId: 103);
        var enemyChopper = Spawn(scenario, "LeafletTestChopper", scenario.EnemyOwner, new Vector3(0, 30, 0), teamId: 104);
        var enemyTurret = Spawn(scenario, "LeafletTestTurret", scenario.EnemyOwner, new Vector3(0, -30, 0), teamId: 105);

        game.Step();
        game.Step();

        Assert.True(enemyTank.IsDisabledByType(DisabledType.Emp));
        Assert.True(enemyGrunt.IsDisabledByType(DisabledType.Emp));
        Assert.False(enemyChopper.IsDisabledByType(DisabledType.Emp), "aircraft is not INFANTRY/VEHICLE");
        Assert.False(enemyTurret.IsDisabledByType(DisabledType.Emp), "structure is not INFANTRY/VEHICLE");
    }

    // ---- testcase 4: Duration application ----

    [Fact]
    public void DurationApplication_StaysDisabledPastDisabledDuration_KnownEngineLimitation()
    {
        // F-LDB-3 (filed in LeafletDropBehavior.cs, same gap as EmpUpdate's F-EMP-6):
        // GameObject.CheckDisabledStates - the sweep that would auto-clear DisabledType.Emp
        // once its recorded expiry frame passes - is only ever called from GameObject.Update
        // (), which nothing in this engine snapshot's GameLogic sleepy-module loop invokes.
        // This test pins what this port actually controls today (disabled, and staying
        // disabled) rather than an auto-recovery this engine snapshot cannot yet deliver.
        var scenario = NewScenario();
        var game = scenario.Game;
        Spawn(scenario, "LeafletDropperInstant", scenario.DropperOwner, Origin, teamId: 101); // DisabledDuration = 20
        var enemyTank = Spawn(scenario, "LeafletTestTank", scenario.EnemyOwner, new Vector3(30, 0, 0), teamId: 102);

        game.Step();
        game.Step();
        Assert.True(enemyTank.IsDisabledByType(DisabledType.Emp));

        for (var i = 0; i < 30; i++) // well past DisabledDuration = 20
        {
            game.Step();
        }
        Assert.True(enemyTank.IsDisabledByType(DisabledType.Emp));
    }

    // ---- testcase 5: early death handler ----

    [Fact]
    public void OnDie_TriggersDisableAttackImmediately_BypassingDelay()
    {
        var scenario = NewScenario();
        var game = scenario.Game;
        var dropper = Spawn(scenario, "LeafletDropperSlow", scenario.DropperOwner, Origin, teamId: 101); // Delay = 1000
        var enemyTank = Spawn(scenario, "LeafletTestTank", scenario.EnemyOwner, new Vector3(30, 0, 0), teamId: 102);
        var events = RecordingSimEvents.InstallOn(game);

        // Killed before its first update() tick ever runs (no Step() has been called), so
        // this also exercises OnDie's FX fallback (the FX would otherwise never fire).
        PortedModuleTestKit.TriggerDeath(dropper);

        Assert.True(enemyTank.IsDisabledByType(DisabledType.Emp), "onDie must disable immediately, bypassing Delay");
        Assert.Single(events.ParticleSystems);
    }

    // ---- testcase 6: particle system lifecycle ----

    [Fact]
    public void LeafletFx_RequestedExactlyOnce_AttachedToSelf()
    {
        // F-LDB-2 (filed in LeafletDropBehavior.cs): the packet's "lifetime = DisabledDuration
        // - 30 frames; initial delay randomized 1-100 frames per emitter" is client-owned
        // ParticleSystemTemplate/ISimEvents territory with no Fix64-safe facade for a
        // [SimState] module to override per-instance - not modeled here, same class of gap as
        // EmpUpdate's F-EMP-1. What IS this module's own behavior, and what this test pins:
        // exactly one FireParticleSystemAtObject request, naming the configured template,
        // attached to the dropper itself (no bone, no random-bone pick).
        var scenario = NewScenario();
        var game = scenario.Game;
        var dropper = Spawn(scenario, "LeafletDropper", scenario.DropperOwner, Origin, teamId: 101);
        var events = RecordingSimEvents.InstallOn(game);

        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }

        var fx = Assert.Single(events.ParticleSystems);
        Assert.Equal("PS_Leaflet", fx.ParticleSystemName);
        Assert.Equal(dropper.Id, fx.ObjectId);
        Assert.Equal(string.Empty, fx.Bone);
        Assert.False(fx.RandomBone);
    }

    // ---- shadow-copy + save/load round-trip ----

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var scenario = NewScenario();
        var game = scenario.Game;
        var dropper = Spawn(scenario, "LeafletDropper", scenario.DropperOwner, Origin, teamId: 101);
        var live = ModuleOf(dropper);

        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }

        var shadowHost = Spawn(scenario, "LeafletDropper", scenario.DropperOwner, new Vector3(300, 0, 0), teamId: 199);
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 4);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static bool[] RunScenario(int roundTripAtFrame)
    {
        var scenario = NewScenario(seed: 0xF00D);
        var game = scenario.Game;
        var dropper = Spawn(scenario, "LeafletDropper", scenario.DropperOwner, Origin, teamId: 101);
        var enemyTank = Spawn(scenario, "LeafletTestTank", scenario.EnemyOwner, new Vector3(30, 0, 0), teamId: 102);
        var module = ModuleOf(dropper);

        var trajectory = new bool[9];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk;
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            game.Step();
            trajectory[i] = enemyTank.IsDisabledByType(DisabledType.Emp);
        }

        return trajectory;
    }
}
