// Mocked-game contract tests for the FadeAndDieOrnamentUpdate port (R12): the ADSR opacity
// envelope timeline (InitialOpacity -> attack -> decay -> sustain -> release -> destroy),
// per-instance independence, and the shadow-copy base test. CurrentOpacity/OpacityAtFrame is
// a pure function of the stored spawn frame + the parsed envelope (S8: rendering is absent
// from ISimContext, so opacity carries no sim-input obligation), so most assertions read it
// directly at named frames rather than depending on the module's own Update() wake cadence -
// only the destroy assertion needs real game.Step() stepping, since that is the one
// sim-visible effect.
//
// Frame math (5 Hz, F6): 200 ms -> 1 frame, 400 ms -> 2 frames (all exact - no ceil rounding
// artifacts to reason about). FadeSmall: InitialDelay 200(1) / AttackTime 400(2) / DecayTime
// 400(2) / SustainTime 200(1) / ReleaseTime 400(2) -> boundaries delay=1, attack=3, decay=5,
// sustain=6, release=8. FadeOther: InitialDelay 0(0) / AttackTime 200(1) / DecayTime 200(1) /
// SustainTime 200(1) / ReleaseTime 200(1) -> boundaries delay=0, attack=1, decay=2, sustain=3,
// release=4 - deliberately distinct from FadeSmall for the independence test.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class FadeAndDieOrnamentUpdateContractTests
{
    private const string Definitions = @"
Object FadeSmall
  KindOf = NONE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = FadeAndDieOrnamentUpdate ModuleTag_Fade
    Envelope = InitialOpacity:0 PeakOpacity:1 SustainOpacity:0.5 InitialDelay:200 AttackTime:400 DecayTime:400 SustainTime:200 ReleaseTime:400
  End
End

Object FadeOther
  KindOf = NONE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = FadeAndDieOrnamentUpdate ModuleTag_Fade
    Envelope = InitialOpacity:0.2 PeakOpacity:0.9 SustainOpacity:0.4 InitialDelay:0 AttackTime:200 DecayTime:200 SustainTime:200 ReleaseTime:200
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xF1DE)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static FadeAndDieOrnamentUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<FadeAndDieOrnamentUpdate>().Single();

    private static LogicFrame Frame(uint value) => new(value);

    [Fact]
    public void EnvelopeProgresses_InitialAtZero_PeakAfterAttackTime()
    {
        var game = NewGame();
        var ornament = game.SpawnObject("FadeSmall", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(ornament);

        // Frame 0: still within InitialDelay (delay ends at frame 1) - InitialOpacity.
        Assert.Equal(Fix64.Zero.RawValue, module.OpacityAtFrame(Frame(0)).RawValue);

        // Midway through the attack ramp (delay=1, attack=3): halfway to Peak.
        Assert.Equal(Fix64.FromDecimalLiteral("0.5").RawValue, module.OpacityAtFrame(Frame(2)).RawValue);

        // Attack completes at frame 3: PeakOpacity reached.
        Assert.Equal(Fix64.One.RawValue, module.OpacityAtFrame(Frame(3)).RawValue);
    }

    [Fact]
    public void SustainPhase_HoldsSustainOpacity_ForSustainTime()
    {
        var game = NewGame();
        var ornament = game.SpawnObject("FadeSmall", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(ornament);

        // Decay ends at frame 5, sustain runs [5, 6): held at SustainOpacity throughout.
        var sustainOpacity = Fix64.FromDecimalLiteral("0.5").RawValue;
        Assert.Equal(sustainOpacity, module.OpacityAtFrame(Frame(5)).RawValue);
        Assert.Equal(sustainOpacity, module.OpacityAtFrame(Frame(6)).RawValue); // release t=0, still Sustain
    }

    [Fact]
    public void ReleasePhase_FadesSustainOpacityToZero_OverReleaseTime()
    {
        var game = NewGame();
        var ornament = game.SpawnObject("FadeSmall", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(ornament);

        // Release runs [6, 8): halfway (frame 7) is half of SustainOpacity; at/after 8 it's zero.
        Assert.Equal(Fix64.FromDecimalLiteral("0.25").RawValue, module.OpacityAtFrame(Frame(7)).RawValue);
        Assert.Equal(Fix64.Zero.RawValue, module.OpacityAtFrame(Frame(8)).RawValue);
        Assert.Equal(Fix64.Zero.RawValue, module.OpacityAtFrame(Frame(100)).RawValue); // stays at zero past release
    }

    [Fact]
    public void InitialDelay_PreventsEnvelopeAdvance_ForFirstDelayFrames()
    {
        var game = NewGame();
        var module = ModuleOf(game.SpawnObject("FadeSmall", game.CivilianPlayer, Vector3.Zero));

        // InitialDelay = 200 ms = 1 frame: opacity stays pinned at InitialOpacity through the
        // whole delay window (frame 0), and is still exactly InitialOpacity at frame 1 (the
        // delay just ended - attack t=0, no advance has happened yet).
        Assert.Equal(Fix64.Zero.RawValue, module.OpacityAtFrame(Frame(0)).RawValue);
        Assert.Equal(Fix64.Zero.RawValue, module.OpacityAtFrame(Frame(1)).RawValue);

        // Contrast: FadeOther has InitialDelay = 0, so by frame 1 its (1-frame) attack has
        // already completed and it is sitting at PeakOpacity - proof it is the delay, not the
        // attack ramp itself, holding FadeSmall back.
        var other = ModuleOf(game.SpawnObject("FadeOther", game.CivilianPlayer, new Vector3(20, 0, 0)));
        Assert.Equal(Fix64.FromDecimalLiteral("0.9").RawValue, other.OpacityAtFrame(Frame(1)).RawValue);
    }

    [Fact]
    public void ObjectIsDestroyed_AfterEnvelopeCompletes()
    {
        var game = NewGame();
        var ornament = game.SpawnObject("FadeSmall", game.CivilianPlayer, Vector3.Zero);

        Assert.False(ornament.IsDestroyed);

        // Release ends at frame 8; step generously past it (the module's own Update() cadence
        // lags CurrentFrame by one tick from the ctor's SetWakeFrame(None) schedule).
        for (var i = 0; i < 15; i++)
        {
            game.Step();
        }

        Assert.True(ornament.IsDestroyed);
    }

    [Fact]
    public void SteppingBeforeCompletion_DoesNotDestroyEarly()
    {
        var game = NewGame();
        var ornament = game.SpawnObject("FadeSmall", game.CivilianPlayer, Vector3.Zero);

        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }

        Assert.False(ornament.IsDestroyed);
    }

    [Fact]
    public void MultipleOrnaments_FadeIndependently_WithDistinctEnvelopeParameters()
    {
        var game = NewGame();
        var small = ModuleOf(game.SpawnObject("FadeSmall", game.CivilianPlayer, Vector3.Zero));
        var other = ModuleOf(game.SpawnObject("FadeOther", game.CivilianPlayer, new Vector3(50, 0, 0)));

        // At frame 1: FadeSmall is still in InitialDelay (ends at 1, so this IS the boundary ->
        // attack t=0, still InitialOpacity=0). FadeOther has no delay and its 1-frame attack
        // already completed, so it should already read PeakOpacity (0.9).
        Assert.Equal(Fix64.Zero.RawValue, small.OpacityAtFrame(Frame(1)).RawValue);
        Assert.Equal(Fix64.FromDecimalLiteral("0.9").RawValue, other.OpacityAtFrame(Frame(1)).RawValue);

        // At frame 4: FadeOther has fully released (release ends at 4) -> zero. FadeSmall is
        // still mid-decay (decay ends at 5) -> somewhere between Peak and Sustain, not zero.
        Assert.Equal(Fix64.Zero.RawValue, other.OpacityAtFrame(Frame(4)).RawValue);
        Assert.NotEqual(Fix64.Zero.RawValue, small.OpacityAtFrame(Frame(4)).RawValue);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc()
    {
        var game = NewGame();
        var ornament = game.SpawnObject("FadeSmall", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(ornament);

        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }

        var shadowHost = game.SpawnObject("FadeSmall", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidState_SaveLoadRoundTrip_ContinuesBitIdentical()
    {
        var gameA = NewGame(seed: 0xC0DE);
        var gameB = NewGame(seed: 0xC0DE);
        var ornamentA = gameA.SpawnObject("FadeSmall", gameA.CivilianPlayer, Vector3.Zero);
        var ornamentB = gameB.SpawnObject("FadeSmall", gameB.CivilianPlayer, Vector3.Zero);
        var moduleA = ModuleOf(ornamentA);
        var moduleB = ModuleOf(ornamentB);

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

        Assert.Equal(ornamentA.IsDestroyed, ornamentB.IsDestroyed);
    }
}
