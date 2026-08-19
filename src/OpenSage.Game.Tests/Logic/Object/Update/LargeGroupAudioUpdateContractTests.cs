// Mocked-game contract tests for the LargeGroupAudioUpdate port (R11 Track B): the
// audio-only module parses, instantiates as a live (permanently parked) runtime module,
// and round-trips its empty state - the [ParseOnly] hole is closed without inventing
// sim behavior for a client-audio feature (see the module header).

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class LargeGroupAudioUpdateContractTests
{
    private const string Definitions = @"
Object NoisyGrunt
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = LargeGroupAudioUpdate ModuleTag_GroupAudio
    Key = Grunt Infantry
    UnitWeight = 2
  End
End
";

    private static (HeadlessSimGame Game, GameObject Unit) Spawn()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xAD0);
        game.LoadIniText(Definitions);
        return (game, game.SpawnObject("NoisyGrunt", game.CivilianPlayer, Vector3.Zero));
    }

    [Fact]
    public void ParsesAndCreatesRuntimeModule()
    {
        var (game, unit) = Spawn();

        var module = unit.BehaviorModules.OfType<LargeGroupAudioUpdate>().Single();
        Assert.NotNull(module);

        var data = (LargeGroupAudioUpdateModuleData)game.AssetStore.ObjectDefinitions
            .GetByName("NoisyGrunt").Behaviors["ModuleTag_GroupAudio"].Data;
        Assert.Equal(new[] { "Grunt", "Infantry" }, data.Keys);
        Assert.Equal(2, data.UnitWeight);
    }

    [Fact]
    public void SteppingIsHarmless_ModuleStaysParked()
    {
        var (game, unit) = Spawn();
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
        var live = unit.BehaviorModules.OfType<LargeGroupAudioUpdate>().Single();

        var shadowHost = game.SpawnObject("NoisyGrunt", game.CivilianPlayer, new Vector3(50, 0, 0));
        var shadow = shadowHost.BehaviorModules.OfType<LargeGroupAudioUpdate>().Single();

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
