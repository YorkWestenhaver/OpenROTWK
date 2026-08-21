// Mocked-game unit tests for the DetachableRiderUpdate port (api-freeze-v1 §6 fitness item 4):
// one test per behavioral branch from research/modules-r13/specs/
// DetachableRiderUpdateModuleData.md §4's test plan, [create -> drive OnRiderDied() -> observable
// effect], plus the mid-death-animation save/load round-trip and the shadow-copy base test - the
// same shape as RunOffMapBehaviorContractTests and CreateObjectDieContractTests.
//
// Observables: the OCL spawn via game.GameLogic.Objects.Where(o => o.Definition.Name ==
// "DeadRider") (the CreateObjectDieContractTests idiom), and SimLocomotorUpdate.Mode (the
// RunOffMapBehaviorContractTests idiom) as the proxy for RunOffMapBehavior's private _triggered.
//
// The sleepy-update caveat, applied throughout (spec §4): a freshly spawned module's first
// Update() runs on the object's SECOND HeadlessSimGame.Step(), and OnRiderDied() called from
// OUTSIDE the frame loop arms CurrentFrame+1, which the next Step() (still processing
// CurrentFrame) does not reach - the wake lands on the Step() after that. Every "fires after N
// frames" assertion below is written against a measured baseline, never a naive step count.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class DetachableRiderUpdateContractTests
{
    // Mirrors the shipped Rohirrim block (cinematicobjects.ini:9789-9807). AnimTime 3000 ms =
    // 15 frames at the frozen 5 Hz.
    private const string Definitions = @"
ObjectCreationList OCL_DeadRider
  CreateObject
    ObjectNames = DeadRider
    Count = 1
  End
End

Object DeadRider
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
End

Locomotor FleeLoco
  Surfaces = GROUND
  Speed = 30
  TurnRate = 360
  Acceleration = 100
  Braking = 100
  Appearance = TWO_LEGS
  ZAxisBehavior = NO_Z_MOTIVE_FORCE
  CloseEnoughDist = 5
End

Object Rider
  KindOf = CAVALRY
  Body = DetachableRiderBody ModuleTag_Body
    MaxHealth = 250
    HealthPercentageWhenRiderDies = 50%
    StartsActive = Yes
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Behavior = DetachableRiderUpdate ModuleTag_Detach
    RiderSubObjects = RUROHRM SHIELD
    RiderlessWeaponSlot = SECONDARY
    RiderlessHordeFlees = Yes
    DeathEntry = AnimState:DEATH_2 AnimTime:3000 RiderOCL:OCL_DeadRider
  End
  Behavior = RunOffMapBehavior ModuleTag_RunOff
    RequiresSpecificTrigger = Yes
    RunOffMapWaypointName = ExitWP
    DieOnMap = Yes
  End
  Locomotor = SET_NORMAL FleeLoco
End

Object RiderNoFlee
  KindOf = CAVALRY
  Body = DetachableRiderBody ModuleTag_Body
    MaxHealth = 250
    HealthPercentageWhenRiderDies = 50%
    StartsActive = Yes
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Behavior = DetachableRiderUpdate ModuleTag_Detach
    RiderSubObjects = RUROHRM SHIELD
    RiderlessWeaponSlot = SECONDARY
    RiderlessHordeFlees = No
    DeathEntry = AnimState:DEATH_2 AnimTime:3000 RiderOCL:OCL_DeadRider
  End
  Behavior = RunOffMapBehavior ModuleTag_RunOff
    RequiresSpecificTrigger = Yes
    RunOffMapWaypointName = ExitWP
    DieOnMap = Yes
  End
  Locomotor = SET_NORMAL FleeLoco
End

Object RiderNoDeathEntry
  KindOf = CAVALRY
  Body = DetachableRiderBody ModuleTag_Body
    MaxHealth = 250
    HealthPercentageWhenRiderDies = 50%
    StartsActive = Yes
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Behavior = DetachableRiderUpdate ModuleTag_Detach
    RiderSubObjects = RUROHRM SHIELD
    RiderlessWeaponSlot = SECONDARY
    RiderlessHordeFlees = Yes
  End
  Behavior = RunOffMapBehavior ModuleTag_RunOff
    RequiresSpecificTrigger = Yes
    RunOffMapWaypointName = ExitWP
    DieOnMap = Yes
  End
  Locomotor = SET_NORMAL FleeLoco
End

Object RiderNoOcl
  KindOf = CAVALRY
  Body = DetachableRiderBody ModuleTag_Body
    MaxHealth = 250
    HealthPercentageWhenRiderDies = 50%
    StartsActive = Yes
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Behavior = DetachableRiderUpdate ModuleTag_Detach
    RiderSubObjects = RUROHRM SHIELD
    RiderlessWeaponSlot = SECONDARY
    RiderlessHordeFlees = Yes
    DeathEntry = AnimState:DEATH_2 AnimTime:3000
  End
  Behavior = RunOffMapBehavior ModuleTag_RunOff
    RequiresSpecificTrigger = Yes
    RunOffMapWaypointName = ExitWP
    DieOnMap = Yes
  End
  Locomotor = SET_NORMAL FleeLoco
End

Object RiderNoRunOffMap
  KindOf = CAVALRY
  Body = DetachableRiderBody ModuleTag_Body
    MaxHealth = 250
    HealthPercentageWhenRiderDies = 50%
    StartsActive = Yes
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Behavior = DetachableRiderUpdate ModuleTag_Detach
    RiderSubObjects = RUROHRM SHIELD
    RiderlessWeaponSlot = SECONDARY
    RiderlessHordeFlees = Yes
    DeathEntry = AnimState:DEATH_2 AnimTime:3000 RiderOCL:OCL_DeadRider
  End
  Locomotor = SET_NORMAL FleeLoco
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xD24U) // "DRU"
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static GameObject Spawn(HeadlessSimGame game, string definitionName)
        => game.SpawnObject(definitionName, game.CivilianPlayer, new Vector3(0, 0, 0));

    private static DetachableRiderUpdate DetachOf(GameObject obj) =>
        obj.BehaviorModules.OfType<DetachableRiderUpdate>().Single();

    private static SimLocomotorUpdate LocoOf(GameObject obj) =>
        obj.BehaviorModules.OfType<SimLocomotorUpdate>().FirstOrDefault();

    private static DetachableRiderUpdateModuleData ModuleDataOf(GameObject obj) =>
        (DetachableRiderUpdateModuleData)obj.Definition.Behaviors.Values
            .Select(behavior => behavior.Data)
            .OfType<DetachableRiderUpdateModuleData>()
            .Single();

    private static GameObject[] DeadRidersIn(HeadlessSimGame game) =>
        game.GameLogic.Objects.Where(o => o.Definition.Name == "DeadRider").ToArray();

    private static void Step(HeadlessSimGame game, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            game.Step();
        }
    }

    // ---------------------------------------------------------------- T1: parse (the bug fix)

    [Fact]
    public void Parse_RiderOclAndAnimTime_AreReadNotDropped()
    {
        var game = NewGame();
        var obj = Spawn(game, "Rider");
        var data = ModuleDataOf(obj);

        Assert.Equal("OCL_DeadRider", data.DeathEntry.RiderOCL!.Value.Name);
        Assert.Equal(new LogicFrameSpan(15), data.DeathEntry.AnimationTime);
        Assert.Equal("DEATH_2", data.DeathEntry.AnimationState);
        Assert.Equal(WeaponSlot.Secondary, data.RiderlessWeaponSlot);
        Assert.Equal(new[] { "RUROHRM", "SHIELD" }, data.RiderSubObjects);
        Assert.True(data.RiderlessHordeFlees);
    }

    // ---------------------------------------------------------------- T1b: RiderOCL optional

    [Fact]
    public void Parse_DeathEntryWithoutRiderOcl_StillParses()
    {
        var game = NewGame();
        var obj = Spawn(game, "RiderNoOcl");
        var data = ModuleDataOf(obj);

        Assert.NotNull(data.DeathEntry);
        Assert.Null(data.DeathEntry.RiderOCL);
    }

    // ---------------------------------------------------------------- T2: idempotence

    [Fact]
    public void OnRiderDied_IsIdempotent()
    {
        var game = NewGame();
        var obj = Spawn(game, "Rider");
        var module = DetachOf(obj);

        module.OnRiderDied();
        module.OnRiderDied(); // second call: must not restart the animation or double-spawn

        Step(game, 30);

        Assert.Single(DeadRidersIn(game));
    }

    // ---------------------------------------------------------------- T3: fires exactly once, after AnimTime

    [Fact]
    public void RiderOcl_FiresExactlyOnce_AndOnlyAfterAnimTime()
    {
        var game = NewGame();
        var obj = Spawn(game, "Rider");
        DetachOf(obj).OnRiderDied();

        // AnimTime = 15 frames. Sleepy-update + external-call wake skew (per-file header):
        // step comfortably short of the wake, assert nothing has spawned yet.
        Step(game, 14);
        Assert.Empty(DeadRidersIn(game));

        // Step until observed (measured baseline, not a naive count).
        for (var i = 0; i < 20 && DeadRidersIn(game).Length == 0; i++)
        {
            game.Step();
        }
        Assert.Single(DeadRidersIn(game));

        // Still exactly one after 10 further steps.
        Step(game, 10);
        Assert.Single(DeadRidersIn(game));
    }

    // ---------------------------------------------------------------- T4: no DeathEntry

    [Fact]
    public void NoDeathEntry_NeverSpawns()
    {
        var game = NewGame();
        var obj = Spawn(game, "RiderNoDeathEntry");
        DetachOf(obj).OnRiderDied();

        Step(game, 40);

        Assert.Empty(DeadRidersIn(game));
        Assert.False(obj.IsDestroyed);
    }

    // ---------------------------------------------------------------- T4b: DeathEntry without RiderOCL

    [Fact]
    public void DeathEntryWithoutRiderOcl_NeverSpawns()
    {
        var game = NewGame();
        var obj = Spawn(game, "RiderNoOcl");
        DetachOf(obj).OnRiderDied();

        Step(game, 40);

        Assert.Empty(DeadRidersIn(game));
    }

    // ---------------------------------------------------------------- T5: RiderlessHordeFlees=Yes

    [Fact]
    public void RiderlessHordeFlees_Yes_TriggersRunOffMapBehavior()
    {
        var game = NewGame();
        game.RegisterWaypoint("ExitWP", new Vector3(500, 0, 0));
        var obj = Spawn(game, "Rider");
        var loco = LocoOf(obj);

        DetachOf(obj).OnRiderDied();

        // Sleepy-update + external-call wake skew: step past it (RunOffMapBehaviorContractTests
        // case 3's idiom).
        Step(game, 4);

        Assert.Equal(SimMoveMode.MoveToPosition, loco.Mode);
    }

    // ---------------------------------------------------------------- T5b: RiderlessHordeFlees=No

    [Fact]
    public void RiderlessHordeFlees_No_DoesNotTriggerRunOffMapBehavior()
    {
        var game = NewGame();
        game.RegisterWaypoint("ExitWP", new Vector3(500, 0, 0));
        var obj = Spawn(game, "RiderNoFlee");
        var loco = LocoOf(obj);

        DetachOf(obj).OnRiderDied();

        Step(game, 20);

        Assert.Equal(SimMoveMode.Idle, loco.Mode);
    }

    // ---------------------------------------------------------------- T5c: missing RunOffMapBehavior sibling

    [Fact]
    public void RiderlessHordeFlees_Yes_NoRunOffMapSibling_IsNoOp()
    {
        var game = NewGame();
        var obj = Spawn(game, "RiderNoRunOffMap");

        DetachOf(obj).OnRiderDied();

        Step(game, 20);

        Assert.False(obj.IsDestroyed);
        // The OCL still spawns on schedule - the null-conditional guard doesn't disturb the
        // rest of OnRiderDied().
        Assert.Single(DeadRidersIn(game));
    }

    // ---------------------------------------------------------------- T6: LockedWeaponSlot

    [Fact]
    public void LockedWeaponSlot_NullUntilRiderless_ThenSecondary()
    {
        var game = NewGame();
        var obj = Spawn(game, "Rider");
        var module = DetachOf(obj);

        Assert.Null(module.LockedWeaponSlot);

        module.OnRiderDied();

        Assert.Equal(WeaponSlot.Secondary, module.LockedWeaponSlot);
    }

    // ---------------------------------------------------------------- T7: mid-animation save/load

    [Fact]
    public void MidDeathAnimation_SaveLoadRoundTrip_ResumesAtSameRemainingFrames()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 7);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static int[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xF00DU);
        var obj = Spawn(game, "Rider");
        var module = DetachOf(obj);
        module.OnRiderDied();

        var trajectory = new int[25];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk; // engine-owned, walk-carried (S6)
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            game.Step();
            trajectory[i] = DeadRidersIn(game).Length;
        }

        return trajectory;
    }

    // ---------------------------------------------------------------- T8: shadow-copy CRC

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidDeathAnimation()
    {
        var game = NewGame();
        var liveHost = Spawn(game, "Rider");
        var live = DetachOf(liveHost);
        live.OnRiderDied();
        Step(game, 4); // drive real state: _riderless true, _deathAnimStartFrame set, _riderOclFired false

        var shadowHost = Spawn(game, "Rider");
        var shadow = DetachOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    // ---------------------------------------------------------------- T9: F-DRU-1, no automatic caller

    [Fact]
    public void SteppingWithoutOnRiderDied_IsInert()
    {
        var game = NewGame();
        game.RegisterWaypoint("ExitWP", new Vector3(500, 0, 0));
        var obj = Spawn(game, "Rider");
        var module = DetachOf(obj);
        var loco = LocoOf(obj);

        Step(game, 40);

        Assert.Empty(DeadRidersIn(game));
        Assert.Equal(SimMoveMode.Idle, loco.Mode);
        Assert.Null(module.LockedWeaponSlot);
    }
}
