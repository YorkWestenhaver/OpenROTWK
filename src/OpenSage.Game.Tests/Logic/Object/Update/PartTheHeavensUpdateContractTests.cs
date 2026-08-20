// Mocked-game contract tests for the PartTheHeavensUpdate port (R12): the visual-effect
// module (ring/circle texture with color and time-based radius/opacity/angle FCurves)
// parses, instantiates as a live (permanently parked) runtime module, and round-trips its
// empty state - the [ParseOnly] hole is closed without inventing rendering behavior for a
// client-visual feature (see the module header).

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class PartTheHeavensUpdateContractTests
{
    private const string Definitions = @"
Object HeavensParter
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = PartTheHeavensUpdate ModuleTag_Heavens
    Texture = EXRingTexture.tga
    Color = R:255 G:200 B:64 A:128
    Radius
      Key = T:0 V:0 I:0 O:0
      Key = T:30 V:100 I:0 O:0
      InPadding = HOLD
      OutPadding = CYCLE
    End
    Opacity
      Key = T:0 V:0 I:0 O:0
      Key = T:15 V:255 I:0 O:0
      Key = T:30 V:0 I:0 O:0
      InPadding = HOLD
      OutPadding = HOLD
    End
    Angle
      Key = T:0 V:0 I:0 O:0
      Key = T:30 V:360 I:0 O:0
      InPadding = CYCLE
      OutPadding = CYCLE
    End
  End
End
";

    private static (HeadlessSimGame Game, GameObject Unit) Spawn()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xAD1);
        game.LoadIniText(Definitions);
        return (game, game.SpawnObject("HeavensParter", game.CivilianPlayer, Vector3.Zero));
    }

    private static PartTheHeavensUpdateModuleData GetData(HeadlessSimGame game) =>
        (PartTheHeavensUpdateModuleData)game.AssetStore.ObjectDefinitions
            .GetByName("HeavensParter").Behaviors["ModuleTag_Heavens"].Data;

    [Fact]
    public void ParsesTextureColorAndFCurves()
    {
        var (game, unit) = Spawn();

        var module = unit.BehaviorModules.OfType<PartTheHeavensUpdate>().Single();
        Assert.NotNull(module);

        var data = GetData(game);
        Assert.Equal("EXRingTexture.tga", data.Texture);
        Assert.Equal(255, data.Color.R);
        Assert.Equal(200, data.Color.G);
        Assert.Equal(64, data.Color.B);
        Assert.Equal(128, data.Color.A);

        Assert.NotNull(data.Radius);
        Assert.NotNull(data.Opacity);
        Assert.NotNull(data.Angle);
    }

    [Fact]
    public void ParsesFCurveKeysWithTimeValueAndTangents()
    {
        var (game, _) = Spawn();
        var data = GetData(game);

        Assert.Equal(2, data.Radius.Keys.Count);
        Assert.Equal(Fix64.Zero, data.Radius.Keys[0].T);
        Assert.Equal(Fix64.Zero, data.Radius.Keys[0].V);
        Assert.Equal(Fix64.Zero, data.Radius.Keys[0].I);
        Assert.Equal(Fix64.Zero, data.Radius.Keys[0].O);
        Assert.Equal(Fix64.FromDecimalLiteral("30"), data.Radius.Keys[1].T);
        Assert.Equal(Fix64.FromDecimalLiteral("100"), data.Radius.Keys[1].V);

        Assert.Equal(3, data.Opacity.Keys.Count);
        Assert.Equal(Fix64.FromDecimalLiteral("15"), data.Opacity.Keys[1].T);
        Assert.Equal(Fix64.FromDecimalLiteral("255"), data.Opacity.Keys[1].V);
    }

    [Fact]
    public void ParsesInAndOutPaddingEnums()
    {
        var (game, _) = Spawn();
        var data = GetData(game);

        Assert.Equal(Padding.Hold, data.Radius.InPadding);
        Assert.Equal(Padding.Cycle, data.Radius.OutPadding);

        Assert.Equal(Padding.Hold, data.Opacity.InPadding);
        Assert.Equal(Padding.Hold, data.Opacity.OutPadding);

        Assert.Equal(Padding.Cycle, data.Angle.InPadding);
        Assert.Equal(Padding.Cycle, data.Angle.OutPadding);
    }

    [Fact]
    public void ModuleParksForeverAndSteppingIsHarmless()
    {
        var (game, unit) = Spawn();

        var module = unit.BehaviorModules.OfType<PartTheHeavensUpdate>().Single();
        Assert.Equal(UpdateSleepTime.Forever, module.Update());

        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }
        Assert.False(unit.IsDestroyed);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc()
    {
        var (game, unit) = Spawn();
        var live = unit.BehaviorModules.OfType<PartTheHeavensUpdate>().Single();

        var shadowHost = game.SpawnObject("HeavensParter", game.CivilianPlayer, new Vector3(50, 0, 0));
        var shadow = shadowHost.BehaviorModules.OfType<PartTheHeavensUpdate>().Single();

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
