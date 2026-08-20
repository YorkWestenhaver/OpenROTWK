// Mocked-game contract tests for the FloatUpdate port (R12): the module parses,
// instantiates as a live (permanently parked) runtime module, and round-trips its empty
// state - the [ParseOnly] hole is closed without inventing sim behavior for the water-table
// Z-snap and the client-side bob, neither of which is reachable under the current sim seam
// (see the module header).

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class FloatUpdateContractTests
{
    private const string Definitions = @"
Object Corsair
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = FloatUpdate ModuleTag_Float
    Enabled = Yes
  End
End
";

    private static (HeadlessSimGame Game, GameObject Unit) Spawn()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xF10A);
        game.LoadIniText(Definitions);
        return (game, game.SpawnObject("Corsair", game.CivilianPlayer, Vector3.Zero));
    }

    [Fact]
    public void ParsesAndCreatesRuntimeModule()
    {
        var (game, unit) = Spawn();

        var module = unit.BehaviorModules.OfType<FloatUpdate>().Single();
        Assert.NotNull(module);

        var data = (FloatUpdateModuleData)game.AssetStore.ObjectDefinitions
            .GetByName("Corsair").Behaviors["ModuleTag_Float"].Data;
        Assert.True(data.Enabled);
    }

    [Fact]
    public void SteppingIsHarmless_ModuleStaysParked()
    {
        // Object above water with Enabled=true: under the current seam there is no
        // module-facing Z-position write and no water-table query, so stepping the sim
        // must not move, destroy, or otherwise disturb the object.
        var (game, unit) = Spawn();
        var startPosition = unit.Transform.Translation;
        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }
        Assert.False(unit.IsDestroyed);
        Assert.Equal(startPosition, unit.Transform.Translation);
    }

    [Fact]
    public void EnabledFalse_AlsoHarmless()
    {
        // Enabled=false: module is inert either way, same as Enabled=true (see header -
        // neither branch has anywhere sim-visible to run under the current seam).
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xF10B);
        game.LoadIniText(@"
Object Raft
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = FloatUpdate ModuleTag_Float
    Enabled = No
  End
End
");
        var unit = game.SpawnObject("Raft", game.CivilianPlayer, Vector3.Zero);
        var data = (FloatUpdateModuleData)game.AssetStore.ObjectDefinitions
            .GetByName("Raft").Behaviors["ModuleTag_Float"].Data;
        Assert.False(data.Enabled);

        var startPosition = unit.Transform.Translation;
        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }
        Assert.False(unit.IsDestroyed);
        Assert.Equal(startPosition, unit.Transform.Translation);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc()
    {
        var (game, unit) = Spawn();
        var live = unit.BehaviorModules.OfType<FloatUpdate>().Single();

        var shadowHost = game.SpawnObject("Corsair", game.CivilianPlayer, new Vector3(50, 0, 0));
        var shadow = shadowHost.BehaviorModules.OfType<FloatUpdate>().Single();

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
