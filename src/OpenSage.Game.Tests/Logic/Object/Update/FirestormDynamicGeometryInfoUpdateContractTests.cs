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
    FinalHeight = 50.0
    FinalMajorRadius = 60.0
    TransitionTime = 30
    ReverseAtTransitionTime = Yes
    ScorchSize = 100.0
    ParticleOffsetZ = 5.0
    ParticleSystem1 = FirestormSmall
    ParticleSystem2 = FirestormLarge
    FXList = FX_FirestormStart
    DelayBetweenDamageFrames = 5
    DamageAmount = 25.0
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
        Assert.Equal(15, data.InitialDelay);
        Assert.Equal(Fix64.FromDecimalLiteral("10.0"), data.InitialMajorRadius);
        Assert.Equal(Fix64.FromDecimalLiteral("60.0"), data.FinalMajorRadius);
        Assert.Equal(30, data.TransitionTime);
        Assert.True(data.ReverseAtTransitionTime);
        Assert.Equal(Fix64.FromDecimalLiteral("100.0"), data.ScorchSize);
        Assert.Equal(Fix64.FromDecimalLiteral("5.0"), data.ParticleOffsetZ);
        Assert.Equal("FirestormSmall", data.ParticleSystem1);
        Assert.Equal("FirestormLarge", data.ParticleSystem2);
        Assert.Equal("FX_FirestormStart", data.FXList);
        Assert.Equal(5, data.DelayBetweenDamageFrames);
        Assert.Equal(Fix64.FromDecimalLiteral("25.0"), data.DamageAmount);
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
