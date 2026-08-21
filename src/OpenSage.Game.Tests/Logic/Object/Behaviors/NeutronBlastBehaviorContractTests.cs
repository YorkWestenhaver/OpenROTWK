// Mocked-game unit tests for the NeutronBlastBehavior port (api-freeze-v1 §6 fitness item 4):
// one test per behavior branch from the R13 module spec, [create -> Kill() -> observe], plus
// the shadow-copy base test and a mid-behavior save/load round-trip.
//
// Sleepy-update caveat (from the spec): a freshly spawned module's NextCallFrame is floored to
// now at creation, and Update() only fires once CurrentFrame >= NextCallFrame - the tick that
// observes CurrentFrame == N runs on the (N+1)th Step() call, not the Nth. This module's own
// Update() never does anything observable (SetWakeFrame(Forever) + Update() always returns
// Forever), so the caveat only matters for the negative pre-death assertion below.
// IDieModule.OnDie fires synchronously inside GameObject.Kill() -> GameObject.OnDie ->
// FindBehaviors<IDieModule>() dispatch, not on any subsequent Step(), so blast-observable
// assertions after a Kill() call need no extra step at all.
//
// Relationship setup mirrors LeafletDropBehaviorContractTests/SabotageSupplyCenterCrateCollide
// ContractTests: PlayerManager.OnNewGame does not establish player-to-player relationships, so
// every scenario here registers extra players via OnNewGame, points SetRelationship the right
// direction (candidate owner -> blast owner - GameObject.GetRelationship resolves through the
// CANDIDATE's own Team/Player), and gives every object a real (singleton) Team.

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

public class NeutronBlastBehaviorContractTests
{
    private static readonly Vector3 Origin = new(0, 0, 0);

    private const string Definitions = @"
GameData
  Gravity = -1.0
End

Object NeutronCore
  KindOf = NO_COLLIDE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = NeutronBlastBehavior ModuleTag_Blast
    BlastRadius     = 50
    AffectAirborne  = No
    AffectAllies    = No
  End
End

Object NeutronCoreAffectsAll
  KindOf = NO_COLLIDE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = NeutronBlastBehavior ModuleTag_Blast
    BlastRadius     = 50
    AffectAirborne  = Yes
    AffectAllies    = Yes
  End
End

Object TestInfantry
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
End

Object TestVehicle
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
End

Object TestCliffJumperVehicle
  KindOf = VEHICLE CLIFF_JUMPER
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
End

Object TestDrone
  KindOf = VEHICLE DRONE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 80
  End
End

Object TestAircraftVehicle
  KindOf = VEHICLE AIRCRAFT
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 150
  End
End
";

    private sealed record Scenario(HeadlessSimGame Game, Player BlastOwner, Player EnemyOwner, Player AlliedOwner);

