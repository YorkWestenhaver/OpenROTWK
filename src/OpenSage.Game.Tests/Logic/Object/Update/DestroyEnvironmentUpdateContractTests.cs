// Mocked-game unit tests for the DestroyEnvironmentUpdate port (api-freeze-v1 §6 fitness item
// 4): one test per behavior branch from the R13 spec, [create -> tick -> observable effect],
// plus the shadow-copy base test and a mid-sequence save/load round-trip. Same shape as
// EmpUpdateContractTests.
//
// Sleepy-update caveat (applies to every case below, per the R13 spec): a freshly spawned
// module's NextCallFrame is floored to "now" at creation, and Update() only runs once
// CurrentFrame >= NextCallFrame - the tick that observes CurrentFrame == N runs on the
// (N+1)th HeadlessSimGame.Step() call, not the Nth.
//
// No test asserts anything about a mid-sequence visual/model-condition state (F-DEU-1: none
// exists on this module to assert).

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class DestroyEnvironmentUpdateContractTests
{
    private static readonly Vector3 OnGround = new(0, 0, 0);

    // 5 Hz logic rate: 1000ms = 5 frames.
    private const string Definitions = @"
GameData
  Gravity = -1.0
End

Object DestroyableProp
  KindOf = NO_COLLIDE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = DestroyEnvironmentUpdate ModuleTag_Destroy
    StartTime       = 5000
    DestructionTime = 2000
  End
End

Object InstantDestroyProp
  KindOf = NO_COLLIDE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = DestroyEnvironmentUpdate ModuleTag_Destroy
    StartTime       = 0
    DestructionTime = 1000
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xDE57) // "dest"
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static DestroyEnvironmentUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<DestroyEnvironmentUpdate>().Single();

    // ---- case 1/2: destroyed exactly at StartTime + DestructionTime (25 + 10 = 35 frames) ----

    [Fact]
    public void NotDestroyed_BeforeStartPlusDestructionTimeElapses()
    {
        var game = NewGame();
        var prop = game.SpawnObject("DestroyableProp", game.CivilianPlayer, OnGround);

        for (var i = 0; i < 35; i++)
        {
            game.Step();
            Assert.False(prop.IsDestroyed, $"must not be destroyed before frame 35 (step {i})");
        }
    }

    [Fact]
    public void Destroyed_ExactlyAtStartTimePlusDestructionTime()
    {
        var game = NewGame();
        var prop = game.SpawnObject("DestroyableProp", game.CivilianPlayer, OnGround);

        for (var i = 0; i < 35; i++)
        {
            game.Step();
        }
        Assert.False(prop.IsDestroyed);

        game.Step(); // 36th step: tick sees CurrentFrame == 35, kills
        Assert.True(prop.IsDestroyed);
    }

    [Fact]
    public void NoRepeatKill_AfterDestruction()
    {
        var game = NewGame();
        var prop = game.SpawnObject("DestroyableProp", game.CivilianPlayer, OnGround);

        for (var i = 0; i < 36; i++)
        {
            game.Step();
        }
        Assert.True(prop.IsDestroyed);

        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }
        Assert.True(prop.IsDestroyed);
    }

    // ---- case 4: StartTime = 0 collapses to a single-timer case ----

    [Fact]
    public void ZeroStartTime_DestroysAtDestructionTimeAlone()
    {
        var game = NewGame();
        var prop = game.SpawnObject("InstantDestroyProp", game.CivilianPlayer, OnGround);

        for (var i = 0; i < 5; i++)
        {
            game.Step();
            Assert.False(prop.IsDestroyed, $"must not be destroyed before frame 5 (step {i})");
        }

        game.Step(); // 6th step: tick sees CurrentFrame == 5, kills
        Assert.True(prop.IsDestroyed);
    }

    // ---- shadow-copy + save/load round-trip ----

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidSequence()
    {
        var game = NewGame();
        var prop = game.SpawnObject("DestroyableProp", game.CivilianPlayer, OnGround);
        var live = ModuleOf(prop);

        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }

        var shadowHost = game.SpawnObject("DestroyableProp", game.CivilianPlayer, new Vector3(300, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidSequence_SaveLoadRoundTrip_ContinuesIdentically()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 8);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static bool[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xF00D);
        var prop = game.SpawnObject("DestroyableProp", game.CivilianPlayer, OnGround);
        var module = ModuleOf(prop);

        var trajectory = new bool[40];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk;
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            game.Step();
            trajectory[i] = prop.IsDestroyed;
        }

        return trajectory;
    }
}
