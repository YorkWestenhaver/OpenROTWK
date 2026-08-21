// Mocked-game contract tests for the HordeTransportContain port (R13), one test per behavior
// branch modules-r13/specs/HordeTransportContainModuleData.md §3 enumerates.
//
// HordeTransportContain is legacy (GameObject, IGameEngine, plain int/Percentage fields),
// matching its ModuleData ancestor and every landed sibling in Logic/Object/Contain/
// (TransportContain, HordeGarrisonContain) - this directory is not yet in SimCore's
// scoped-directories migration list.
//
// The headless host builds no Drawable model (no DrawModules at all), so ExitStart/ExitEnd
// bone lookups always miss (same limitation ParachuteContainContractTests documents for
// PARA_COG/PARA_ATTACH/PARA_MAN) - TryAssignExitPath therefore always returns false in these
// tests regardless of NumberOfExitPaths, and a passenger that evacuates always ends up at the
// container's own transform (the RemoveUnit "no exit path found" fallback), not at a bone
// position. The NumberOfExitPaths-default test below asserts the parsed field value directly
// rather than an exit-path observable for that reason.
//
// KillPassengersOnDeath has no wired kill-in-place branch in this packet (disclosed gap, see
// HordeTransportContain.cs's header comment and the spec's §5) - the parent-death test below
// deliberately sets it to prove it is currently inert, matching the base
// OpenContainModule eject-with-DamagePercentToUnits path.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Contain;

