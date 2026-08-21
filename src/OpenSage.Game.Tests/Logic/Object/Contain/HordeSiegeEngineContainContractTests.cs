// Contract tests for the HordeSiegeEngineContain port (R12): the fade-effect wrapper's own
// testable surface - EnterSound/ExitSound (via the RecordingSimEvents sink), the
// enter/exit fade timeline (FadePassengerOnEnter/Exit, EnterFadeTime/ExitFadeTime, FadeReverse,
// FadeFilter), and UpgradeCreationTrigger activation. The base SiegeEngineContainModuleData
// crew/slot system stays [ParseOnly] (see the port's header SCOPE note), so these tests drive
// passenger membership directly through NotifyMemberEntered/NotifyMemberExited rather than
// through a real crew-seating flow.
//
// Frame math (5 Hz, F6, ceil): EnterFadeTime 800ms -> ceil(800*5/1000) = 4 frames.
// ExitFadeTime 500ms -> ceil(500*5/1000) = 3 frames (2.5 rounds up).

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Contain;

public class HordeSiegeEngineContainContractTests
{
    private const string TriggerUpgrade = "HordeSiegeTestUpgrade";

    private const string Definitions = @"
Upgrade " + TriggerUpgrade + @"
  Type = OBJECT
End

Object SiegeEngineHost
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = HordeSiegeEngineContain ModuleTag_Contain
    Slots = 5
    EnterSound = SiegeEnterSound
    ExitSound = SiegeExitSound
    FadeFilter = +INFANTRY
    FadePassengerOnEnter = Yes
    EnterFadeTime = 800
    FadePassengerOnExit = Yes
    ExitFadeTime = 500
    UpgradeCreationTrigger = " + TriggerUpgrade + @" NoModel 0
  End
End

Object SiegeEngineHostReversed
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = HordeSiegeEngineContain ModuleTag_Contain
    EnterSound = SiegeEnterSound
    ExitSound = SiegeExitSound
    FadeFilter = ALL
    FadePassengerOnEnter = Yes
    EnterFadeTime = 800
    FadePassengerOnExit = Yes
    ExitFadeTime = 500
    FadeReverse = Yes
  End
End

Object SiegeEngineHostInstant
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = HordeSiegeEngineContain ModuleTag_Contain
    FadeFilter = ALL
    FadePassengerOnEnter = Yes
    EnterFadeTime = 0
    FadePassengerOnExit = Yes
    ExitFadeTime = 0
  End
End

Object InfantryUnit
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
End

Object VehicleUnit
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
End
";

    private static readonly Vector3 Origin = new(0, 0, 0);

    private static HeadlessSimGame NewGame(uint seed = 0x51EE6)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static HordeSiegeEngineContain ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<HordeSiegeEngineContain>().Single();

    private static LogicFrame Frame(uint value) => new(value);

    // ---- entry: sound + fade-in, gated by FadeFilter ----

    [Fact]
    public void MemberEntered_PlaysEnterSound_ForAnyPassenger()
    {
        var game = NewGame();
        var host = game.SpawnObject("SiegeEngineHost", game.CivilianPlayer, Origin);
        var module = ModuleOf(host);
        var recorder = RecordingSimEvents.InstallOn(game);
        var vehicle = game.SpawnObject("VehicleUnit", game.CivilianPlayer, Origin);

        // Sound plays for every entering passenger, independent of FadeFilter.
        module.NotifyMemberEntered(vehicle.Id);

        Assert.Contains(("SiegeEnterSound", vehicle.Id), recorder.AudioEvents);
    }

    [Fact]
    public void MemberEntered_MatchingFadeFilter_FadesInOverEnterFadeTime()
    {
        var game = NewGame();
        var host = game.SpawnObject("SiegeEngineHost", game.CivilianPlayer, Origin);
        var module = ModuleOf(host);
        var infantry = game.SpawnObject("InfantryUnit", game.CivilianPlayer, Origin);

        var startFrame = Frame(3);
        // Simulate the entry happening at a known frame by stepping the game there first.
        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }
        module.NotifyMemberEntered(infantry.Id);