    private static Scenario NewScenario(uint seed = 0xB57)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);

        var mapBlastPlayer = new OpenSage.Data.Map.Player { Name = "BlastPlayer", Faction = "FactionOne", DisplayName = "BlastPlayer" };
        var mapEnemyPlayer = new OpenSage.Data.Map.Player { Name = "EnemyPlayer", Faction = "FactionTwo", DisplayName = "EnemyPlayer" };
        var mapAlliedPlayer = new OpenSage.Data.Map.Player { Name = "AlliedPlayer", Faction = "FactionThree", DisplayName = "AlliedPlayer" };

        game.PlayerManager.OnNewGame(
            [
                OpenSage.Data.Map.Player.CreateNeutralPlayer(),
                OpenSage.Data.Map.Player.CreateCivilianPlayer(),
                mapBlastPlayer,
                mapEnemyPlayer,
                mapAlliedPlayer,
            ],
            GameType.Skirmish);

        var blastOwner = game.PlayerManager.GetPlayerByIndex(2);
        var enemyOwner = game.PlayerManager.GetPlayerByIndex(3);
        var alliedOwner = game.PlayerManager.GetPlayerByIndex(4);

        // The module asks self.GetRelationship(candidate) (NeutronBlastBehavior.cs:103), which
        // resolves self.Team -> candidate.Team -> blastOwner.GetRelationship(candidateOwner)
        // (GameObject.cs:1856, Team.cs:47) - i.e. from the BLAST CORE's player outward. Both
        // directions are set so the fixture reads the same whichever end asks.
        blastOwner.SetRelationship(enemyOwner, RelationshipType.Enemies);
        blastOwner.SetRelationship(alliedOwner, RelationshipType.Allies);
        enemyOwner.SetRelationship(blastOwner, RelationshipType.Enemies);
        alliedOwner.SetRelationship(blastOwner, RelationshipType.Allies);

        return new Scenario(game, blastOwner, enemyOwner, alliedOwner);
    }

    private static GameObject Spawn(Scenario scenario, string definitionName, Player owner, in Vector3 position, uint teamId)
    {
        var obj = scenario.Game.SpawnObject(definitionName, owner, position);
        obj.Team = new Team(new TeamTemplate(new TeamFactory(scenario.Game), teamId, $"Team{teamId}", owner, isSingleton: true), teamId);
        return obj;
    }

    private static NeutronBlastBehavior ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<NeutronBlastBehavior>().Single();

    // ---- 1: no blast before death ----

    [Fact]
    public void NoBlast_BeforeDeath()
    {
        var scenario = NewScenario();
        var game = scenario.Game;
        Spawn(scenario, "NeutronCore", scenario.BlastOwner, Origin, teamId: 101);
        var infantry = Spawn(scenario, "TestInfantry", scenario.EnemyOwner, new Vector3(30, 0, 0), teamId: 102);

        for (var i = 0; i < 10; i++)
        {
            game.Step();
            Assert.False(infantry.IsDestroyed);
        }
    }

    // ---- 2/3: radius bound ----

    [Fact]
    public void Infantry_Killed_OnDeath_WithinRadius()
    {
        var scenario = NewScenario();
        var neutronCore = Spawn(scenario, "NeutronCore", scenario.BlastOwner, Origin, teamId: 101); // BlastRadius = 50
        var infantry = Spawn(scenario, "TestInfantry", scenario.EnemyOwner, new Vector3(30, 0, 0), teamId: 102);

        neutronCore.Kill();

        Assert.True(infantry.IsDestroyed);
    }

    [Fact]
    public void Infantry_Unaffected_OutsideRadius()
    {
        var scenario = NewScenario();
        var neutronCore = Spawn(scenario, "NeutronCore", scenario.BlastOwner, Origin, teamId: 101); // BlastRadius = 50
        var infantry = Spawn(scenario, "TestInfantry", scenario.EnemyOwner, new Vector3(80, 0, 0), teamId: 102);

        neutronCore.Kill();

        Assert.False(infantry.IsDestroyed);
    }

    // ---- 4/5: AffectAllies gate ----

    [Fact]
    public void AffectAllies_False_AlliedInfantry_Spared_EnemyInfantry_Killed()
    {
        var scenario = NewScenario();
        var neutronCore = Spawn(scenario, "NeutronCore", scenario.BlastOwner, Origin, teamId: 101); // AffectAllies = No
        var alliedInfantry = Spawn(scenario, "TestInfantry", scenario.AlliedOwner, new Vector3(30, 0, 0), teamId: 102);
        var enemyInfantry = Spawn(scenario, "TestInfantry", scenario.EnemyOwner, new Vector3(-30, 0, 0), teamId: 103);

        neutronCore.Kill();

        Assert.False(alliedInfantry.IsDestroyed, "allied units are spared when AffectAllies = No");
        Assert.True(enemyInfantry.IsDestroyed);
    }

    [Fact]
    public void AffectAllies_True_AlliedInfantry_AlsoKilled()
    {
        var scenario = NewScenario();
        var neutronCore = Spawn(scenario, "NeutronCoreAffectsAll", scenario.BlastOwner, Origin, teamId: 101); // AffectAllies = Yes
        var alliedInfantry = Spawn(scenario, "TestInfantry", scenario.AlliedOwner, new Vector3(30, 0, 0), teamId: 102);

        neutronCore.Kill();

        Assert.True(alliedInfantry.IsDestroyed);
    }

    // ---- 6/7: AffectAirborne gate (kind half, isolated from height) ----

    [Fact]
    public void AffectAirborne_False_GroundedAircraftVehicle_Excluded()
    {
        var scenario = NewScenario();
        var neutronCore = Spawn(scenario, "NeutronCore", scenario.BlastOwner, Origin, teamId: 101); // AffectAirborne = No
        var aircraftVehicle = Spawn(scenario, "TestAircraftVehicle", scenario.EnemyOwner, new Vector3(30, 0, 0), teamId: 102);

        neutronCore.Kill();

        // Excluded purely by KindOf(Aircraft), regardless of height - the hitAir pre-filter's
        // IsKindOf term fires before IsSignificantlyAboveTerrain is even consulted.
        Assert.False(aircraftVehicle.IsDisabledByType(DisabledType.Unmanned));
        Assert.False(aircraftVehicle.IsDestroyed);
    }

    [Fact]
    public void AffectAirborne_True_AircraftVehicle_Included()
    {
        var scenario = NewScenario();
        var neutronCore = Spawn(scenario, "NeutronCoreAffectsAll", scenario.BlastOwner, Origin, teamId: 101); // AffectAirborne = Yes
        var aircraftVehicle = Spawn(scenario, "TestAircraftVehicle", scenario.EnemyOwner, new Vector3(30, 0, 0), teamId: 102);

        neutronCore.Kill();

        Assert.True(aircraftVehicle.IsDisabledByType(DisabledType.Unmanned));
    }

    // ---- 8/9/10: vehicle branch shapes ----

    [Fact]
    public void Vehicle_NonCliffJumper_NonDrone_BecomesUnmannedAndNeutral_NotKilled()
    {
        var scenario = NewScenario();
        var neutronCore = Spawn(scenario, "NeutronCore", scenario.BlastOwner, Origin, teamId: 101);
        var vehicle = Spawn(scenario, "TestVehicle", scenario.EnemyOwner, new Vector3(30, 0, 0), teamId: 102);

        neutronCore.Kill();

        Assert.False(vehicle.IsDestroyed);
        Assert.True(vehicle.IsDisabledByType(DisabledType.Unmanned));
        Assert.Equal(scenario.Game.PlayerManager.NeutralPlayer.DefaultTeam, vehicle.Team);
    }

    [Fact]
    public void Vehicle_CliffJumper_KilledOutright_NotDisabled()
    {
        var scenario = NewScenario();
        var neutronCore = Spawn(scenario, "NeutronCore", scenario.BlastOwner, Origin, teamId: 101);
        var cliffJumper = Spawn(scenario, "TestCliffJumperVehicle", scenario.EnemyOwner, new Vector3(30, 0, 0), teamId: 102);

        neutronCore.Kill();

        Assert.True(cliffJumper.IsDestroyed);
        Assert.False(cliffJumper.IsDisabledByType(DisabledType.Unmanned));
    }

    [Fact]
    public void Vehicle_Drone_Unaffected_ByVehicleBranch()
    {
        var scenario = NewScenario();
        var neutronCore = Spawn(scenario, "NeutronCore", scenario.BlastOwner, Origin, teamId: 101);
        var drone = Spawn(scenario, "TestDrone", scenario.EnemyOwner, new Vector3(30, 0, 0), teamId: 102);

        neutronCore.Kill();

        Assert.False(drone.IsDestroyed);
        Assert.False(drone.IsDisabledByType(DisabledType.Unmanned));
    }

    // ---- 11: self-exclusion ----

    [Fact]
    public void Self_NeverAffectedByOwnBlast()
    {
        var scenario = NewScenario();
        var neutronCore = Spawn(scenario, "NeutronCore", scenario.BlastOwner, Origin, teamId: 101);

        var exception = Record.Exception(() => neutronCore.Kill());

        Assert.Null(exception);

        // The core dies from the Kill() itself, but its own blast must not act on it. It stays
        // un-Destroy()ed because GameObject.OnDie (GameObject.cs:1609) only auto-Destroy()s
        // objects with NO die modules, and this object has one - the blast itself. So
        // self-exclusion reads as "effectively dead, but never destroyed by its own blast".
        Assert.True(neutronCore.IsEffectivelyDead);
        Assert.False(neutronCore.IsDestroyed);
    }

    // ---- 13/14: shadow-copy + save/load round-trip ----

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_PreDeath()
    {
        var scenario = NewScenario();
        var game = scenario.Game;
        var neutronCore = Spawn(scenario, "NeutronCore", scenario.BlastOwner, Origin, teamId: 101);
        var live = ModuleOf(neutronCore);

        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }

        var shadowHost = Spawn(scenario, "NeutronCore", scenario.BlastOwner, new Vector3(300, 0, 0), teamId: 199);
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void SaveLoad_RoundTrip_AroundDeath_BlastStillFiresIdentically()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 3);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static bool[] RunScenario(int roundTripAtFrame)
    {
        var scenario = NewScenario(seed: 0xF00D);
        var game = scenario.Game;
        var neutronCore = Spawn(scenario, "NeutronCore", scenario.BlastOwner, Origin, teamId: 101);
        var infantry = Spawn(scenario, "TestInfantry", scenario.EnemyOwner, new Vector3(30, 0, 0), teamId: 102);
        var module = ModuleOf(neutronCore);

        var trajectory = new bool[10];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk;
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            if (i == 5)
            {
                neutronCore.Kill();
            }

            game.Step();
            trajectory[i] = infantry.IsDestroyed;
        }

        return trajectory;
    }
}
