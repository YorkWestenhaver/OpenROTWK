// Mocked-game unit tests for the StealthUpdate port (experiment-round-4 §4.1, template v1.1
// fitness item 4): one test per INI-configurable behavior branch, [create -> tick -> observable
// status effect], plus the mid-behavior save/load round-trip and the shadow-copy base test.
// Object definitions are parsed through the real parser, so the S5 duration/Fix64 parse
// functions are on the tested path.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Mathematics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class StealthUpdateContractTests
{
    // 5 Hz (F6): StealthDelay 400 ms -> 2 frames.
    private const string Definitions = @"
Object Sneak
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = StealthUpdate ModuleTag_Stealth
    StealthDelay = 400
    InnateStealth = Yes
    StealthForbiddenConditions = MOVING
  End
End

Object Spy
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = StealthUpdate ModuleTag_Stealth
    StealthDelay = 400
    GrantedBySpecialPower = Yes
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x57E)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static StealthUpdate StealthModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<StealthUpdate>().Single();

    [Fact]
    public void InnateStealth_BecomesStealthedAfterDelay()
    {
        var game = NewGame();
        var unit = game.SpawnObject("Sneak", game.CivilianPlayer, Vector3.Zero);

        // Innate stealth grants the CAN_STEALTH status immediately.
        Assert.True(unit.TestStatus(ObjectStatus.CanStealth));

        // Not stealthed on the very first frame (StealthDelay timer is still running).
        game.Step();
        Assert.False(unit.TestStatus(ObjectStatus.Stealthed));

        // After the 2-frame delay elapses it becomes stealthed and stays that way.
        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }
        Assert.True(unit.TestStatus(ObjectStatus.Stealthed));
    }

    [Fact]
    public void ForbiddenCondition_PreventsStealth_AndReArmsTheTimer()
    {
        var game = NewGame();
        var unit = game.SpawnObject("Sneak", game.CivilianPlayer, Vector3.Zero);

        // A forbidden model condition (S1-maintained) blocks stealthing entirely.
        unit.SetModelConditionState(ModelConditionFlag.Moving);
        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }
        Assert.False(unit.TestStatus(ObjectStatus.Stealthed));

        // Clearing it lets the object re-arm and eventually stealth again.
        unit.ClearModelConditionState(ModelConditionFlag.Moving);
        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }
        Assert.True(unit.TestStatus(ObjectStatus.Stealthed));
    }

    [Fact]
    public void Stealthed_RevealsWhenForbiddenConditionAppears()
    {
        var game = NewGame();
        var unit = game.SpawnObject("Sneak", game.CivilianPlayer, Vector3.Zero);

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }
        Assert.True(unit.TestStatus(ObjectStatus.Stealthed));

        // Firing/moving reveals a stealthed unit on the next tick.
        unit.SetModelConditionState(ModelConditionFlag.Moving);
        game.Step();
        Assert.False(unit.TestStatus(ObjectStatus.Stealthed));
    }

    [Fact]
    public void MarkAsDetected_SetsDetected_ThenClearsWhenTimerLapses()
    {
        var game = NewGame();
        var unit = game.SpawnObject("Sneak", game.CivilianPlayer, Vector3.Zero);
        var module = StealthModuleOf(unit);

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }
        Assert.True(unit.TestStatus(ObjectStatus.Stealthed));
        Assert.False(unit.TestStatus(ObjectStatus.Detected));

        // A detector (the natural pair) arms the detected timer for 3 frames.
        module.MarkAsDetected(new LogicFrameSpan(3));
        game.Step();
        Assert.True(unit.TestStatus(ObjectStatus.Detected));

        // After the timer lapses the object is no longer detected.
        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }
        Assert.False(unit.TestStatus(ObjectStatus.Detected));
    }

    [Fact]
    public void GrantedBySpecialPower_StartsInactive_GrantExpires()
    {
        var game = NewGame();
        var spy = game.SpawnObject("Spy", game.CivilianPlayer, Vector3.Zero);
        var module = StealthModuleOf(spy);

        // No innate stealth and asleep until granted.
        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }
        Assert.False(spy.TestStatus(ObjectStatus.Stealthed));

        // A special power grants stealth for 5 frames.
        module.ReceiveGrant(true, new LogicFrameSpan(5));
        Assert.True(spy.TestStatus(ObjectStatus.Stealthed));

        // The grant counts down and self-disables; the unit reveals.
        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }
        Assert.False(spy.TestStatus(ObjectStatus.Stealthed));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var unit = game.SpawnObject("Sneak", game.CivilianPlayer, Vector3.Zero);
        var live = StealthModuleOf(unit);

        // Drive real state in: stealthed, then detected for a window.
        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }
        live.MarkAsDetected(new LogicFrameSpan(10));
        game.Step();

        // The shadow is the same class on a second, differently-stated object.
        var shadowHost = game.SpawnObject("Sneak", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = StealthModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 3);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    // Records the (stealthed, detected) status pair each frame; a lost or misread Xfer field
    // makes B's continuation diverge from A's.
    private static int[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xFEED);
        var unit = game.SpawnObject("Sneak", game.CivilianPlayer, Vector3.Zero);
        var module = StealthModuleOf(unit);

        var trajectory = new int[14];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == 5)
            {
                module.MarkAsDetected(new LogicFrameSpan(4));
            }

            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk;   // engine-owned, walk-carried (S6)
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            game.Step();
            trajectory[i] = (unit.TestStatus(ObjectStatus.Stealthed) ? 1 : 0) |
                            (unit.TestStatus(ObjectStatus.Detected) ? 2 : 0);
        }

        return trajectory;
    }
}
