// Mocked-game unit tests for the OneRingPenaltyUpdate port (api-freeze-v1 §6 fitness item 4):
// one test per behavior branch, [create -> tick/drive -> observable effect], covering the
// R13 port spec's testCases
// (bfme2-workbench/research/modules-r13/specs/OneRingPenaltyUpdateModuleData.md §3).
//
// Frame arithmetic: RingTimeBeforeSpawning/TimeSpentRoamingAround/TimeRingPowerSuppressed/
// TimeFrozenFromPenalty are milliseconds (ParseDurationLogicFrames, the SAGE INI convention),
// quantized to the frozen 5 Hz logic rate - "1000" below is exactly 5 logic frames.
//
// Sleepy-update caveat (spec §3): a module spawned via HeadlessSimGame.SpawnObject before any
// Step() call has NextCallFrame == 1; the first Step() runs with _currentFrame == 0, sees
// 1 > 0, and skips the module entirely (only incrementing the frame to 1) - the module's
// Update() first actually executes on the SECOND Step() call. Every test below that asserts
// spawn-phase behavior accounts for this explicitly.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class OneRingPenaltyUpdateContractTests
{
    private const string Definitions = @"
Object RingBearer
  KindOf = INFANTRY
  IsTrainable = Yes
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = OneRingPenaltyUpdate ModuleTag_RingPenalty
    SpecialObjectName = RingToken
    RingTimeBeforeSpawning = 1000
    TimeSpentRoamingAround = 1000
    TimeRingPowerSuppressed = 3000
    StartingDistanceFromMe = 50
    TimeFrozenFromPenalty = 2000
    DiscoveredSound = Sound_RingFound
  End
End

Object RingBearerFastSpawn
  KindOf = INFANTRY
  IsTrainable = Yes
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = OneRingPenaltyUpdate ModuleTag_RingPenalty
    SpecialObjectName = RingToken
    RingTimeBeforeSpawning = 200
    TimeSpentRoamingAround = 1000
    TimeRingPowerSuppressed = 3000
    StartingDistanceFromMe = 50
    TimeFrozenFromPenalty = 2000
    DiscoveredSound = Sound_RingFound
  End
End

Object RingBearerNoSpecialObject
  KindOf = INFANTRY
  IsTrainable = Yes
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = OneRingPenaltyUpdate ModuleTag_RingPenalty
    RingTimeBeforeSpawning = 200
    TimeSpentRoamingAround = 200
    TimeRingPowerSuppressed = 1000
    StartingDistanceFromMe = 50
    TimeFrozenFromPenalty = 1000
  End
End

Object RingToken
  KindOf = NONE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x511E) // "RING" leetspeak, arbitrary fixed seed
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static OneRingPenaltyUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<OneRingPenaltyUpdate>().Single();

    private static void Step(HeadlessSimGame game, int times)
    {
        for (var i = 0; i < times; i++)
        {
            game.Step();
        }
    }

    [Fact]
    public void Create_DoesNotSpawnRingBeforeDelayElapses()
    {
        var game = NewGame();
        game.SpawnObject("RingBearer", game.CivilianPlayer, Vector3.Zero);

        // RingTimeBeforeSpawning = 1000ms = 5 frames. One Step() (module hasn't run at all yet
        // per the sleepy-update caveat) plus three more (four total, _currentFrame reaches 3,
        // still < spawnFrame = 5).
        Step(game, 4);

        Assert.DoesNotContain(game.GameLogic.Objects, o => o.Definition.Name == "RingToken");
    }

    [Fact]
    public void SpawnDelay_Elapses_SpawnsRingAtDistanceFromMe()
    {
        var game = NewGame();
        var origin = new Vector3(100, 100, 0);
        game.SpawnObject("RingBearer", game.CivilianPlayer, origin);

        // Past the spawn frame (5), accounting for the caveat's one-frame registration offset.
        Step(game, 6);

        var ring = Assert.Single(game.GameLogic.Objects, o => o.Definition.Name == "RingToken");
        var distance = Vector3.Distance(
            new Vector3(ring.Translation.X, ring.Translation.Y, 0),
            new Vector3(origin.X, origin.Y, 0));

        Assert.InRange(distance, 49.99f, 50.01f);
        Assert.Equal(game.CivilianPlayer, ring.Owner);
    }

    [Fact]
    public void SpecialObjectNameUnset_NoSpawnNoPenalty()
    {
        var game = NewGame();
        var hero = game.SpawnObject("RingBearerNoSpecialObject", game.CivilianPlayer, Vector3.Zero);

        // Past the spawn frame, past TimeSpentRoamingAround + TimeFrozenFromPenalty's combined
        // window (200ms spawn + 200ms roam = 2 frames; well past by 20 steps).
        Step(game, 20);

        Assert.DoesNotContain(game.GameLogic.Objects, o => o.Definition.Name == "RingToken");
        Assert.False(hero.IsDisabledByType(DisabledType.Paralyzed));
    }

    [Fact]
    public void RoamTimeout_NotDiscovered_AppliesFrozenPenalty()
    {
        var game = NewGame();
        var hero = game.SpawnObject("RingBearerFastSpawn", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(hero);

        // RingTimeBeforeSpawning = 200ms = 1 frame: spawns on the second Step() (caveat).
        Step(game, 2);
        Assert.Contains(game.GameLogic.Objects, o => o.Definition.Name == "RingToken");

        // TimeSpentRoamingAround = 1000ms = 5 frames past spawn; never call
        // NotifyRingDiscovered().
        Step(game, 6);

        Assert.True(hero.IsDisabledByType(DisabledType.Paralyzed));
        Assert.False(module.NotifyRingDiscovered()); // terminal Penalized phase rejects it too
    }

    [Fact]
    public void RoamTimeout_AppliesRingPowerSuppressed_QueryableViaAccessor()
    {
        var game = NewGame();
        var hero = game.SpawnObject("RingBearerFastSpawn", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(hero);

        // Drive to timeout as in RoamTimeout_NotDiscovered_AppliesFrozenPenalty.
        Step(game, 8);

        Assert.True(hero.IsDisabledByType(DisabledType.Paralyzed));
        Assert.True(module.IsRingPowerSuppressed);

        // TimeRingPowerSuppressed = 3000ms = 15 frames past the penalty frame; step well past
        // it (F-RING-6's self-tracked expiry - no engine sweep clears it, the accessor's own
        // frame comparison must reflect this even with no Update() work left in Penalized).
        Step(game, 16);

        Assert.False(module.IsRingPowerSuppressed);
    }

    [Fact]
    public void NotifyRingDiscovered_BeforeTimeout_FiresSoundAndSkipsPenalty()
    {
        var game = NewGame();
        var hero = game.SpawnObject("RingBearerFastSpawn", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(hero);
        var sink = RecordingSimEvents.InstallOn(game);

        // RingTimeBeforeSpawning = 200ms = 1 frame: spawns on the second Step().
        Step(game, 2);

        // One more Step(): still inside the 5-frame roam window.
        Step(game, 1);

        Assert.True(module.NotifyRingDiscovered());
        Assert.Single(sink.AudioEvents, e => e.AudioEventName == "Sound_RingFound" && e.ObjectId == hero.Id);

        // Step past what would have been the roam-timeout frame.
        Step(game, 10);

        Assert.False(hero.IsDisabledByType(DisabledType.Paralyzed));
    }

    [Fact]
    public void NotifyRingDiscovered_BeforeSpawn_IsRejected()
    {
        var game = NewGame();
        var hero = game.SpawnObject("RingBearer", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(hero);
        var sink = RecordingSimEvents.InstallOn(game);

        // RingTimeBeforeSpawning = 1000ms = 5 frames: still in WaitingToSpawn after one Step().
        Step(game, 1);

        Assert.False(module.NotifyRingDiscovered());
        Assert.Empty(sink.AudioEvents);
    }

    [Fact]
    public void NotifyRingDiscovered_AfterAlreadyDiscovered_IsRejected()
    {
        var game = NewGame();
        var hero = game.SpawnObject("RingBearerFastSpawn", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(hero);
        var sink = RecordingSimEvents.InstallOn(game);

        Step(game, 2); // spawn
        Step(game, 1); // still roaming

        Assert.True(module.NotifyRingDiscovered());
        Assert.False(module.NotifyRingDiscovered());

        Assert.Single(sink.AudioEvents);
    }

    [Fact]
    public void Xfer_SaveLoadRoundTrip_MidRoamingPhase()
    {
        var game = NewGame();
        var hero = game.SpawnObject("RingBearerFastSpawn", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(hero);

        Step(game, 2); // spawn
        Step(game, 1); // mid-roam

        var shadowHost = game.SpawnObject("RingBearerFastSpawn", game.CivilianPlayer, new Vector3(500, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidPenalizedPhase()
    {
        var game = NewGame();
        var hero = game.SpawnObject("RingBearerFastSpawn", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(hero);

        Step(game, 8); // drive past spawn + roam timeout into Penalized

        var shadowHost = game.SpawnObject("RingBearerFastSpawn", game.CivilianPlayer, new Vector3(500, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
