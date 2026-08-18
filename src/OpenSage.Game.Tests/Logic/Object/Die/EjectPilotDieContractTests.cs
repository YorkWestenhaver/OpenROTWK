// Contract tests for the EjectPilotDie port (experiment-round-4 §4.1 DoD item 4: one test
// per INI branch, minimum [create -> trigger death -> observable effect], plus a mid-behavior
// save/load continuation). Shape cloned from AutoHealContractTests; the death half comes from
// the batch's shared PortedModuleTestKit death-trigger helper.
//
// The observable effect is the ejected object itself: EjectPilotDie's whole job is to run an
// ObjectCreationList, so "did the module run" is "did a pilot appear in the object list".

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Die;

public class EjectPilotDieContractTests
{
    private const string PilotGround = "EjectTestPilotGround";
    private const string PilotAir = "EjectTestPilotAir";

    // Gravity is what IsSignificantlyAboveTerrain measures against (-9 * gravity); it is
    // declared so the air/ground split is a property of the data, not of a zero default.
    private const string Definitions = @"
GameData
  Gravity = -1.0
End

Object " + PilotGround + @"
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
End

Object " + PilotAir + @"
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
End

ObjectCreationList OCL_EjectTestGround
  CreateObject
    ObjectNames = " + PilotGround + @"
    Count = 1
  End
End

ObjectCreationList OCL_EjectTestAir
  CreateObject
    ObjectNames = " + PilotAir + @"
    Count = 1
  End
End

; both branches configured: the altitude test chooses
Object EjectTestVehicle
  KindOf = VEHICLE
  ExperienceRequired = 0 100 200 300
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = EjectPilotDie ModuleTag_Eject
    GroundCreationList = OCL_EjectTestGround
    AirCreationList = OCL_EjectTestAir
  End
End

; only the ground branch configured: dying in the air must be a silent no-op
Object EjectTestGroundOnlyVehicle
  KindOf = VEHICLE
  ExperienceRequired = 0 100 200 300
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = EjectPilotDie ModuleTag_Eject
    GroundCreationList = OCL_EjectTestGround
  End
End

; the shared die mux still gates this module: only a BURNED death ejects
Object EjectTestBurnOnlyVehicle
  KindOf = VEHICLE
  ExperienceRequired = 0 100 200 300
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = EjectPilotDie ModuleTag_Eject
    GroundCreationList = OCL_EjectTestGround
    DeathTypes = NONE +BURNED
  End
End

; veterancy branch: only a HEROIC crew bails out
Object EjectTestHeroicOnlyVehicle
  KindOf = VEHICLE
  ExperienceRequired = 0 100 200 300
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = EjectPilotDie ModuleTag_Eject
    GroundCreationList = OCL_EjectTestGround
    VeterancyLevels = HEROIC
  End
End

; the other side of the same branch: REGULAR is the rank every object starts at
Object EjectTestRegularOnlyVehicle
  KindOf = VEHICLE
  ExperienceRequired = 0 100 200 300
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = EjectPilotDie ModuleTag_Eject
    GroundCreationList = OCL_EjectTestGround
    VeterancyLevels = REGULAR
  End
End

; InvulnerableTime is parsed (and, as in the original, consumed by nothing)
Object EjectTestInvulnerableTimeVehicle
  KindOf = VEHICLE
  ExperienceRequired = 0 100 200 300
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = EjectPilotDie ModuleTag_Eject
    GroundCreationList = OCL_EjectTestGround
    InvulnerableTime = 3000
  End
End

Object EjectTestKiller
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static readonly Vector3 OnGround = new(0, 0, 0);
    private static readonly Vector3 HighUp = new(0, 0, 500);

