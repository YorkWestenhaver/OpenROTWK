// Mocked-game contract tests for the RandomSoundSelectorClientBehavior port (R12): the
// audio-only module parses, instantiates as a live (permanently parked) runtime module, and
// round-trips its empty state - the [ParseOnly] hole is closed without inventing sim behavior
// for a client-audio feature (see the module header).
//
// The spec packet's behavioral test cases (Chance-gated queueing, RerollOnEveryFrame timing,
// VoicePriority arbitration between sibling selectors) all describe the retail client-side
// audio chooser, which lives outside ISimContext (S8, no audio host) and never touches sim
// state or the CRC walk. They cannot be exercised as sim-behavior tests here; this suite
// instead asserts the contract this port actually delivers - Chance/RerollOnEveryFrame/
// VoicePriority parse and are retrievable off the parsed data, the runtime module is live
// (not a ModuleNotPortedException hole), stepping the sim is harmless, and the module's
// (empty) Xfer walk shadow-copies deterministically - so a future audio-host port can add
// the real selection behavior without re-deriving the parked scaffold.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Mathematics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.ClientBehavior;

public class RandomSoundSelectorClientBehaviorContractTests
{
    private const string Definitions = @"
Object ChattyGrunt
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  ClientBehavior = RandomSoundSelectorClientBehavior ModuleTag_Voice
    Chance = 50%
    RerollOnEveryFrame = Yes
    VoicePriority = 10
  End
End

Object QuietGrunt
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  ClientBehavior = RandomSoundSelectorClientBehavior ModuleTag_Voice
    Chance = 0%
    RerollOnEveryFrame = No
    VoicePriority = 1
  End
End
";

    private static (HeadlessSimGame Game, GameObject Unit) Spawn(string objectName = "ChattyGrunt")
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xC0FFEE);
        game.LoadIniText(Definitions);
        return (game, game.SpawnObject(objectName, game.CivilianPlayer, Vector3.Zero));
    }

    [Fact]
    public void ParsesAndCreatesRuntimeModule()
    {
        var (game, unit) = Spawn();

        var module = unit.BehaviorModules.OfType<RandomSoundSelectorClientBehavior>().Single();
        Assert.NotNull(module);

        var data = (RandomSoundSelectorClientBehaviorData)game.AssetStore.ObjectDefinitions
            .GetByName("ChattyGrunt").ClientBehaviors["ModuleTag_Voice"].Data;
        Assert.Equal(new Percentage(0.5f).ToString(), data.Chance.ToString());
        Assert.True(data.RerollOnEveryFrame);
        Assert.Equal(10, data.VoicePriority);
    }

    [Fact]
    public void ParsesZeroChanceAndNoReroll()
    {
        var (game, unit) = Spawn("QuietGrunt");

        var data = (RandomSoundSelectorClientBehaviorData)game.AssetStore.ObjectDefinitions
            .GetByName("QuietGrunt").ClientBehaviors["ModuleTag_Voice"].Data;
        Assert.True(data.Chance.IsZero);
        Assert.False(data.RerollOnEveryFrame);
        Assert.Equal(1, data.VoicePriority);
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
        var live = unit.BehaviorModules.OfType<RandomSoundSelectorClientBehavior>().Single();

        var shadowHost = game.SpawnObject("ChattyGrunt", game.CivilianPlayer, new Vector3(50, 0, 0));
        var shadow = shadowHost.BehaviorModules.OfType<RandomSoundSelectorClientBehavior>().Single();

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
