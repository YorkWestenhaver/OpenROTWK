// Mocked-game contract tests for the SabotageMilitaryFactoryCrateCollide port (R12): the
// REAL INI name (SabotageDuration data block) must produce a live CrateCollide runtime
// module instead of the [ParseOnly] hole. See the module header for why the retail
// execute-on-collide behavior (isValidToExecute/executeCrateBehavior: enemy-only,
// live-target-only, radar infiltration event, EVA message, DISABLED_HACKED) stays parked:
// the shared CrateCollide base has never had its onCollide dispatch ported for ANY sibling
// in this file, and this module additionally needs an EVA queue and a DISABLED_HACKED
// DisabledType value that don't exist yet. These tests cover what the port actually lands -
// parsing/data and a live, side-effect-free runtime module - and are the seam the future
// behavior work (packet testCases) attaches to once that base pipeline and those subsystems
// exist.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Collide;

public class SabotageMilitaryFactoryCrateCollideContractTests
{
    private const string Definitions = @"
Object SaboteurCrate
  KindOf = NONE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = SabotageMilitaryFactoryCrateCollide ModuleTag_Sabotage
    SabotageDuration = 9000
  End
End

Object EnemyBarracks
  KindOf = STRUCTURE FS_BARRACKS
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
End
";

    private static (HeadlessSimGame Game, GameObject Crate, GameObject Barracks) Spawn()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0x5AB07A6E);
        game.LoadIniText(Definitions);
        var crate = game.SpawnObject("SaboteurCrate", game.CivilianPlayer, Vector3.Zero);
        var barracks = game.SpawnObject("EnemyBarracks", game.CivilianPlayer, new Vector3(10, 0, 0));
        return (game, crate, barracks);
    }

    [Fact]
    public void RealIniName_CreatesLiveRuntimeModule_WithParsedData()
    {
        var (game, crate, _) = Spawn();

        var module = crate.BehaviorModules.OfType<SabotageMilitaryFactoryCrateCollide>().Single();
        Assert.NotNull(module);
        Assert.IsAssignableFrom<CrateCollide>(module);
        Assert.IsAssignableFrom<ICollideModule>(module);

        var data = (SabotageMilitaryFactoryCrateCollideModuleData)game.AssetStore.ObjectDefinitions
            .GetByName("SaboteurCrate").Behaviors["ModuleTag_Sabotage"].Data;
        Assert.Equal(9000, data.SabotageDuration);
    }

    [Fact]
    public void CollidingWithEnemyBarracks_IsHarmless_ModuleStaysParked()
    {
        // Documents current status: colliding does not yet disable the target, does not
        // destroy the crate, and does not throw - the execute pipeline is parked (see
        // module header), not wired to a (wrong) guessed effect.
        var (game, crate, barracks) = Spawn();

        crate.OnCollide(barracks, barracks.Translation, Vector3.UnitZ);

        Assert.False(crate.IsDestroyed);
        Assert.False(barracks.IsDestroyed);
        Assert.False(barracks.IsDisabledByType(DisabledType.Default));
    }
}
