// Mocked-game contract tests for the OathbreakersFadeAwayBehavior port (R13): the linear
// opacity ramp from One to Zero over FadeOutTime, the zero-length-span-is-fully-elapsed
// convention, destroy-on-completion, per-instance independence, and the shadow-copy /
// save-load base tests. Modeled directly on FadeAndDieOrnamentUpdateContractTests.cs.
//
// Frame math at 5 Hz (F6): FadeOutTime = 400 ms -> 2 frames exact (no ceil-rounding
// ambiguity), so EndFrame = spawnFrame + 2.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class OathbreakersFadeAwayBehaviorContractTests
{
    private const string Definitions = @"
Object OathbreakerFading
  KindOf = NONE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = OathbreakersFadeAwayBehavior ModuleTag_Fade
    FadeOutTime = 400
  End
End

Object OathbreakerFadingZero
  KindOf = NONE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = OathbreakersFadeAwayBehavior ModuleTag_Fade
    FadeOutTime = 0
  End
End

Object OathbreakerFadingShort
  KindOf = NONE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = OathbreakersFadeAwayBehavior ModuleTag_Fade
    FadeOutTime = 200
  End
End

Object OathbreakerFadingLong
  KindOf = NONE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = OathbreakersFadeAwayBehavior ModuleTag_Fade
    FadeOutTime = 600
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xFADE)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static OathbreakersFadeAwayBehavior ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<OathbreakersFadeAwayBehavior>().Single();

    private static LogicFrame Frame(uint value) => new(value);

    [Fact]
    public void OpacityRampsLinearly_FromOneToZero_OverFadeOutTime()
    {
        var game = NewGame();
        var obj = game.SpawnObject("OathbreakerFading", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(obj);

        Assert.Equal(Fix64.One.RawValue, module.OpacityAtFrame(Frame(0)).RawValue);
        Assert.Equal(Fix64.FromDecimalLiteral("0.5").RawValue, module.OpacityAtFrame(Frame(1)).RawValue);
        Assert.Equal(Fix64.Zero.RawValue, module.OpacityAtFrame(Frame(2)).RawValue);
        Assert.Equal(Fix64.Zero.RawValue, module.OpacityAtFrame(Frame(100)).RawValue); // stays at zero past end
    }

    [Fact]
    public void ZeroFadeOutTime_IsImmediatelyElapsed()
    {
        var game = NewGame();
        var obj = game.SpawnObject("OathbreakerFadingZero", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(obj);

        Assert.Equal(Fix64.Zero.RawValue, module.OpacityAtFrame(Frame(0)).RawValue);
    }

    [Fact]
    public void ObjectIsDestroyed_AfterFadeOutTimeElapses()
    {
        var game = NewGame();
        var obj = game.SpawnObject("OathbreakerFading", game.CivilianPlayer, Vector3.Zero);

        Assert.False(obj.IsDestroyed);

        // EndFrame = spawnFrame + 2; step generously past it (the module's own Update() cadence
        // lags CurrentFrame by one tick from the ctor's SetWakeFrame(None) schedule: a freshly
        // constructed module's first Update() runs on the object's second HeadlessSimGame.Step()).
        for (var i = 0; i < 15; i++)
        {
            game.Step();
        }

        Assert.True(obj.IsDestroyed);
    }

    [Fact]
    public void SteppingBeforeFadeOutTimeElapses_DoesNotDestroyEarly()
    {
        var game = NewGame();
        var obj = game.SpawnObject("OathbreakerFading", game.CivilianPlayer, Vector3.Zero);

        game.Step();

        Assert.False(obj.IsDestroyed);
    }

    [Fact]
    public void MultipleInstances_FadeIndependently_WithDistinctFadeOutTimes()
    {
        var game = NewGame();
        var shortLived = ModuleOf(game.SpawnObject("OathbreakerFadingShort", game.CivilianPlayer, Vector3.Zero));
        var longLived = ModuleOf(game.SpawnObject("OathbreakerFadingLong", game.CivilianPlayer, new Vector3(50, 0, 0)));

        // OathbreakerFadingShort: FadeOutTime 200ms -> 1 frame, fully faded by frame 1.
        // OathbreakerFadingLong: FadeOutTime 600ms -> 3 frames, still ramping at frame 1.
        Assert.Equal(Fix64.Zero.RawValue, shortLived.OpacityAtFrame(Frame(1)).RawValue);
        Assert.NotEqual(Fix64.Zero.RawValue, longLived.OpacityAtFrame(Frame(1)).RawValue);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc()
    {
        var game = NewGame();
        var obj = game.SpawnObject("OathbreakerFading", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(obj);

        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }

        var shadowHost = game.SpawnObject("OathbreakerFading", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidState_SaveLoadRoundTrip_ContinuesBitIdentical()
    {
        var gameA = NewGame(seed: 0xC0DE);
        var gameB = NewGame(seed: 0xC0DE);
        var objA = gameA.SpawnObject("OathbreakerFading", gameA.CivilianPlayer, Vector3.Zero);
        var objB = gameB.SpawnObject("OathbreakerFading", gameB.CivilianPlayer, Vector3.Zero);
        var moduleA = ModuleOf(objA);
        var moduleB = ModuleOf(objB);

        for (var i = 0; i < 3; i++)
        {
            gameA.Step();
            gameB.Step();
        }

        var state = PortedModuleTestKit.Save(moduleB);
        var wake = moduleB.NextWakeFrameForWalk;
        PortedModuleTestKit.Load(moduleB, state);
        moduleB.NextWakeFrameForWalk = wake;

        for (var i = 0; i < 15; i++)
        {
            gameA.Step();
            gameB.Step();
        }

        Assert.Equal(objA.IsDestroyed, objB.IsDestroyed);
    }
}