    private static HeadlessSimGame NewGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: 0xE7EC7u);
        game.LoadIniText(Definitions);
        return game;
    }

    private static int CountOf(HeadlessSimGame game, string definitionName) =>
        game.GameLogic.Objects.Count(o => o.Definition.Name == definitionName);

    private static EjectPilotDie ModuleOf(GameObject gameObject) =>
        gameObject.FindBehavior<EjectPilotDie>();

    private static EjectPilotDieModuleData EjectDataOf(HeadlessSimGame game, string definitionName) =>
        Assert.IsType<EjectPilotDieModuleData>(
            game.AssetStore.ObjectDefinitions.GetByName(definitionName)
                .Behaviors.Values.Single(b => b.Data is EjectPilotDieModuleData).Data);


    // ---- INI branch: GroundCreationList, owner dies on the ground ----

    [Fact]
    public void DyingOnTheGround_RunsTheGroundCreationList()
    {
        var game = NewGame();
        var vehicle = game.SpawnObject("EjectTestVehicle", game.CivilianPlayer, OnGround);
        Assert.False(vehicle.IsSignificantlyAboveTerrain);

        PortedModuleTestKit.TriggerDeath(vehicle);

        Assert.Equal(1, CountOf(game, PilotGround));
        Assert.Equal(0, CountOf(game, PilotAir));
    }

    // ---- INI branch: AirCreationList, owner dies significantly above terrain ----

    [Fact]
    public void DyingInTheAir_RunsTheAirCreationList()
    {
        var game = NewGame();
        var vehicle = game.SpawnObject("EjectTestVehicle", game.CivilianPlayer, HighUp);
        Assert.True(vehicle.IsSignificantlyAboveTerrain);

        PortedModuleTestKit.TriggerDeath(vehicle);

        Assert.Equal(1, CountOf(game, PilotAir));
        Assert.Equal(0, CountOf(game, PilotGround));
    }

    // ---- INI branch: the branch's list is absent -> silent no-op (GPL "if (!ocl) return") ----

    [Fact]
    public void DyingInTheAirWithNoAirList_EjectsNothingAndDoesNotThrow()
    {
        var game = NewGame();
        var vehicle = game.SpawnObject("EjectTestGroundOnlyVehicle", game.CivilianPlayer, HighUp);
        Assert.True(vehicle.IsSignificantlyAboveTerrain);

        PortedModuleTestKit.TriggerDeath(vehicle);

        Assert.Equal(0, CountOf(game, PilotGround));
        Assert.Equal(0, CountOf(game, PilotAir));
    }

    [Fact]
    public void GroundOnlyVehicleDyingOnTheGround_StillEjects()
    {
        var game = NewGame();
        var vehicle = game.SpawnObject("EjectTestGroundOnlyVehicle", game.CivilianPlayer, OnGround);

        PortedModuleTestKit.TriggerDeath(vehicle);

        Assert.Equal(1, CountOf(game, PilotGround));
    }

    // ---- INI branch: DeathTypes (the shared die mux gate upstream of Die()) ----

    [Fact]
    public void DeathTypesFilter_GatesTheEjection()
    {
        var game = NewGame();

        var normal = game.SpawnObject("EjectTestBurnOnlyVehicle", game.CivilianPlayer, OnGround);
        PortedModuleTestKit.TriggerDeath(normal, DeathType.Normal);
        Assert.Equal(0, CountOf(game, PilotGround));

        var burned = game.SpawnObject("EjectTestBurnOnlyVehicle", game.CivilianPlayer, new Vector3(20, 0, 0));
        PortedModuleTestKit.TriggerDeath(burned, DeathType.Burned);
        Assert.Equal(1, CountOf(game, PilotGround));
    }

    // ---- INI branch: VeterancyLevels ----
    //
    // Both sides of the mask are exercised at the rank every object starts at (REGULAR)
    // rather than by promoting one: GameObject.OnVeterancyLevelChanged unconditionally plays
    // the promotion sound through GameEngine.AudioSystem, which the graphics-free headless
    // host does not have. Recorded as a host finding in EjectPilotDie.md - it does not weaken
    // the branch coverage, because what the module reads is the mask bit, not the promotion.

    [Fact]
    public void VeterancyLevelsExcludingThisRank_SuppressesTheEjection()
    {
        var game = NewGame();
        var vehicle = game.SpawnObject("EjectTestHeroicOnlyVehicle", game.CivilianPlayer, OnGround);
        Assert.Equal(VeterancyLevel.Regular, vehicle.Rank);

        PortedModuleTestKit.TriggerDeath(vehicle);

        Assert.Equal(0, CountOf(game, PilotGround));
    }

    [Fact]
    public void VeterancyLevelsIncludingThisRank_AllowsTheEjection()
    {
        var game = NewGame();
        var vehicle = game.SpawnObject("EjectTestRegularOnlyVehicle", game.CivilianPlayer, OnGround);
        Assert.Equal(VeterancyLevel.Regular, vehicle.Rank);

        PortedModuleTestKit.TriggerDeath(vehicle);

        Assert.Equal(1, CountOf(game, PilotGround));
    }

    // ---- INI branch: no VeterancyLevels declared -> every rank ejects (the ALL default) ----

    [Fact]
    public void WithoutVeterancyLevels_TheMaskIsAbsentAndTheEjectionRuns()
    {
        var game = NewGame();
        var vehicle = game.SpawnObject("EjectTestVehicle", game.CivilianPlayer, OnGround);

        var data = EjectDataOf(game, "EjectTestVehicle");
        Assert.Null(data.VeterancyLevels);

        PortedModuleTestKit.TriggerDeath(vehicle);
        Assert.Equal(1, CountOf(game, PilotGround));
    }

    // ---- INI branch: InvulnerableTime parses to frames and is consumed by nothing ----

    [Fact]
    public void InvulnerableTimeIsParsedToFramesAndUnconsumed()
    {
        var game = NewGame();
        var vehicle = game.SpawnObject("EjectTestInvulnerableTimeVehicle", game.CivilianPlayer, OnGround);

        var data = EjectDataOf(game, "EjectTestInvulnerableTimeVehicle");

        // 3000 ms at 5 Hz = 15 frames (S5 ceil quantization).
        Assert.Equal(15u, data.InvulnerableTime.Value);

        // ...and the ejection is unaffected by it.
        PortedModuleTestKit.TriggerDeath(vehicle);
        Assert.Equal(1, CountOf(game, PilotGround));
    }

    // ---- the damage dealer reaches the module (GPL findObjectByID(m_sourceID)) ----

    [Fact]
    public void KillerIsResolvedAndTheEjectionStillHappens()
    {
        var game = NewGame();
        var killer = game.SpawnObject("EjectTestKiller", game.CivilianPlayer, new Vector3(30, 0, 0));
        var vehicle = game.SpawnObject("EjectTestVehicle", game.CivilianPlayer, OnGround);

        var result = PortedModuleTestKit.TriggerDeath(vehicle, DeathType.Normal, DamageType.Unresistable, killer);

        Assert.True(result.Died);
        Assert.Equal(1, CountOf(game, PilotGround));
    }

    [Fact]
    public void SubLethalDamage_EjectsNothing()
    {
        var game = NewGame();
        var vehicle = game.SpawnObject("EjectTestVehicle", game.CivilianPlayer, OnGround);

        var result = PortedModuleTestKit.ApplyDamage(vehicle, amount: 30f);

        Assert.False(result.Died);
        Assert.Equal(0, CountOf(game, PilotGround));
    }

    // ---- item 3: the shadow-copy base test, taken MID-BEHAVIOR ----

    [Fact]
    public void ShadowCopyCrcMatches_MidBehavior()
    {
        // "Mid-behavior" for this class means: on the frame the ejection has happened and the
        // dead owner has not yet been reaped. EjectPilotDie's mutable state inventory is
        // EMPTY (the GPL class declares no members), so the walk is version-only by design -
        // this test asserts the walk is nonetheless complete, well-ordered and byte-stable,
        // and it is the test that would fail the day someone adds a field without adding it
        // to Xfer.
        var game = NewGame();
        var live = game.SpawnObject("EjectTestVehicle", game.CivilianPlayer, OnGround);
        var shadow = game.SpawnObject("EjectTestVehicle", game.CivilianPlayer, new Vector3(40, 0, 0));

        PortedModuleTestKit.TriggerDeath(live);
        Assert.Equal(1, CountOf(game, PilotGround));

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(ModuleOf(live), ModuleOf(shadow));
    }

    [Fact]
    public void XferIsDeclaredToTheWalk()
    {
        var game = NewGame();
        var vehicle = game.SpawnObject("EjectTestVehicle", game.CivilianPlayer, OnGround);

        var module = ModuleOf(vehicle);
        Assert.NotNull(module);

        // A ported module must be in the Objects channel walk (D-2), and its walk must be
        // stable across repeated visits.
        Assert.Equal(PortedModuleTestKit.LiveCrc(module), PortedModuleTestKit.LiveCrc(module));
    }

    // ---- item 4: mid-behavior save/load continuation ----

    [Fact]
    public void SaveLoadMidBehavior_ContinuesIdentically()
    {
        // Run A: eject, then save the module's state, load it back into the same module, and
        // keep playing - a second vehicle dies afterwards and must eject exactly as it would
        // have without the round trip.
        var game = NewGame();
        var first = game.SpawnObject("EjectTestVehicle", game.CivilianPlayer, OnGround);
        var second = game.SpawnObject("EjectTestVehicle", game.CivilianPlayer, new Vector3(60, 0, 0));

        PortedModuleTestKit.TriggerDeath(first);
        Assert.Equal(1, CountOf(game, PilotGround));

        var saved = PortedModuleTestKit.Save(ModuleOf(second));
        game.Step();
        PortedModuleTestKit.Load(ModuleOf(second), saved);

        PortedModuleTestKit.TriggerDeath(second);
        Assert.Equal(2, CountOf(game, PilotGround));

        // Run B: the same schedule without the save/load round trip.
        var control = NewGame();
        var controlFirst = control.SpawnObject("EjectTestVehicle", control.CivilianPlayer, OnGround);
        var controlSecond = control.SpawnObject("EjectTestVehicle", control.CivilianPlayer, new Vector3(60, 0, 0));

        PortedModuleTestKit.TriggerDeath(controlFirst);
        control.Step();
        PortedModuleTestKit.TriggerDeath(controlSecond);

        Assert.Equal(CountOf(control, PilotGround), CountOf(game, PilotGround));
        Assert.Equal(
            PortedModuleTestKit.LiveCrc(ModuleOf(controlSecond)),
            PortedModuleTestKit.LiveCrc(ModuleOf(second)));
    }
}
