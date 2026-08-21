// Mocked-game contract tests for the FirestormDynamicGeometryInfoUpdate port (R12): a
// permanently-parked module (see the module header) - it parses, instantiates as a live
// runtime module, and round-trips its empty state. The [ParseOnly] hole is closed without
// inventing sim behavior for particle-emitter radius sync / scorch placement / area damage,
// none of which ISimContext exposes a seam for yet.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class FirestormDynamicGeometryInfoUpdateContractTests
{
    private const string Definitions = @"
Object FirestormMine
  KindOf = IMMOBILE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = FirestormDynamicGeometryInfoUpdate ModuleTag_Firestorm
    InitialDelay = 15
    InitialHeight = 0.0
    InitialMajorRadius = 10.0
    InitialMinorRadius = 8.0
    FinalHeight = 50.0
    FinalMajorRadius = 60.0
    FinalMinorRadius = 48.0
    TransitionTime = 30
    ReverseAtTransitionTime = Yes
    ScorchSize = 100.0
    ParticleOffsetZ = 5.0
    ParticleSystem1 = FirestormSmall
    ParticleSystem2 = FirestormLarge
    ParticleSystem3 = FirestormMedium
    ParticleSystem16 = FirestormFinal
    FXList = FX_FirestormStart
    DelayBetweenDamageFrames = 5
    DamageAmount = 25.0
    MaxHeightForDamage = 30.0
  End
End
";

    private static (HeadlessSimGame Game, GameObject Unit) Spawn()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xF12E);
        game.LoadIniText(Definitions);
        return (game, game.SpawnObject("FirestormMine", game.CivilianPlayer, Vector3.Zero));
    }

    [Fact]
    public void ParsesAndCreatesRuntimeModule()
    {
        var (game, unit) = Spawn();

        var module = unit.BehaviorModules.OfType<FirestormDynamicGeometryInfoUpdate>().Single();
        Assert.NotNull(module);

        var data = (FirestormDynamicGeometryInfoUpdateModuleData)game.AssetStore.ObjectDefinitions
            .GetByName("FirestormMine").Behaviors["ModuleTag_Firestorm"].Data;

        // InitialDelay/TransitionTime: GPL parses these through INI::parseDurationUnsignedInt
        // (DynamicGeometryInfoUpdate.cpp:69,77), i.e. ms -> logic frames via
        // ceil(ms * fps / 1000) at BFME2's 5 Hz tick - NOT the raw millisecond value. 15ms and
        // 30ms both quantize up to a single logic frame at 5 fps (ceil(0.075) = ceil(0.15) = 1).
        Assert.Equal(new LogicFrameSpan(1), data.InitialDelay);
        Assert.Equal(new LogicFrameSpan(1), data.TransitionTime);

        Assert.Equal(Fix64.FromDecimalLiteral("10.0"), data.InitialMajorRadius);
        Assert.Equal(Fix64.FromDecimalLiteral("8.0"), data.InitialMinorRadius);
        Assert.Equal(Fix64.FromDecimalLiteral("60.0"), data.FinalMajorRadius);
        Assert.Equal(Fix64.FromDecimalLiteral("48.0"), data.FinalMinorRadius);
        Assert.True(data.ReverseAtTransitionTime);
        Assert.Equal(Fix64.FromDecimalLiteral("100.0"), data.ScorchSize);
        Assert.Equal(Fix64.FromDecimalLiteral("5.0"), data.ParticleOffsetZ);
        Assert.Equal("FirestormSmall", data.ParticleSystem1);
        Assert.Equal("FirestormLarge", data.ParticleSystem2);
        Assert.Equal("FirestormMedium", data.ParticleSystem3);
        Assert.Null(data.ParticleSystem4);
        Assert.Equal("FirestormFinal", data.ParticleSystem16);
        Assert.Equal("FX_FirestormStart", data.FXList);

        // DelayBetweenDamageFrames: GPL parses this through INI::parseDurationReal
        // (FirestormDynamicGeometryInfoUpdate.cpp:71), i.e. ms -> Real (fractional, unrounded)
        // logic frames via msec * fps / 1000 - unlike InitialDelay/TransitionTime above, this
        // one is NOT ceiled to a whole frame. Encode the GPL formula directly rather than a
        // hardcoded raw value, since it's the formula (not a specific quantized result) that is
        // the spec here.
        var fps = Fix64.FromRaw((long)game.SageGame.LogicFramesPerSecond() << 32);
        var thousand = Fix64.FromRaw(1000L << 32);
        var expectedDelayBetweenDamageFrames = Fix64.FromDecimalLiteral("5.0") * fps / thousand;
        Assert.Equal(expectedDelayBetweenDamageFrames, data.DelayBetweenDamageFrames);

        Assert.Equal(Fix64.FromDecimalLiteral("25.0"), data.DamageAmount);
        Assert.Equal(Fix64.FromDecimalLiteral("30.0"), data.MaxHeightForDamage);
    }

    [Fact]
    public void MaxHeightForDamage_DefaultsTo20_WhenOmitted()
    {
        // GPL default: FirestormDynamicGeometryInfoUpdateModuleData ctor sets
        // m_maxHeightForDamage = 20.0f (FirestormDynamicGeometryInfoUpdate.cpp:60).
        const string definitions = @"
Object FirestormMineNoOverride
  KindOf = IMMOBILE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = FirestormDynamicGeometryInfoUpdate ModuleTag_Firestorm
    InitialMajorRadius = 10.0
    FinalMajorRadius = 60.0
  End
End
";
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xF12E);
        game.LoadIniText(definitions);

        var data = (FirestormDynamicGeometryInfoUpdateModuleData)game.AssetStore.ObjectDefinitions
            .GetByName("FirestormMineNoOverride").Behaviors["ModuleTag_Firestorm"].Data;
        Assert.Equal(Fix64.FromDecimalLiteral("20.0"), data.MaxHeightForDamage);
    }

    [Fact]
    public void SteppingIsHarmless_ModuleStaysParked()
    {
        var (game, unit) = Spawn();
        for (var i = 0; i < 40; i++)
        {
            game.Step();
        }
        Assert.False(unit.IsDestroyed);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc()
    {
        var (game, unit) = Spawn();
        var live = unit.BehaviorModules.OfType<FirestormDynamicGeometryInfoUpdate>().Single();

        var shadowHost = game.SpawnObject("FirestormMine", game.CivilianPlayer, new Vector3(50, 0, 0));
        var shadow = shadowHost.BehaviorModules.OfType<FirestormDynamicGeometryInfoUpdate>().Single();

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
