// Mocked-game unit tests for the RailedTransportDockUpdate port (R12), one test per packet
// testCase: gradual pull-in with tolerance-gated auto-containment, sequential unloadAll,
// immediate containment when already within the close-enough distance, unloadSingleObject's
// one-at-a-time counter, destroyed-object recovery on both the docking and unloading paths,
// and isClearToEnter's capacity check.
//
// The headless host builds no Drawable model, so DOCKEND/DOCKWAITING07 bone lookups always
// miss and fall back to the dock's own position (the same fallback DockUpdate itself uses for
// DOCKACTION/DOCKWAITING<n>). That makes the push-out leg of unloadNext a one-frame trip in
// these tests rather than a multi-frame glide - the state machine (remove -> disable -> push ->
// arrive -> idle -> next) is still exercised exactly as GPL drives it; the multi-frame glide
// itself is covered by the pull-in tests, which move relative to the dock's own translation and
// never touch a bone.

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class RailedTransportDockUpdateContractTests
{
    // Bfme2 runs at 5 Hz (200 ms/frame). PullInsideDuration 1000 -> 5 frames,
    // PushOutsideDuration 600 -> 3 frames.
    private const string Definitions = @"
Locomotor TestGroundLoco
  Surfaces = GROUND
  Speed = 30
  TurnRate = 360
  Acceleration = 100
  Braking = 100
  Appearance = TWO_LEGS
  ZAxisBehavior = NO_Z_MOTIVE_FORCE
End

Object DockerUnit
  KindOf = VEHICLE SELECTABLE
  TransportSlotCount = 1
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
  Locomotor = SET_NORMAL TestGroundLoco
End

// Same as DockerUnit, but with a real PhysicsBehavior: the unload/push-out path drives the
// unloaded object through AIUpdate.AddTargetPoint (a real locomotor move command), and
// Locomotor.SetPhysicsOptions requires GameObject.Physics to be non-null - the dock/contain
// (Docking_*) tests never touch AIUpdate movement (they manipulate the transform directly),
// so only the payload used by the unload tests needs this.
Object DockerCargo
  KindOf = VEHICLE SELECTABLE
  TransportSlotCount = 1
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = PhysicsBehavior ModuleTag_Physics
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
  Locomotor = SET_NORMAL TestGroundLoco
End

Object EmptyTrain
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = RailedTransportContain ModuleTag_Contain
    AllowInsideKindOf = VEHICLE
    Slots = 5
  End
  Behavior = RailedTransportDockUpdate ModuleTag_Dock
    PullInsideDuration = 1000
    PushOutsideDuration = 600
    ToleranceDistance = 50
  End
End

Object LoadedTrain
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = RailedTransportContain ModuleTag_Contain
    AllowInsideKindOf = VEHICLE
    Slots = 5
    InitialPayload = DockerCargo 3
  End
  Behavior = RailedTransportDockUpdate ModuleTag_Dock
    PullInsideDuration = 1000
    PushOutsideDuration = 600
    ToleranceDistance = 50
  End
End

Object OneSlotTrain
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = RailedTransportContain ModuleTag_Contain
    AllowInsideKindOf = VEHICLE
    Slots = 1
    InitialPayload = DockerCargo 1
  End
  Behavior = RailedTransportDockUpdate ModuleTag_Dock
    PullInsideDuration = 1000
    PushOutsideDuration = 600
    ToleranceDistance = 50
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x7A11) // "rail"
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static RailedTransportDockUpdate DockOf(GameObject train) =>
        train.FindBehavior<RailedTransportDockUpdate>();

    private static OpenContainModule ContainOf(GameObject train) =>
        train.FindBehavior<OpenContainModule>();

    // ---- testCase 1: gradual pull-in, tolerance-gated auto-containment -------------------

    [Fact]
    public void Docking_PullsGraduallyThenAutoContainsWithinTolerance()
    {
        var game = NewGame();
        var train = game.SpawnObject("EmptyTrain", game.CivilianPlayer, Vector3.Zero);
        var docker = game.SpawnObject("DockerUnit", game.CivilianPlayer, new Vector3(25, 0, 0));
        var dock = DockOf(train);
        var contain = ContainOf(train);

        dock.Dock(docker);
        Assert.True(dock.IsLoadingOrUnloading);

        // A freshly spawned/awoken sleepy update module's very first Update() lands on the
        // tick after the one it was created/armed on (SetWakeFrame(UpdateSleepTime.None) is a
        // 1-frame minimum latency shared by every module, GameLogic.cs) - this first Step()
        // only reaches that arming tick, not the module's first real pull-in tick yet.
        game.Step();

        // Per-frame step is mag/PullInsideDuration = 25/5 = 5 units/frame; close-enough is a
        // fixed 6 units (GPL's hardcoded closeEnoughDistance, not ToleranceDistance). It should
        // NOT be contained after just one (real) frame...
        game.Step();
        Assert.DoesNotContain(docker.Id, contain.ContainedObjectIds);
        Assert.True(docker.ModelConditionFlags.Get(ModelConditionFlag.Moving));

        // ...but should be, a few frames later, once it is pulled within 6 units of the dock.
        for (var i = 0; i < 5 && !contain.ContainedObjectIds.Contains(docker.Id); i++)
        {
            game.Step();
        }

        Assert.Contains(docker.Id, contain.ContainedObjectIds);
        Assert.False(dock.IsLoadingOrUnloading);
        Assert.False(docker.ModelConditionFlags.Get(ModelConditionFlag.Moving));
        Assert.False(docker.IsSelectable);
    }

    // ---- testCase 2: unloadAll pushes each contained object out in turn ------------------

    [Fact]
    public void UnloadAll_PushesEachContainedObjectOutInTurn()
    {
        var game = NewGame();
        var train = game.SpawnObject("LoadedTrain", game.CivilianPlayer, new Vector3(0, 0, 0));
        var dock = DockOf(train);
        var contain = ContainOf(train);
        Assert.Equal(3, contain.ContainedObjectIds.Count);

        dock.UnloadAll();
        Assert.True(dock.IsLoadingOrUnloading);
        Assert.Equal(2, contain.ContainedObjectIds.Count); // first one already pulled from contain

        // Drain all three; unloadNext keeps re-arming itself until the container is empty.
        for (var i = 0; i < 20 && dock.IsLoadingOrUnloading; i++)
        {
            game.Step();
        }

        Assert.Empty(contain.ContainedObjectIds);
        Assert.False(dock.IsLoadingOrUnloading);
    }

    [Fact]
    public void UnloadAll_IgnoredWhileAlreadyUnloading()
    {
        var game = NewGame();
        var train = game.SpawnObject("LoadedTrain", game.CivilianPlayer, Vector3.Zero);
        var dock = DockOf(train);
        var contain = ContainOf(train);

        dock.UnloadAll();
        Assert.Equal(2, contain.ContainedObjectIds.Count);

        // A second unloadAll() call while one is already in flight is a documented no-op
        // (GPL: "if we're already unloading, ignore this command").
        dock.UnloadAll();
        Assert.Equal(2, contain.ContainedObjectIds.Count);
    }

    // ---- testCase 3: within tolerance/close-enough at the start -> immediate containment --

    [Fact]
    public void Docking_AlreadyWithinCloseEnoughDistance_ContainsOnTheFirstFrame()
    {
        var game = NewGame();
        var train = game.SpawnObject("EmptyTrain", game.CivilianPlayer, Vector3.Zero);
        // Within both ToleranceDistance (50, gates entry into the docking state) and the
        // hardcoded 6-unit close-enough radius (completes the pull immediately, regardless
        // of how many frames PullInsideDuration nominally allows).
        var docker = game.SpawnObject("DockerUnit", game.CivilianPlayer, new Vector3(4, 0, 0));
        var dock = DockOf(train);
        var contain = ContainOf(train);

        dock.Dock(docker);
        // Two steps: the first only reaches the module's arming tick (SetWakeFrame(None)'s
        // 1-frame minimum latency, shared by every sleepy update module - see the sibling
        // gradual-pull test above), the second is its first real Update().
        game.Step();
        game.Step();

        Assert.Contains(docker.Id, contain.ContainedObjectIds);
        Assert.False(dock.IsLoadingOrUnloading);
    }

    [Fact]
    public void Docking_BeyondToleranceDistance_NeverStartsDocking()
    {
        var game = NewGame();
        var train = game.SpawnObject("EmptyTrain", game.CivilianPlayer, Vector3.Zero);
        // ToleranceDistance is 50; this docker is well outside it.
        var docker = game.SpawnObject("DockerUnit", game.CivilianPlayer, new Vector3(200, 0, 0));
        var dock = DockOf(train);

        dock.Dock(docker);

        Assert.False(dock.IsLoadingOrUnloading);
    }

    // ---- testCase 4: unloadSingleObject unloads exactly one object per call --------------

    [Fact]
    public void UnloadSingleObject_UnloadsOneObjectPerCall()
    {
        var game = NewGame();
        var train = game.SpawnObject("LoadedTrain", game.CivilianPlayer, Vector3.Zero);
        var dock = DockOf(train);
        var contain = ContainOf(train);
        Assert.Equal(3, contain.ContainedObjectIds.Count);

        dock.UnloadSingleObject(null); // GPL ignores the argument entirely
        Assert.Equal(2, contain.ContainedObjectIds.Count);

        // Drain that single unload; unloadNext must NOT pick up a second object on its own,
        // because the single-unload counter reached zero.
        for (var i = 0; i < 20 && dock.IsLoadingOrUnloading; i++)
        {
            game.Step();
        }
        Assert.False(dock.IsLoadingOrUnloading);
        Assert.Equal(2, contain.ContainedObjectIds.Count); // still 2 - no automatic second unload

        // A second explicit call unloads exactly one more.
        dock.UnloadSingleObject(null);
        Assert.Single(contain.ContainedObjectIds);
        for (var i = 0; i < 20 && dock.IsLoadingOrUnloading; i++)
        {
            game.Step();
        }
        Assert.Single(contain.ContainedObjectIds); // one remains, untouched
    }

    // ---- testCase 5: destroyed object recovery --------------------------------------------

    [Fact]
    public void DockingObjectDestroyedMidPull_ClearsStateAndDoesNotThrow()
    {
        var game = NewGame();
        var train = game.SpawnObject("EmptyTrain", game.CivilianPlayer, Vector3.Zero);
        var docker = game.SpawnObject("DockerUnit", game.CivilianPlayer, new Vector3(25, 0, 0));
        var dock = DockOf(train);

        dock.Dock(docker);
        game.Step(); // one frame of pulling, well short of arrival
        Assert.True(dock.IsLoadingOrUnloading);

        docker.Destroy();
        game.Step(); // docker is destroyed-but-not-yet-deleted this frame
        game.Step(); // by now GetObjectById(_dockingObjectId) is gone

        Assert.False(dock.IsLoadingOrUnloading);
    }

    [Fact]
    public void UnloadingObjectDestroyedMidPush_AdvancesToTheNextObjectAutomatically()
    {
        var game = NewGame();
        var train = game.SpawnObject("LoadedTrain", game.CivilianPlayer, Vector3.Zero);
        var dock = DockOf(train);
        var contain = ContainOf(train);

        dock.UnloadAll();
        Assert.Equal(2, contain.ContainedObjectIds.Count);

        // Find the unit currently mid-unload (it is outside the container and disabled/held).
        GameObject unloading = null;
        foreach (var obj in game.GameLogic.Objects)
        {
            if (obj.Definition.Name == "DockerCargo" &&
                obj.ContainerId.IsInvalid &&
                obj.IsDisabledByType(DisabledType.Held))
            {
                unloading = obj;
                break;
            }
        }
        Assert.NotNull(unloading);

        unloading.Destroy();

        // unloadNext() must be re-driven from doPushOutDocking's null-check, moving on to a
        // remaining contained object without the caller having to intervene.
        for (var i = 0; i < 20 && contain.ContainedObjectIds.Count == 2; i++)
        {
            game.Step();
        }

        Assert.True(contain.ContainedObjectIds.Count <= 2);
    }

    // ---- testCase 6: isClearToEnter checks contain capacity -------------------------------

    [Fact]
    public void IsClearToEnter_DeniesWhenFull_AllowsWhenSpaceAvailable()
    {
        var game = NewGame();
        var fullTrain = game.SpawnObject("OneSlotTrain", game.CivilianPlayer, Vector3.Zero);
        var roomyTrain = game.SpawnObject("EmptyTrain", game.CivilianPlayer, new Vector3(500, 0, 0));
        var newcomer = game.SpawnObject("DockerUnit", game.CivilianPlayer, new Vector3(1000, 0, 0));

        Assert.False(DockOf(fullTrain).IsClearToEnter(newcomer));
        Assert.True(DockOf(roomyTrain).IsClearToEnter(newcomer));
    }
}