        // Right at the start: fully faded out (opacity 0), ramping toward 1 over 4 frames.
        Assert.Equal(Fix64.Zero.RawValue, module.OpacityAtFrame(infantry.Id, startFrame).RawValue);
        Assert.Equal(Fix64.FromDecimalLiteral("0.5").RawValue, module.OpacityAtFrame(infantry.Id, startFrame + new LogicFrameSpan(2)).RawValue);
        Assert.Equal(Fix64.One.RawValue, module.OpacityAtFrame(infantry.Id, startFrame + new LogicFrameSpan(4)).RawValue);
    }

    [Fact]
    public void MemberEntered_NonMatchingFadeFilter_NeverFades()
    {
        var game = NewGame();
        var host = game.SpawnObject("SiegeEngineHost", game.CivilianPlayer, Origin);
        var module = ModuleOf(host);
        var vehicle = game.SpawnObject("VehicleUnit", game.CivilianPlayer, Origin);

        // SiegeEngineHost's FadeFilter is +INFANTRY only - a VEHICLE passenger is unaffected.
        module.NotifyMemberEntered(vehicle.Id);

        Assert.Equal(Fix64.One.RawValue, module.GetPassengerOpacity(vehicle.Id).RawValue);
    }

    // ---- exit: sound + fade-out ----

    [Fact]
    public void MemberExited_PlaysExitSound_AndFadesOutOverExitFadeTime()
    {
        var game = NewGame();
        var host = game.SpawnObject("SiegeEngineHost", game.CivilianPlayer, Origin);
        var module = ModuleOf(host);
        var recorder = RecordingSimEvents.InstallOn(game);
        var infantry = game.SpawnObject("InfantryUnit", game.CivilianPlayer, Origin);

        var startFrame = game.GameEngine.SimContext.CurrentFrame;
        module.NotifyMemberExited(infantry.Id);

        Assert.Contains(("SiegeExitSound", infantry.Id), recorder.AudioEvents);

        // Exit fades OUT: full opacity right at the start, zero once ExitFadeTime (3 frames) elapses.
        Assert.Equal(Fix64.One.RawValue, module.OpacityAtFrame(infantry.Id, startFrame).RawValue);
        Assert.Equal(Fix64.Zero.RawValue, module.OpacityAtFrame(infantry.Id, startFrame + new LogicFrameSpan(3)).RawValue);
    }

    // ---- FadeReverse: both directions flip ----

    [Fact]
    public void FadeReverse_FlipsBothEnterAndExitDirections()
    {
        var game = NewGame();
        var host = game.SpawnObject("SiegeEngineHostReversed", game.CivilianPlayer, Origin);
        var module = ModuleOf(host);
        var enterUnit = game.SpawnObject("InfantryUnit", game.CivilianPlayer, Origin);
        var exitUnit = game.SpawnObject("InfantryUnit", game.CivilianPlayer, Origin);

        var now = game.GameEngine.SimContext.CurrentFrame;

        // Reversed entry: normally fades IN (0->1); with FadeReverse it fades OUT (1->0).
        module.NotifyMemberEntered(enterUnit.Id);
        Assert.Equal(Fix64.One.RawValue, module.OpacityAtFrame(enterUnit.Id, now).RawValue);
        Assert.Equal(Fix64.Zero.RawValue, module.OpacityAtFrame(enterUnit.Id, now + new LogicFrameSpan(4)).RawValue);

        // Reversed exit: normally fades OUT (1->0); with FadeReverse it fades IN (0->1).
        module.NotifyMemberExited(exitUnit.Id);
        Assert.Equal(Fix64.Zero.RawValue, module.OpacityAtFrame(exitUnit.Id, now).RawValue);
        Assert.Equal(Fix64.One.RawValue, module.OpacityAtFrame(exitUnit.Id, now + new LogicFrameSpan(3)).RawValue);
    }

    // ---- zero-length fade times: instant, no animation frames ----

    [Fact]
    public void ZeroFadeTimes_ApplyInstantly()
    {
        var game = NewGame();
        var host = game.SpawnObject("SiegeEngineHostInstant", game.CivilianPlayer, Origin);
        var module = ModuleOf(host);
        var enterUnit = game.SpawnObject("InfantryUnit", game.CivilianPlayer, Origin);
        var exitUnit = game.SpawnObject("InfantryUnit", game.CivilianPlayer, Origin);

        var now = game.GameEngine.SimContext.CurrentFrame;

        module.NotifyMemberEntered(enterUnit.Id);
        // EnterFadeTime = 0: already at full opacity at the very frame the fade "starts" - no
        // intermediate animation frame exists.
        Assert.Equal(Fix64.One.RawValue, module.OpacityAtFrame(enterUnit.Id, now).RawValue);

        module.NotifyMemberExited(exitUnit.Id);
        // ExitFadeTime = 0: the exit fade also completes instantly - already at its terminal
        // opacity (fully faded OUT, zero) at the very frame it "starts", with no intermediate
        // animation frame in between.
        Assert.Equal(Fix64.Zero.RawValue, module.OpacityAtFrame(exitUnit.Id, now).RawValue);
    }

    // ---- R13 regression: terminal opacity must survive real game.Step() ticks past
    // completion, not just direct OpacityAtFrame(now) reads against an unpurged in-memory list.
    // Update() runs every frame (SetWakeFrame(UpdateSleepTime.None)); the R12 bug purged each
    // PassengerFade record once its timeline finished, which silently reset
    // GetPassengerOpacity's fallback to Fix64.One - correct for a completed ENTER fade, WRONG
    // for a completed EXIT fade (and for a FadeReverse-flipped ENTRY fade, whose terminal value
    // is also Zero). These tests call game.Step() well past each fade's duration and assert the
    // real GetPassengerOpacity(now) read (not a direct OpacityAtFrame(id, startFrame) read).

    [Fact]
    public void MemberExited_OpacityStaysZero_AfterGameStepsPastExitFadeTime()
    {
        var game = NewGame();
        var host = game.SpawnObject("SiegeEngineHost", game.CivilianPlayer, Origin);
        var module = ModuleOf(host);
        var infantry = game.SpawnObject("InfantryUnit", game.CivilianPlayer, Origin);

        module.NotifyMemberExited(infantry.Id);

        // ExitFadeTime = 500ms -> 3 frames. Step well past that so a real Update() runs on
        // every intervening frame.
        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }

        // Correct terminal value for a completed EXIT fade is Zero (fully faded out /
        // departed) - not the One a purged-record fallback would wrongly report.
        Assert.Equal(Fix64.Zero.RawValue, module.GetPassengerOpacity(infantry.Id).RawValue);
    }

    [Fact]
    public void FadeReverse_ReversedEntry_OpacityStaysZero_AfterGameStepsPastEnterFadeTime()
    {
        var game = NewGame();
        var host = game.SpawnObject("SiegeEngineHostReversed", game.CivilianPlayer, Origin);
        var module = ModuleOf(host);
        var enterUnit = game.SpawnObject("InfantryUnit", game.CivilianPlayer, Origin);

        // Reversed entry fades OUT (1->0); EnterFadeTime = 800ms -> 4 frames.
        module.NotifyMemberEntered(enterUnit.Id);

        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }

        Assert.Equal(Fix64.Zero.RawValue, module.GetPassengerOpacity(enterUnit.Id).RawValue);
    }

    [Fact]
    public void ZeroDurationExitFade_OpacityStaysZero_AfterASingleGameStep()
    {
        var game = NewGame();
        var host = game.SpawnObject("SiegeEngineHostInstant", game.CivilianPlayer, Origin);
        var module = ModuleOf(host);
        var exitUnit = game.SpawnObject("InfantryUnit", game.CivilianPlayer, Origin);

        module.NotifyMemberExited(exitUnit.Id);

        // A zero-duration fade is eligible for purge on the very next Update() tick under the
        // R12 bug (now >= StartFrame + 0 is true immediately), so a single step is enough to
        // reproduce the corruption.
        game.Step();

        Assert.Equal(Fix64.Zero.RawValue, module.GetPassengerOpacity(exitUnit.Id).RawValue);
    }

    // ---- UpgradeCreationTrigger ----

    [Fact]
    public void UpgradeCreationTrigger_GrantsUpgrade_OnFirstMemberEntered()
    {
        var game = NewGame();
        var host = game.SpawnObject("SiegeEngineHost", game.CivilianPlayer, Origin);
        var module = ModuleOf(host);
        var upgrade = game.AssetStore.Upgrades.GetByName(TriggerUpgrade);
        var infantry = game.SpawnObject("InfantryUnit", game.CivilianPlayer, Origin);

        Assert.False(host.HasUpgrade(upgrade));

        module.NotifyMemberEntered(infantry.Id);

        Assert.True(host.HasUpgrade(upgrade));
    }

    [Fact]
    public void UpgradeCreationTrigger_DoesNotFire_WhenNoTriggerConfigured()
    {
        var game = NewGame();
        var host = game.SpawnObject("SiegeEngineHostReversed", game.CivilianPlayer, Origin);
        var module = ModuleOf(host);
        var upgrade = game.AssetStore.Upgrades.GetByName(TriggerUpgrade);
        var infantry = game.SpawnObject("InfantryUnit", game.CivilianPlayer, Origin);

        module.NotifyMemberEntered(infantry.Id);

        // SiegeEngineHostReversed declares no UpgradeCreationTrigger at all.
        Assert.False(host.HasUpgrade(upgrade));
    }

    // ---- member-count bookkeeping ----

    [Fact]
    public void MemberCount_TracksEnterAndExitCalls()
    {
        var game = NewGame();
        var host = game.SpawnObject("SiegeEngineHost", game.CivilianPlayer, Origin);
        var module = ModuleOf(host);
        var a = game.SpawnObject("InfantryUnit", game.CivilianPlayer, Origin);
        var b = game.SpawnObject("InfantryUnit", game.CivilianPlayer, Origin);

        Assert.Equal(0, module.MemberCount);
        module.NotifyMemberEntered(a.Id);
        module.NotifyMemberEntered(b.Id);
        Assert.Equal(2, module.MemberCount);
        module.NotifyMemberExited(a.Id);
        Assert.Equal(1, module.MemberCount);
    }

    // ---- shadow-copy / round-trip base tests ----

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc()
    {
        var game = NewGame();
        var host = game.SpawnObject("SiegeEngineHost", game.CivilianPlayer, Origin);
        var live = ModuleOf(host);
        var infantry = game.SpawnObject("InfantryUnit", game.CivilianPlayer, Origin);
        live.NotifyMemberEntered(infantry.Id);

        var shadowHost = game.SpawnObject("SiegeEngineHost", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidState_SaveLoadRoundTrip_PreservesFadeAndMemberState()
    {
        var game = NewGame();
        var host = game.SpawnObject("SiegeEngineHost", game.CivilianPlayer, Origin);
        var module = ModuleOf(host);
        var infantry = game.SpawnObject("InfantryUnit", game.CivilianPlayer, Origin);
        module.NotifyMemberEntered(infantry.Id);

        var state = PortedModuleTestKit.Save(module);

        var otherHost = game.SpawnObject("SiegeEngineHost", game.CivilianPlayer, new Vector3(200, 0, 0));
        var otherModule = ModuleOf(otherHost);
        PortedModuleTestKit.Load(otherModule, state);

        Assert.Equal(module.MemberCount, otherModule.MemberCount);
        Assert.Equal(
            module.GetPassengerOpacity(infantry.Id).RawValue,
            otherModule.GetPassengerOpacity(infantry.Id).RawValue);
    }
}
