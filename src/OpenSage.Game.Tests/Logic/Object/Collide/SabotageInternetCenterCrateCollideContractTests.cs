// Mocked-game contract tests for the SabotageInternetCenterCrateCollide port (R12): the
// [ParseOnly] hole is closed the same way as the other landed CrateCollide siblings in this
// directory (ConvertToHijackedVehicleCrateCollide, ConvertToCarBombCrateCollide,
// MoneyCrateCollide, SalvageCrateCollide) - a structurally real, loadable module - rather than
// by inventing the collision-trigger logic (executeCrateBehavior / isValidToExecute) against
// engine capabilities that do not exist yet (see the module's header for the specific gaps:
// no AI goal-object query, no DISABLED_HACKED type / live InternetHackContain runtime, no
// EVA / radar-infiltration event surface). The packet's behavioral test cases (valid sabotage
// trigger, dead/friendly/wrong-building rejection, AI goal validation, hacker disabling) are
// therefore not exercised here; they belong to the follow-up task that lands those capabilities.
//
// The module has no legacy Xfer implementation (BehaviorModule.Xfer is virtual-throwing for
// unported modules), so the SimCore shadow-copy CRC kit does not apply here - these tests cover
// what IS real: real INI parsing through the real IniParser, real module construction, and that
// the parked module survives being stepped.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Collide;

public class SabotageInternetCenterCrateCollideContractTests
{
    private const string Definitions = @"
Object SaboteurCrate
  KindOf = SELECTABLE PARACHUTABLE IMMOBILE NOT_AUTOACQUIRABLE UNATTACKABLE CRATE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1.0
  End
  Behavior = SabotageInternetCenterCrateCollide ModuleTag_Sabotage
    RequiredKindOf = INFANTRY
    SabotageDuration = 900
  End
End
";

    private static (HeadlessSimGame Game, GameObject Crate) Spawn()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xC047);
        game.LoadIniText(Definitions);
        return (game, game.SpawnObject("SaboteurCrate", game.CivilianPlayer, Vector3.Zero));
    }

    [Fact]
    public void ParsesAndCreatesRealRuntimeModule()
    {
        var (game, crate) = Spawn();

        var module = crate.BehaviorModules.OfType<SabotageInternetCenterCrateCollide>().Single();
        Assert.NotNull(module);

        var data = (SabotageInternetCenterCrateCollideModuleData)game.AssetStore.ObjectDefinitions
            .GetByName("SaboteurCrate").Behaviors["ModuleTag_Sabotage"].Data;
        // GPL parseDurationUnsignedInt semantics: authored milliseconds -> ceil(ms * fps / 1000)
        // logic frames (BFME2 fps = 5, IniParser.ParseDurationLogicFrames). 900ms * 5/1000 = 4.5
        // -> ceil -> 5 frames. NOT the raw authored integer (that was the R12 bug: ParseInteger()
        // stored 900 verbatim, ~30x too long once executeCrateBehavior is wired to read this as
        // a frame count).
        Assert.Equal(new LogicFrameSpan(5), data.SabotageDuration);
        Assert.Equal(ObjectKinds.Infantry, data.RequiredKindOf);
    }

    [Fact]
    public void SteppingIsHarmless_ModuleStaysParked()
    {
        var (game, crate) = Spawn();
        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }
        Assert.False(crate.IsDestroyed);
        Assert.NotNull(crate.FindBehavior<SabotageInternetCenterCrateCollide>());
    }
}
