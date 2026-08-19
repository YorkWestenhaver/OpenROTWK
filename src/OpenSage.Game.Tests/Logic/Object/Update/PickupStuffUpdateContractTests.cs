// Mocked-game contract tests for the PickupStuffUpdate port (R11 Track B): the periodic
// crate scan (filter + range), one pickup per cadence tick in ascending-ObjectId order,
// and the shadow-copy base test. The headless players are AI-side (IsHuman false), so
// SkirmishAIOnly = Yes exercises the enabled path.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class PickupStuffUpdateContractTests
{
    // Scan every 0.5 s -> ceil(0.5 * 5 Hz) = 3 frames; crates within 100.
    private const string Definitions = @"
Object StuffCrate
  KindOf = CRATE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
End

Object NotACrate
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object CrateGrabber
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = PickupStuffUpdate ModuleTag_Pickup
    SkirmishAIOnly = Yes
    StuffToPickUp = NONE +CRATE
    ScanRange = 100
    ScanIntervalSeconds = 0.5
  End
End
";

    private static HeadlessSimGame NewGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xC8A);
        game.LoadIniText(Definitions);
        return game;
    }

    private static void StepFrames(HeadlessSimGame game, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            game.Step();
        }
    }

    private static PickupStuffUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<PickupStuffUpdate>().Single();

    [Fact]
    public void ScanInterval_ParsesSecondsToFrames()
    {
        var game = NewGame();
        var data = (PickupStuffUpdateModuleData)game.AssetStore.ObjectDefinitions
            .GetByName("CrateGrabber").Behaviors["ModuleTag_Pickup"].Data;

        // 0.5 s at the frozen 5 Hz = ceil(2.5) = 3 logic frames.
        Assert.Equal(3u, data.ScanInterval.Value);
    }

    [Fact]
    public void CrateInRange_IsPickedUp()
    {
        var game = NewGame();
        var grabber = game.SpawnObject("CrateGrabber", game.CivilianPlayer, new Vector3(100, 100, 0));
        var crate = game.SpawnObject("StuffCrate", game.CivilianPlayer, new Vector3(130, 100, 0));
        // A non-crate neighbor is never consumed.
        var bystander = game.SpawnObject("NotACrate", game.CivilianPlayer, new Vector3(110, 100, 0));

        StepFrames(game, 4);

        Assert.True(crate.IsDestroyed);
        Assert.False(bystander.IsDestroyed);
        Assert.Equal(1, ModuleOf(grabber).NumPickedUp);
    }

    [Fact]
    public void CrateOutOfRange_IsIgnored()
    {
        var game = NewGame();
        var grabber = game.SpawnObject("CrateGrabber", game.CivilianPlayer, new Vector3(100, 100, 0));
        var crate = game.SpawnObject("StuffCrate", game.CivilianPlayer, new Vector3(300, 100, 0));

        StepFrames(game, 8);

        Assert.False(crate.IsDestroyed);
        Assert.Equal(0, ModuleOf(grabber).NumPickedUp);
    }

    [Fact]
    public void OnePickupPerTick_LowestIdFirst()
    {
        var game = NewGame();
        var grabber = game.SpawnObject("CrateGrabber", game.CivilianPlayer, new Vector3(100, 100, 0));
        var first = game.SpawnObject("StuffCrate", game.CivilianPlayer, new Vector3(140, 100, 0));
        var second = game.SpawnObject("StuffCrate", game.CivilianPlayer, new Vector3(120, 100, 0));

        // First cadence tick (wake frame 3 runs on the 4th step): exactly one crate
        // consumed - the lower ObjectId (spawned first), whatever the distances say.
        StepFrames(game, 4);
        Assert.True(first.IsDestroyed);
        Assert.False(second.IsDestroyed);

        // Next tick takes the second.
        StepFrames(game, 3);
        Assert.True(second.IsDestroyed);
        Assert.Equal(2, ModuleOf(grabber).NumPickedUp);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var liveHost = game.SpawnObject("CrateGrabber", game.CivilianPlayer, new Vector3(100, 100, 0));
        game.SpawnObject("StuffCrate", game.CivilianPlayer, new Vector3(130, 100, 0));
        StepFrames(game, 4);
        var live = ModuleOf(liveHost);
        Assert.Equal(1, live.NumPickedUp);

        var shadow = ModuleOf(game.SpawnObject("CrateGrabber", game.CivilianPlayer, new Vector3(400, 400, 0)));

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
