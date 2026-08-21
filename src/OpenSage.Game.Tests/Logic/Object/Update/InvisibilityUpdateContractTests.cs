// Mocked-game unit tests for the InvisibilityUpdate port (api-freeze-v1 §6 fitness item 4):
// one test per INI-configurable behavior branch, [create -> tick -> observable effect on the
// object's ObjectStatus stealth bits], plus the shadow-copy base test and a mid-behavior
// save/load continuation. Object definitions are parsed from INI text through the real parser,
// so the S5 quantizing parse (UpdatePeriod ms->frames, DetectionRange->Fix64) is on the path.
//
// Behavioral reference is the GPL analog StealthUpdate.cpp (no same-name GPL file exists).

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class InvisibilityUpdateContractTests
{
    // 5 Hz (F6): UpdatePeriod 400 ms -> 2 logic frames (real AotR data uses 2000 ms = 10 frames).
    private const string Definitions = @"
Object CloakedScout
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = InvisibilityUpdate ModuleTag_Stealth
    StartsActive = Yes
    UpdatePeriod = 400
    InvisibilityNugget
      InvisibilityType = CAMOUFLAGE
      ForbiddenConditions = FIRING_ANY
    End
  End
End

Object DormantScout
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = InvisibilityUpdate ModuleTag_Stealth
    StartsActive = No
    UpdatePeriod = 400
    InvisibilityNugget
      InvisibilityType = STEALTH
    End
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xB00)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static InvisibilityUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<InvisibilityUpdate>().Single();

    private static GameObject Spawn(HeadlessSimGame game, string def) =>
        game.SpawnObject(def, game.CivilianPlayer, Vector3.Zero);

    private static void Step(HeadlessSimGame game, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            game.Step();
        }
    }

    [Fact]
    public void StartsActive_BecomesStealthedAfterTheDelay()
    {
        var game = NewGame();
        var scout = Spawn(game, "CloakedScout");

        // The re-stealth delay (2 frames) must elapse before the object goes invisible.
        Assert.False(scout.TestStatus(ObjectStatus.Stealthed));
        Assert.True(scout.TestStatus(ObjectStatus.CanStealth));

        Step(game, 5);
        Assert.True(scout.TestStatus(ObjectStatus.Stealthed));
    }

    [Fact]
    public void ForbiddenCondition_BreaksStealth_AndClearingItRestealthsAfterDelay()
    {
        var game = NewGame();
        var scout = Spawn(game, "CloakedScout");

        Step(game, 5);
        Assert.True(scout.TestStatus(ObjectStatus.Stealthed));

        // Firing is a ForbiddenConditions bit -> stealth breaks on the next tick.
        scout.SetModelConditionState(ModelConditionFlag.FiringAny);
        Step(game, 1);
        Assert.False(scout.TestStatus(ObjectStatus.Stealthed));

        // While firing, it never re-stealths however long we wait.
        Step(game, 10);
        Assert.False(scout.TestStatus(ObjectStatus.Stealthed));

        // Stop firing: stealth returns only after the re-stealth delay has been re-served.
        scout.ClearModelConditionState(ModelConditionFlag.FiringAny);
        Step(game, 5);
        Assert.True(scout.TestStatus(ObjectStatus.Stealthed));
    }

    [Fact]
    public void StartsActiveNo_NeverStealths_UntilExternallyActivated()
    {
        var game = NewGame();
        var scout = Spawn(game, "DormantScout");

        Assert.False(scout.TestStatus(ObjectStatus.CanStealth));
        Step(game, 10);
        Assert.False(scout.TestStatus(ObjectStatus.Stealthed));

        // GPL receiveGrant(TRUE): a special power switches invisibility on.
        ModuleOf(scout).SetInvisibilityActive(true);
        Step(game, 3);
        Assert.True(scout.TestStatus(ObjectStatus.Stealthed));

        // ...and off again, dropping the object back to visible.
        ModuleOf(scout).SetInvisibilityActive(false);
        Step(game, 1);
        Assert.False(scout.TestStatus(ObjectStatus.Stealthed));
        Assert.False(scout.TestStatus(ObjectStatus.CanStealth));
    }

    [Fact]
    public void MarkAsDetected_ForcesDetected_ThenExpires()
    {
        var game = NewGame();
        var scout = Spawn(game, "CloakedScout");
        Step(game, 5);
        Assert.True(scout.TestStatus(ObjectStatus.Stealthed));

        // A detector reveals us for a 3-frame window.
        ModuleOf(scout).MarkAsDetected(new LogicFrameSpan(3));
        Step(game, 1);
        Assert.True(scout.TestStatus(ObjectStatus.Detected));

        // After the window the DETECTED status clears again.
        Step(game, 5);
        Assert.False(scout.TestStatus(ObjectStatus.Detected));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var live = ModuleOf(Spawn(game, "CloakedScout"));

        // Drive real state in: enabled on, some frames elapsed, a detection pending.
        Step(game, 3);
        live.MarkAsDetected(new LogicFrameSpan(4));
        Step(game, 1);

        // Shadow: same class/data on a second object, in an untouched state.
        var shadowHost = game.SpawnObject("CloakedScout", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 3);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    // The observable trajectory: (stealthed, detected) per frame. If Load lost or misread any
    // walk-carried field (or the engine wake frame), B's continuation diverges from A's.
    private static (bool Stealthed, bool Detected)[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xFEED);
        var scout = Spawn(game, "CloakedScout");
        var module = ModuleOf(scout);

        var trajectory = new (bool, bool)[14];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == 6)
            {
                module.MarkAsDetected(new LogicFrameSpan(3));
            }

            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk;      // engine-owned, walk-carried (S6)
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            game.Step();
            trajectory[i] = (scout.TestStatus(ObjectStatus.Stealthed), scout.TestStatus(ObjectStatus.Detected));
        }

        return trajectory;
    }
}
