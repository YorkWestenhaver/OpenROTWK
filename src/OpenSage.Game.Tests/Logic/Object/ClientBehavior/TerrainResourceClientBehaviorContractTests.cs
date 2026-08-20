// Mocked-game contract tests for the TerrainResourceClientBehavior port (R12): a
// permanently-parked client marker module (see the module header) that parses, instantiates
// as a live runtime module, and round-trips its empty state - the [ParseOnly] hole is closed
// without inventing sim behavior for the retail client<->server pairing role.
//
// ClientBehaviorModuleData (the "ClientBehavior = ..." INI block) is a distinct module
// family from BehaviorModuleData: ObjectDefinition.ClientBehaviors is a separate dictionary
// from ObjectDefinition.Behaviors, and nothing in the engine currently walks it to build
// GameObject.BehaviorModules (see ObjectDefinition.cs / GameObject.cs - the "ClientBehavior"
// INI keyword only ever populates the parsed-data dictionary today). So these tests exercise
// TerrainResourceClientBehaviorData.CreateModule directly, the same seam the engine would
// call through if/when that dictionary is wired up.

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.ClientBehavior;

public class TerrainResourceClientBehaviorContractTests
{
    private const string Definitions = @"
Object ResourceMarker
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = TerrainResourceBehavior ModuleTag_ServerResource
    Radius = 50
    MaxIncome = 1000
    IncomeInterval = 1000
  End
  ClientBehavior = TerrainResourceClientBehavior ModuleTag_ClientResource
  End
End
";

    private static (HeadlessSimGame Game, GameObject Marker) Spawn()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xC71);
        game.LoadIniText(Definitions);
        return (game, game.SpawnObject("ResourceMarker", game.CivilianPlayer, Vector3.Zero));
    }

    [Fact]
    public void ParserHandlesModuleTagOnlyBlock()
    {
        var (game, _) = Spawn();

        var container = game.AssetStore.ObjectDefinitions.GetByName("ResourceMarker")
            .ClientBehaviors["ModuleTag_ClientResource"];
        Assert.IsType<TerrainResourceClientBehaviorData>(container.Data);
    }

    [Fact]
    public void CoexistsWithServerSideTerrainResourceBehaviorWithoutTagCollision()
    {
        var (game, _) = Spawn();

        var definition = game.AssetStore.ObjectDefinitions.GetByName("ResourceMarker");

        // Behaviors and ClientBehaviors are separate dictionaries, so identical-looking
        // module tags in each namespace never collide.
        Assert.True(definition.Behaviors.ContainsKey("ModuleTag_ServerResource"));
        Assert.True(definition.ClientBehaviors.ContainsKey("ModuleTag_ClientResource"));
    }

    [Fact]
    public void ModuleInstantiatesWithEmptyParametersWithoutError()
    {
        var (game, marker) = Spawn();

        var data = (TerrainResourceClientBehaviorData)game.AssetStore.ObjectDefinitions
            .GetByName("ResourceMarker").ClientBehaviors["ModuleTag_ClientResource"].Data;

        var module = data.CreateModule(marker, game.GameEngine);

        Assert.NotNull(module);
        Assert.IsType<OpenSage.Logic.Object.TerrainResourceClientBehavior>(module);

        module.Dispose();
    }

    [Fact]
    public void ModuleDoesNotInvokeClientUpdateOrDrawCallbacks()
    {
        var (game, marker) = Spawn();

        var data = (TerrainResourceClientBehaviorData)game.AssetStore.ObjectDefinitions
            .GetByName("ResourceMarker").ClientBehaviors["ModuleTag_ClientResource"].Data;

        var module = data.CreateModule(marker, game.GameEngine);

        // The module is a plain BehaviorModule: it is neither an UpdateModule (sim tick)
        // nor a ClientUpdateModule/DrawModule (client update or draw callbacks).
        Assert.IsNotType<UpdateModule>(module);
        Assert.IsNotAssignableFrom<ClientUpdateModule>(module);

        module.Dispose();
    }

    [Fact]
    public void NoExceptionOnObjectDestructionWhenClientBehaviorIsPresent()
    {
        var (game, marker) = Spawn();

        var data = (TerrainResourceClientBehaviorData)game.AssetStore.ObjectDefinitions
            .GetByName("ResourceMarker").ClientBehaviors["ModuleTag_ClientResource"].Data;
        var module = data.CreateModule(marker, game.GameEngine);

        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }

        // The assertion is that none of this throws: the module has no destructor-order
        // dependency on GameObject state (it carries none), so disposing the module and then
        // its owning object - in either order - must be harmless. A no-op Dispose() call is
        // itself an observable, so exercise the "already disposed" path too.
        module.Dispose();
        module.Dispose();
        marker.Dispose();
    }

    [Fact]
    public void MultipleObjectsWithIdenticalClientBehaviorInstancesCoexist()
    {
        var (game, first) = Spawn();
        var second = game.SpawnObject("ResourceMarker", game.CivilianPlayer, new Vector3(100, 0, 0));

        var data = (TerrainResourceClientBehaviorData)game.AssetStore.ObjectDefinitions
            .GetByName("ResourceMarker").ClientBehaviors["ModuleTag_ClientResource"].Data;

        var firstModule = data.CreateModule(first, game.GameEngine);
        var secondModule = data.CreateModule(second, game.GameEngine);

        Assert.NotNull(firstModule);
        Assert.NotNull(secondModule);
        Assert.NotSame(firstModule, secondModule);

        firstModule.Dispose();
        secondModule.Dispose();
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc()
    {
        var (game, marker) = Spawn();
        var data = (TerrainResourceClientBehaviorData)game.AssetStore.ObjectDefinitions
            .GetByName("ResourceMarker").ClientBehaviors["ModuleTag_ClientResource"].Data;

        var live = data.CreateModule(marker, game.GameEngine);

        var shadowHost = game.SpawnObject("ResourceMarker", game.CivilianPlayer, new Vector3(50, 0, 0));
        var shadow = data.CreateModule(shadowHost, game.GameEngine);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);

        live.Dispose();
        shadow.Dispose();
    }
}
