// Mocked-game unit tests for the MissileLauncherBuildingUpdate port (api-freeze-v1 §6
// fitness item 4): one test per behavior branch, [create -> tick/notify -> observable
// effect], covering the R12 task packet's testCases.
//
// The special power's ready frame is a driven input (see the MIGRATION NOTE at the top of
// the module file): tests call NotifySpecialPowerReadyFrame directly instead of standing up
// a special-power reload timer, exactly the shape SpecialPowerCompletionDie's tests use for
// SetCreator.
//
// The observable is the DOOR_1_* model-condition flags the module drives (client-side
// output; HeadlessSimGame carries a real GameClient so Drawable/ModelConditionFlags are
// live) plus the RecordingSimEvents FX log for the FX-on-transition testCase.

using System.Linq;
using System.Numerics;
using OpenSage;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class MissileLauncherBuildingUpdateContractTests
{
    private const string Definitions = @"
Object Silo
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = MissileLauncherBuildingUpdate ModuleTag_Door
    SpecialPowerTemplate = TestSuperweapon
    DoorOpenTime = 30
    DoorWaitOpenTime = 3
    DoorCloseTime = 4
    DoorOpeningFX = FX_DoorOpening
    DoorWaitingToCloseFX = FX_DoorWaitingToClose
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xD00D)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static MissileLauncherBuildingUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<MissileLauncherBuildingUpdate>().Single();

    private static bool AnyDoorFlagSet(GameObject obj) =>
        obj.ModelConditionFlags.Get(ModelConditionFlag.Door1Opening)
        || obj.ModelConditionFlags.Get(ModelConditionFlag.Door1WaitingOpen)
        || obj.ModelConditionFlags.Get(ModelConditionFlag.Door1WaitingToClose)
        || obj.ModelConditionFlags.Get(ModelConditionFlag.Door1Closing);

    /// <summary>Steps until <paramref name="predicate"/> is first true, returning the logic
    /// frame it was actually observed on (Update() runs on the pre-increment frame counter,
    /// so this is one less than GameLogic.CurrentFrame right after the Step() that flipped it).</summary>
    private static uint StepUntil(HeadlessSimGame game, System.Func<bool> predicate, int maxSteps = 2000)
    {
        for (var i = 0; i < maxSteps; i++)
        {
            game.Step();
            if (predicate())
            {
                return game.GameLogic.CurrentFrame.Value - 1;
            }
        }

        Assert.Fail($"predicate never became true within {maxSteps} steps");
        return 0;
    }

    [Fact]
    public void PreOpeningTiming_OpensAtReadyFrameMinusDoorOpenTime()
    {
        // doorOpenTime=30, power ready frame 300 -> opens (DOOR_1_OPENING) at frame 270.
        var game = NewGame();
        var silo = game.SpawnObject("Silo", game.CivilianPlayer, Vector3.Zero);
        ModuleOf(silo).NotifySpecialPowerReadyFrame(new LogicFrame(300));

        var openedAtFrame = StepUntil(game, () => silo.ModelConditionFlags.Get(ModelConditionFlag.Door1Opening));

        Assert.Equal(270u, openedAtFrame);
    }

    [Fact]
    public void PrematureReadiness_ForcesDoorFullyOpenBeforeItsOwnTimeout()
    {
        // The door is mid-OPENING (its own timeout is still pending, readyFrame-1 in the
        // future) when the special power reports a NEW, earlier ready frame - the door must
        // pop straight to OPEN at that earlier frame rather than wait for its own timeout.
        var game = NewGame();
        var silo = game.SpawnObject("Silo", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(silo);
        module.NotifySpecialPowerReadyFrame(new LogicFrame(1000));

        var openingStartedAtFrame = StepUntil(game, () => silo.ModelConditionFlags.Get(ModelConditionFlag.Door1Opening));
        Assert.Equal(970u, openingStartedAtFrame); // whenToStartOpening = 1000 - 30

        // Door is now OPENING with its own timeout scheduled for 1000 - 1 = 999. Report an
        // earlier ready frame - the reload got refreshed faster than the door predicted.
        module.NotifySpecialPowerReadyFrame(new LogicFrame(980));

        var openedAtFrame = StepUntil(game, () => silo.ModelConditionFlags.Get(ModelConditionFlag.Door1WaitingOpen));

        Assert.Equal(980u, openedAtFrame); // popped early, well before the original 999/1000
        Assert.False(silo.ModelConditionFlags.Get(ModelConditionFlag.Door1Opening));
    }

    [Fact]
    public void InitiateIntentToDoSpecialPower_InOpenState_TransitionsToWaitingToClose()
    {
        var game = NewGame();
        var silo = game.SpawnObject("Silo", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(silo);
        // Default (never-notified) ready frame is 0 (GPL's own "uninitialized" quirk): the
        // door pops OPEN on the module's very first tick.
        StepUntil(game, () => silo.ModelConditionFlags.Get(ModelConditionFlag.Door1WaitingOpen));

        // Wrong template: no-op.
        Assert.False(module.InitiateIntentToDoSpecialPower("SomeOtherPower"));
        Assert.True(silo.ModelConditionFlags.Get(ModelConditionFlag.Door1WaitingOpen));

        // This module's own template: fires the shutdown sequence.
        Assert.True(module.InitiateIntentToDoSpecialPower("TestSuperweapon"));
        Assert.True(silo.ModelConditionFlags.Get(ModelConditionFlag.Door1WaitingToClose));
        Assert.False(silo.ModelConditionFlags.Get(ModelConditionFlag.Door1WaitingOpen));
    }

    [Fact]
    public void TimeoutProgression_WaitingToCloseThenClosingThenClosed()
    {
        // doorWaitOpenTime=3, doorCloseTime=4 (from Definitions). Default (never-notified)
        // ready frame is 0, so the door pops OPEN immediately (GPL's own quirk) - cheap to
        // reach. Only once OPEN do we push the ready frame far out, so the later CLOSING
        // clamp (half the time left before ready) never binds and doorCloseTime alone governs.
        var game = NewGame();
        var silo = game.SpawnObject("Silo", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(silo);

        StepUntil(game, () => silo.ModelConditionFlags.Get(ModelConditionFlag.Door1WaitingOpen));
        module.NotifySpecialPowerReadyFrame(new LogicFrame(1_000_000));
        Assert.True(module.InitiateIntentToDoSpecialPower("TestSuperweapon"));
        Assert.True(silo.ModelConditionFlags.Get(ModelConditionFlag.Door1WaitingToClose));

        // doorWaitOpenTime expires -> DOOR_CLOSING.
        StepUntil(game, () => silo.ModelConditionFlags.Get(ModelConditionFlag.Door1Closing));
        Assert.False(silo.ModelConditionFlags.Get(ModelConditionFlag.Door1WaitingToClose));

        // doorClosingTime expires -> DOOR_CLOSED (no door flag left set).
        StepUntil(game, () => !AnyDoorFlagSet(silo));
        Assert.False(AnyDoorFlagSet(silo));
    }

    [Fact]
    public void UnderConstruction_SkipsAllTransitions()
    {
        var game = NewGame();
        var silo = game.SpawnObject("Silo", game.CivilianPlayer, Vector3.Zero);
        silo.SetObjectStatus(ObjectStatus.UnderConstruction, true);
        // Default ready frame (0) would otherwise pop the door open on the very first tick.

        for (var i = 0; i < 20; i++)
        {
            game.Step();
        }

        Assert.False(AnyDoorFlagSet(silo));

        // Once construction finishes, the module resumes deciding.
        silo.SetObjectStatus(ObjectStatus.UnderConstruction, false);
        StepUntil(game, () => silo.ModelConditionFlags.Get(ModelConditionFlag.Door1WaitingOpen));
    }

    [Fact]
    public void ModelConditionsAndFx_EachTransitionClearsOldSetsNewFiresFx()
    {
        var game = NewGame();
        var recorder = RecordingSimEvents.InstallOn(game);
        var silo = game.SpawnObject("Silo", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(silo);
        module.NotifySpecialPowerReadyFrame(new LogicFrame(10));

        // OPENING: sets DOOR_1_OPENING alone and fires DoorOpeningFX, unoriented, at the door.
        StepUntil(game, () => silo.ModelConditionFlags.Get(ModelConditionFlag.Door1Opening));
        Assert.False(silo.ModelConditionFlags.Get(ModelConditionFlag.Door1WaitingOpen));
        Assert.False(silo.ModelConditionFlags.Get(ModelConditionFlag.Door1WaitingToClose));
        Assert.False(silo.ModelConditionFlags.Get(ModelConditionFlag.Door1Closing));
        var openingFx = Assert.Single(recorder.Events);
        Assert.Equal("FX_DoorOpening", openingFx.FXListName);
        Assert.Equal(silo.Id, openingFx.ObjectId);
        Assert.Equal(FXOrientation.PositionOnly, openingFx.Orientation);

        // OPEN (via the natural OPENING timeout this time, readyFrame - 1 = 9): clears
        // DOOR_1_OPENING, sets DOOR_1_WAITING_OPEN. No FX is configured for this transition.
        StepUntil(game, () => silo.ModelConditionFlags.Get(ModelConditionFlag.Door1WaitingOpen));
        Assert.False(silo.ModelConditionFlags.Get(ModelConditionFlag.Door1Opening));
        Assert.Single(recorder.Events); // unchanged - OPEN fires no FX in this data set

        // WAITING_TO_CLOSE: clears DOOR_1_WAITING_OPEN, sets DOOR_1_WAITING_TO_CLOSE, fires
        // DoorWaitingToCloseFX.
        Assert.True(module.InitiateIntentToDoSpecialPower("TestSuperweapon"));
        Assert.True(silo.ModelConditionFlags.Get(ModelConditionFlag.Door1WaitingToClose));
        Assert.False(silo.ModelConditionFlags.Get(ModelConditionFlag.Door1WaitingOpen));
        Assert.Equal(2, recorder.Events.Count);
        var waitingToCloseFx = recorder.Events[1];
        Assert.Equal("FX_DoorWaitingToClose", waitingToCloseFx.FXListName);
        Assert.Equal(silo.Id, waitingToCloseFx.ObjectId);
        Assert.Equal(FXOrientation.PositionOnly, waitingToCloseFx.Orientation);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var silo = game.SpawnObject("Silo", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(silo);
        live.NotifySpecialPowerReadyFrame(new LogicFrame(300));
        StepUntil(game, () => silo.ModelConditionFlags.Get(ModelConditionFlag.Door1Opening));

        var shadowHost = game.SpawnObject("Silo", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