public class HordeTransportContainContractTests
{
    private const string Definitions = @"
Object TestPassenger
  KindOf = INFANTRY SELECTABLE
  TransportSlotCount = 1
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object TestVehiclePassenger
  KindOf = VEHICLE SELECTABLE
  TransportSlotCount = 1
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object TestTransport
  KindOf = VEHICLE SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = HordeTransportContain ModuleTag_Contain
    Slots = 5
    AllowInsideKindOf = INFANTRY VEHICLE
    AllowEnemiesInside = Yes
    AllowNeutralInside = Yes
    AllowAlliesInside = Yes
    InitialPayload = TestPassenger 3
  End
End

Object TestTransportSlots2
  KindOf = VEHICLE SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = HordeTransportContain ModuleTag_Contain
    Slots = 2
    ContainMax = 99
    AllowInsideKindOf = INFANTRY VEHICLE
    AllowEnemiesInside = Yes
    AllowNeutralInside = Yes
    AllowAlliesInside = Yes
  End
End

Object TestTransportFiltered
  KindOf = VEHICLE SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = HordeTransportContain ModuleTag_Contain
    Slots = 5
    AllowInsideKindOf = INFANTRY VEHICLE
    AllowEnemiesInside = Yes
    AllowNeutralInside = Yes
    AllowAlliesInside = Yes
    PassengerFilter = ANY +INFANTRY
  End
End

Object TestTransportNoExitPathsKey
  KindOf = VEHICLE SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = HordeTransportContain ModuleTag_Contain
    Slots = 5
    AllowInsideKindOf = INFANTRY VEHICLE
    AllowEnemiesInside = Yes
    AllowNeutralInside = Yes
    AllowAlliesInside = Yes
  End
End

Object TestTransportExitDelay
  KindOf = VEHICLE SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = HordeTransportContain ModuleTag_Contain
    Slots = 2
    AllowInsideKindOf = INFANTRY VEHICLE
    AllowEnemiesInside = Yes
    AllowNeutralInside = Yes
    AllowAlliesInside = Yes
    ExitDelay = 250
    InitialPayload = TestPassenger 2
  End
End

Object TestTransportDeath
  KindOf = VEHICLE SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = HordeTransportContain ModuleTag_Contain
    Slots = 5
    AllowInsideKindOf = INFANTRY VEHICLE
    AllowEnemiesInside = Yes
    AllowNeutralInside = Yes
    AllowAlliesInside = Yes
    DamagePercentToUnits = 50%
    KillPassengersOnDeath = Yes
    InitialPayload = TestPassenger 1
  End
End

Object TestTransportNoPips
  KindOf = VEHICLE SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = HordeTransportContain ModuleTag_Contain
    Slots = 5
    AllowInsideKindOf = INFANTRY VEHICLE
    AllowEnemiesInside = Yes
    AllowNeutralInside = Yes
    AllowAlliesInside = Yes
    ShowPips = No
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xB07A) // "bota"(ny)-ish, arbitrary
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    // A freshly spawned sleepy-update module's very first Update() lands on the tick *after*
    // the one it was created on (GameLogic.CreateObject arms it at max(CurrentFrame, 1), and
    // the frame-0 Step() pass only processes modules due at frame 0) - copied verbatim from
    // ParachuteContainContractTests.ArmingStep.
    private static void ArmingStep(HeadlessSimGame game) => game.Step();

    private static HordeTransportContain ContainOf(GameObject obj) =>
        obj.FindBehavior<HordeTransportContain>();

    // ---- testCase 1: CreateModule + ctor seeds InitialPayloads ----

    [Fact]
    public void CtorSeedsInitialPayloads_BeforeAnyStep()
    {
        var game = NewGame();
        var transport = game.SpawnObject("TestTransport", game.CivilianPlayer, new Vector3(0, 0, 0));
        var contain = ContainOf(transport);

        // Ctor-time effect: observable immediately, no Step() needed.
        Assert.Equal(3, contain.ContainedObjectIds.Count);
        foreach (var id in contain.ContainedObjectIds)
        {
            var passenger = game.GameLogic.GetObjectById(id);
            Assert.Equal("TestPassenger", passenger.Definition.Name);
        }
        Assert.Equal(3, contain.OccupiedSlots);
    }

    // ---- testCase 2: TotalSlots reflects Slots, not base ContainMax ----

    [Fact]
    public void TotalSlots_ReflectsSlots_NotContainMax()
    {
        var game = NewGame();
        var transport = game.SpawnObject("TestTransportSlots2", game.CivilianPlayer, new Vector3(0, 0, 0));
        var contain = ContainOf(transport);
        ArmingStep(game);

        Assert.Equal(2, contain.TotalSlots);
        Assert.False(contain.Full);

        var p1 = game.SpawnObject("TestPassenger", game.CivilianPlayer, new Vector3(10, 0, 0));
        var p2 = game.SpawnObject("TestPassenger", game.CivilianPlayer, new Vector3(11, 0, 0));
        var p3 = game.SpawnObject("TestPassenger", game.CivilianPlayer, new Vector3(12, 0, 0));

        contain.Add(p1);
        contain.Add(p2);
        Assert.True(contain.Full);

        // A third unit past capacity is a no-op - Slots (2), not ContainMax (99), gates it.
        contain.Add(p3);
        Assert.Equal(2, contain.ContainedObjectIds.Count);
        Assert.DoesNotContain(p3.Id, contain.ContainedObjectIds);
    }

    // ---- testCase 3: PassengerFilter rejects a non-matching unit ----

    [Fact]
    public void PassengerFilter_RejectsNonMatchingUnit_BaseKindOfGateStaysPermissive()
    {
        var game = NewGame();
        var transport = game.SpawnObject("TestTransportFiltered", game.CivilianPlayer, new Vector3(0, 0, 0));
        var contain = ContainOf(transport);
        ArmingStep(game);

        var infantry = game.SpawnObject("TestPassenger", game.CivilianPlayer, new Vector3(10, 0, 0));
        var vehicle = game.SpawnObject("TestVehiclePassenger", game.CivilianPlayer, new Vector3(11, 0, 0));

        contain.Add(infantry);
        Assert.Contains(infantry.Id, contain.ContainedObjectIds);

        // Rejected by PassengerFilter (CanUnitEnter), not by the base AllowInsideKindOf gate -
        // the test object's AllowInsideKindOf already permits both INFANTRY and VEHICLE.
        contain.Add(vehicle);
        Assert.DoesNotContain(vehicle.Id, contain.ContainedObjectIds);
    }

    // ---- testCase 4: NumberOfExitPaths default is 1 when unset (bug-fix case) ----

    [Fact]
    public void NumberOfExitPaths_DefaultsToOne_WhenKeyUnset()
    {
        var game = NewGame();
        var transport = game.SpawnObject("TestTransportNoExitPathsKey", game.CivilianPlayer, new Vector3(0, 0, 0));
        ArmingStep(game);

        // The pre-port [ParseOnly] stub had no explicit default, silently defaulting to C#'s
        // int 0 (the "don't use ExitStart/ExitEnd" branch) - this proves the port's fix: no
        // NumberOfExitPaths key at all now parses to 1, matching TransportContainModuleData's
        // documented default and every real HordeTransportContain corpus usage.
        Assert.Equal(1, GetModuleData(transport).NumberOfExitPaths);
    }

    private static HordeTransportContainModuleData GetModuleData(GameObject obj) =>
        (HordeTransportContainModuleData)obj.Definition.Behaviors.Values
            .Select(container => container.Data)
            .OfType<HordeTransportContainModuleData>()
            .Single();

    // ---- testCase 5: ExitDelay gates successive evacuations ----

    [Fact]
    public void ExitDelay_GatesSuccessiveEvacuations()
    {
        var game = NewGame();
        var transport = game.SpawnObject("TestTransportExitDelay", game.CivilianPlayer, new Vector3(0, 0, 0));
        var contain = ContainOf(transport);
        ArmingStep(game);

        Assert.Equal(2, contain.ContainedObjectIds.Count);

        contain.Evacuate(); // queues both contained passengers for evac
        game.Step();

        // At 5 Hz LogicFramesPerSecond, ExitDelay = 250ms -> 250/1000*5 = 1.25 -> 1 LogicFrameSpan
        // (integer-truncating cast, matching TransportContain's identical arithmetic) - so only
        // one of the two queued passengers evacuates on the first Step() after Evacuate().
        Assert.Equal(1, contain.ContainedObjectIds.Count);

        game.Step(); // past the delay - the second passenger evacuates
        Assert.Equal(0, contain.ContainedObjectIds.Count);
    }

    // ---- testCase 6: parent-death eject applies DamagePercentToUnits; KillPassengersOnDeath inert (disclosed gap) ----

    [Fact]
    public void ParentDeath_EjectsPassenger_AppliesDamagePercent_KillPassengersOnDeathStillInert()
    {
        var game = NewGame();
        var transport = game.SpawnObject("TestTransportDeath", game.CivilianPlayer, new Vector3(0, 0, 0));
        var contain = ContainOf(transport);
        ArmingStep(game);

        var passengerId = contain.ContainedObjectIds.Single();
        var passenger = game.GameLogic.GetObjectById(passengerId);
        var maxHealth = passenger.BodyModule.MaxHealth;

        transport.Kill();
        game.Step();

        Assert.DoesNotContain(passengerId, contain.ContainedObjectIds);
        Assert.Equal(maxHealth * 0.5f, passenger.BodyModule.Health, 2);

        // KillPassengersOnDeath = Yes was set deliberately - proves it is currently a
        // documented no-op (the sibling HordeTransportContainDamage packet's job to wire), not
        // a silently-dropped kill-in-place effect.
        Assert.False(passenger.IsEffectivelyDead);
    }

    // ---- testCase 7: ShowPips maps to DrawPips ----

    [Fact]
    public void ShowPips_MapsToBaseDrawPips()
    {
        var game = NewGame();
        var transport = game.SpawnObject("TestTransportNoPips", game.CivilianPlayer, new Vector3(0, 0, 0));
        var contain = ContainOf(transport);
        ArmingStep(game);

        Assert.False(contain.DrawPips);
    }
}
