// Mocked-game contract tests for the CritterEmitterUpdate port (R13): the periodic
// fire-FX-and-maybe-spawn cadence, the optional SpawnObject guard (the only enabled AotR
// usage, natureunits.ini:4720-4724, leaves it unset), and the shadow-copy base test.
//
// Sleepy-update caveat (same convention EmpUpdateContractTests/PickupStuffUpdateContractTests
// already document): SetWakeFrame(UpdateSleepTime.Frames(N)) in this module's ctor arms the
// wake frame at N, and the tick that observes CurrentFrame == N runs on the (N+1)th
// HeadlessSimGame.Step() call, not the Nth - so a freshly spawned emitter's first emission
// lands on the (ReloadTime.Value + 1)th Step().

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class CritterEmitterUpdateContractTests
{
    // 5 Hz logic rate (F6): 2000 ms = 10 frames.
    private const string Definitions = @"
Object CritterSpawnObject
  KindOf = NO_COLLIDE
  Body = ImmortalBody ModuleTag_Body
    MaxHealth = 1
  End
End

Object BirdEmitterFull
  KindOf = PRELOAD CRITTER_EMITTER
  Body = ImmortalBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = CritterEmitterUpdate ModuleTag_Critters
    FX = FX_Birds
    SpawnObject = CritterSpawnObject
    ReloadTime = 2000
  End
End

Object BirdEmitterFxOnly
  KindOf = PRELOAD CRITTER_EMITTER
  Body = ImmortalBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = CritterEmitterUpdate ModuleTag_Critters
    FX = FX_Birds
    ; SpawnObject intentionally omitted - matches the only enabled AotR usage
    ; (natureunits.ini:4720-4724).
    ReloadTime = 2000
  End
End
";

    private static HeadlessSimGame NewGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xC317);
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

    private static CritterEmitterUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<CritterEmitterUpdate>().Single();

    [Fact]
    public void ReloadTime_ParsesMillisecondsToFrames()
    {
        var game = NewGame();
        var data = (CritterEmitterUpdateModuleData)game.AssetStore.ObjectDefinitions
            .GetByName("BirdEmitterFull").Behaviors["ModuleTag_Critters"].Data;

        // 2000 ms at the frozen 5 Hz = 10 logic frames, exact.
        Assert.Equal(10u, data.ReloadTime.Value);
    }

    [Fact]
    public void NoEmission_BeforeReloadTimeElapses()
    {
        var game = NewGame();
        var emitter = game.SpawnObject("BirdEmitterFull", game.CivilianPlayer, new Vector3(0, 0, 0));
        var events = RecordingSimEvents.InstallOn(game);

        for (var i = 0; i < 10; i++)
        {
            game.Step();
            Assert.Empty(events.Events);
            Assert.Equal(0, ModuleOf(emitter).NumEmissions);
        }
    }

    [Fact]
    public void FirstEmission_FiresFxAndSpawnsObject_AtReloadTime()
    {
        var game = NewGame();
        var emitter = game.SpawnObject("BirdEmitterFull", game.CivilianPlayer, new Vector3(0, 0, 0));
        var events = RecordingSimEvents.InstallOn(game);

        StepFrames(game, 11); // the tick seeing CurrentFrame == 10 runs on the 11th Step()

        var fx = Assert.Single(events.Events);
        Assert.Equal("FX_Birds", fx.FXListName);
        Assert.Equal(emitter.Id, fx.ObjectId);
        Assert.Equal(ObjectId.Invalid, fx.SourceObjectId);
        Assert.Equal(FXOrientation.ToObject, fx.Orientation);

        Assert.Equal(1, ModuleOf(emitter).NumEmissions);

        var spawned = game.GameLogic.Objects
            .Where(o => o.Definition.Name == "CritterSpawnObject")
            .ToList();
        Assert.Single(spawned);
        Assert.Equal(emitter.Owner, spawned[0].Owner);
    }

    [Fact]
    public void SecondEmission_FiresAgain_ReloadTimeLater()
    {
        var game = NewGame();
        var emitter = game.SpawnObject("BirdEmitterFull", game.CivilianPlayer, new Vector3(0, 0, 0));
        var events = RecordingSimEvents.InstallOn(game);

        StepFrames(game, 11);
        Assert.Equal(1, ModuleOf(emitter).NumEmissions);

        StepFrames(game, 10);

        Assert.Equal(2, events.Events.Count);
        Assert.Equal(2, ModuleOf(emitter).NumEmissions);

        var spawned = game.GameLogic.Objects
            .Where(o => o.Definition.Name == "CritterSpawnObject")
            .ToList();
        Assert.Equal(2, spawned.Count);
    }

    [Fact]
    public void FxOnlyEmitter_FiresFxButSpawnsNothing()
    {
        var game = NewGame();
        var emitter = game.SpawnObject("BirdEmitterFxOnly", game.CivilianPlayer, new Vector3(0, 0, 0));
        var events = RecordingSimEvents.InstallOn(game);

        StepFrames(game, 11);

        var fx = Assert.Single(events.Events);
        Assert.Equal("FX_Birds", fx.FXListName);
        Assert.Equal(1, ModuleOf(emitter).NumEmissions);

        Assert.All(game.GameLogic.Objects, o => Assert.Same(emitter, o));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidCadence()
    {
        var game = NewGame();
        var liveHost = game.SpawnObject("BirdEmitterFull", game.CivilianPlayer, new Vector3(0, 0, 0));
        StepFrames(game, 11);
        var live = ModuleOf(liveHost);
        Assert.Equal(1, live.NumEmissions);

        var shadow = ModuleOf(game.SpawnObject("BirdEmitterFull", game.CivilianPlayer, new Vector3(400, 400, 0)));

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
