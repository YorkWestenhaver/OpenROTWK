// Mocked-game unit tests for the SpyVisionUpdate Round-9 port (api-freeze-v1 §6 fitness
// item 4): one test per activation branch of the state machine [create -> tick -> observable
// activation state], plus the mid-behavior save/load round-trip and the shadow-copy base test.
//
// The observable effect under test is the activation STATE (SpyVisionUpdate.IsCurrentlyActive):
// the reveal side-effect and the enemy-player vision fan-out are deferred to the partition
// flag-day (findings SVU-1/SVU-2 in research/modules-r9/SpyVisionUpdate.md), so the activation
// flag is precisely the determinism-relevant surface this port owns. Object definitions are
// parsed from INI text through the real parser, so the quantizing S5 parse functions
// (ParseDurationLogicFrames, the UpgradeMux child table) are on the tested path.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Tests.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class SpyVisionUpdateContractTests
{
    // 5 Hz (F6): 600 ms -> 3 frames duration, 400 ms -> 2 frames interval.
    private const string Definitions = @"
Upgrade Upgrade_SpySat
  Type = PLAYER
End

Object UpgradeSpy
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpyVisionUpdate ModuleTag_Spy
    NeedsUpgrade = Yes
    TriggeredBy = Upgrade_SpySat
    SelfPoweredDuration = 0
    SpyOnKindof = VEHICLE
  End
End

Object CyclingSpy
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpyVisionUpdate ModuleTag_Spy
    NeedsUpgrade = Yes
    StartsActive = Yes
    SelfPowered = Yes
    SelfPoweredDuration = 600
    SelfPoweredInterval = 400
    SpyOnKindof = VEHICLE
  End
End

Object AlwaysOnSpy
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpyVisionUpdate ModuleTag_Spy
    NeedsUpgrade = Yes
    StartsActive = Yes
    SelfPowered = Yes
    SelfPoweredDuration = 0
    SelfPoweredInterval = 0
    SpyOnKindof = VEHICLE
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x5F4)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static SpyVisionUpdate SpyModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<SpyVisionUpdate>().Single();

    [Fact]
    public void UpgradeGated_InactiveUntilTriggered_ThenStaysOn()
    {
        var game = NewGame();
        var spy = game.SpawnObject("UpgradeSpy", game.CivilianPlayer, Vector3.Zero);
        var module = SpyModuleOf(spy);

        Assert.False(module.IsCurrentlyActive);         // NeedsUpgrade, not yet triggered

        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }
        Assert.False(module.IsCurrentlyActive);          // still no trigger => still off

        var upgrades = new UpgradeSet
        {
            game.AssetStore.Upgrades.GetByName("Upgrade_SpySat"),
        };
        module.TryUpgrade(upgrades);

        Assert.True(module.IsCurrentlyActive);           // duration 0 => on permanently

        for (var i = 0; i < 8; i++)
        {
            game.Step();
        }
        Assert.True(module.IsCurrentlyActive);           // never self-deactivates
    }

    [Fact]
    public void SelfPowered_CyclesOnForDuration_OffForInterval()
    {
        var game = NewGame();
        var spy = game.SpawnObject("CyclingSpy", game.CivilianPlayer, Vector3.Zero);
        var module = SpyModuleOf(spy);

        // StartsActive fires the mux at construction -> active for the 3-frame duration.
        Assert.True(module.IsCurrentlyActive);

        var trajectory = new bool[10];
        for (var i = 0; i < trajectory.Length; i++)
        {
            game.Step();
            trajectory[i] = module.IsCurrentlyActive;
        }

        // Active [start..frame3), off [3..5), active [5..8), off [8..10) - the module must both
        // switch off (duration elapsed) and switch back on (interval elapsed) at least once.
        Assert.Contains(false, trajectory);              // turned itself off
        Assert.Contains(true, trajectory);               // and back on again
        // The off->on re-activation is the interval path, distinct from the initial activation.
        var firstOff = System.Array.IndexOf(trajectory, false);
        Assert.True(trajectory.Skip(firstOff).Contains(true), "expected a re-activation after the off window");
    }

    [Fact]
    public void SelfPowered_ZeroDuration_StaysOnForever()
    {
        var game = NewGame();
        var spy = game.SpawnObject("AlwaysOnSpy", game.CivilianPlayer, Vector3.Zero);
        var module = SpyModuleOf(spy);

        Assert.True(module.IsCurrentlyActive);           // on at construction

        for (var i = 0; i < 12; i++)
        {
            game.Step();
            Assert.True(module.IsCurrentlyActive);       // duration 0 => never turns off
        }
    }

    [Fact]
    public void DisabledEdge_SuspendsThenReArmsOnWake()
    {
        var game = NewGame();
        var spy = game.SpawnObject("AlwaysOnSpy", game.CivilianPlayer, Vector3.Zero);
        var module = SpyModuleOf(spy);

        Assert.True(module.IsCurrentlyActive);

        // Sabotage/EMP edge (SVU-4: engine does not yet drive this; the path itself is live).
        module.OnDisabledEdge(nowDisabled: true);
        Assert.False(module.IsCurrentlyActive);          // suspended immediately

        game.Step();
        Assert.False(module.IsCurrentlyActive);          // stays down while disabled

        // Disable lifted -> re-arm; a following update turns a self-powered module back on.
        module.OnDisabledEdge(nowDisabled: false);
        var reactivated = false;
        for (var i = 0; i < 4 && !reactivated; i++)
        {
            game.Step();
            reactivated = module.IsCurrentlyActive;
        }
        Assert.True(reactivated);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var spy = game.SpawnObject("CyclingSpy", game.CivilianPlayer, Vector3.Zero);
        var live = SpyModuleOf(spy);

        // Drive real state into the module: triggered flag on (StartsActive), a couple of
        // on/off transitions, a live deactivate frame.
        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        var shadowHost = game.SpawnObject("CyclingSpy", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = SpyModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 4);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static bool[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xFEED);
        var spy = game.SpawnObject("CyclingSpy", game.CivilianPlayer, Vector3.Zero);
        var module = SpyModuleOf(spy);

        var trajectory = new bool[14];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk;      // engine-owned, walk-carried (S6)
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            game.Step();
            trajectory[i] = module.IsCurrentlyActive;
        }

        return trajectory;
    }
}
