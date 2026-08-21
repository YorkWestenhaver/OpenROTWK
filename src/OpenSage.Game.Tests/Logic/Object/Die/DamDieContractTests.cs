// Mocked-game contract tests for the DamDie port (experiment-round-4 §4.1, DoD item 4):
// one test per INI-configurable branch, each shaped [create -> trigger death -> observable
// effect] through the batch death-trigger helper, plus the shadow-copy base test and a
// mid-behavior save/load continuation.
//
// DamDie's own INI surface is empty (GPL buildFieldParse forwards to DieModuleData and its
// own table is commented out), so "one test per INI branch" here means one test per branch
// of the SHARED Die gate that DamDie is configured through in data - DeathTypes,
// RequiredStatus, ExemptStatus - plus the ungated default, plus the negative controls that
// pin what the module must NOT touch.
//
// Object definitions are parsed from INI text through the real parser, so the S5 parse path
// is on the tested path even though DamDie contributes no quantized field to it.
//
// TEST-DESIGN HAZARD, found the hard way and worth the next Die task's minute: the status
// gate cannot be probed with NO_COLLISIONS. ActiveBody's structure-death path sets
// ObjectStatus.NoCollisions on the dying object itself ("nobody collides with us, ever
// again" - the rubble rule) BEFORE the Die dispatch, so a RequiredStatus/ExemptStatus test
// keyed on it reads as always-satisfied and inverts both branches. CAN_ATTACK is used below
// precisely because nothing in the engine writes it.

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Die;

public class DamDieContractTests
{
    private const string Definitions = @"
Object WaterDam
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = DamDie ModuleTag_Die
  End
End

Object FloodOnlyDam
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = DamDie ModuleTag_Die
    DeathTypes = NONE +FLOODED
  End
End

Object StatusGatedDam
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = DamDie ModuleTag_Die
    RequiredStatus = CAN_ATTACK
  End
End

Object StatusExemptDam
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = DamDie ModuleTag_Die
    ExemptStatus = CAN_ATTACK
  End
End

Object WaterWave
  KindOf = WAVEGUIDE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object Bystander
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xDA3)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    /// <summary>
    /// The map's pre-placed water waves: spawned already disabled, exactly the state the
    /// original's dam maps ship them in (DamDie exists to release them).
    /// </summary>
    private static List<GameObject> SpawnDisabledWaves(HeadlessSimGame game, int count)
    {
        var waves = new List<GameObject>(count);
        for (var i = 0; i < count; i++)
        {
            var wave = game.SpawnObject("WaterWave", game.CivilianPlayer, new Vector3(50f * (i + 1), 0, 0));
            wave.SetDisabled(DisabledType.Default);
            waves.Add(wave);
        }
        return waves;
    }

    private static bool AnyStillDisabled(IEnumerable<GameObject> objects) =>
        objects.Any(o => o.IsDisabledByType(DisabledType.Default));

    private static DamDie DamModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<DamDie>().Single();

    // ---------------------------------------------------------------- branches

    [Fact]
    public void Ungated_DeathReleasesEveryWaveGuideOnTheMap()
    {
        var game = NewGame();
        var waves = SpawnDisabledWaves(game, 3);
        var dam = game.SpawnObject("WaterDam", game.CivilianPlayer, Vector3.Zero);

        Assert.True(waves.All(w => w.IsDisabledByType(DisabledType.Default)));

        PortedModuleTestKit.TriggerDeath(dam);

        Assert.False(AnyStillDisabled(waves));
    }

    [Fact]
    public void SubLethalDamage_DoesNotRelease()
    {
        var game = NewGame();
        var waves = SpawnDisabledWaves(game, 2);
        var dam = game.SpawnObject("WaterDam", game.CivilianPlayer, Vector3.Zero);

        var result = PortedModuleTestKit.ApplyDamage(dam, amount: 100f);

        Assert.False(result.Died);
        Assert.True(waves.All(w => w.IsDisabledByType(DisabledType.Default)));
    }

    [Fact]
    public void NonWaveGuideObjects_AreNeverTouched()
    {
        var game = NewGame();
        var bystander = game.SpawnObject("Bystander", game.CivilianPlayer, new Vector3(20, 0, 0));
        bystander.SetDisabled(DisabledType.Default);
        var dam = game.SpawnObject("WaterDam", game.CivilianPlayer, Vector3.Zero);

        PortedModuleTestKit.TriggerDeath(dam);

        // GPL's loop skips everything that is not KINDOF_WAVEGUIDE - the disabled bit on a
        // plain structure survives the dam's death.
        Assert.True(bystander.IsDisabledByType(DisabledType.Default));
    }

    [Fact]
    public void DeathTypes_ReleasesOnListedType_FiltersOthers()
    {
        // Filtered: NORMAL is not in "NONE +FLOODED".
        var filteredGame = NewGame();
        var filteredWaves = SpawnDisabledWaves(filteredGame, 2);
        var filteredDam = filteredGame.SpawnObject("FloodOnlyDam", filteredGame.CivilianPlayer, Vector3.Zero);

        PortedModuleTestKit.TriggerDeath(filteredDam, DeathType.Normal);

        Assert.True(filteredWaves.All(w => w.IsDisabledByType(DisabledType.Default)));

        // Applicable: FLOODED is listed.
        var floodedGame = NewGame();
        var floodedWaves = SpawnDisabledWaves(floodedGame, 2);
        var floodedDam = floodedGame.SpawnObject("FloodOnlyDam", floodedGame.CivilianPlayer, Vector3.Zero);

        PortedModuleTestKit.TriggerDeath(floodedDam, DeathType.Flooded);

        Assert.False(AnyStillDisabled(floodedWaves));
    }

    [Fact]
    public void RequiredStatus_ReleasesOnlyWhenTheStatusIsSet()
    {
        var withoutGame = NewGame();
        var withoutWaves = SpawnDisabledWaves(withoutGame, 2);
        var withoutDam = withoutGame.SpawnObject("StatusGatedDam", withoutGame.CivilianPlayer, Vector3.Zero);

        PortedModuleTestKit.TriggerDeath(withoutDam);

        Assert.True(withoutWaves.All(w => w.IsDisabledByType(DisabledType.Default)));

        var withGame = NewGame();
        var withWaves = SpawnDisabledWaves(withGame, 2);
        var withDam = withGame.SpawnObject("StatusGatedDam", withGame.CivilianPlayer, Vector3.Zero);
        withDam.SetObjectStatus(ObjectStatus.CanAttack, true);

        PortedModuleTestKit.TriggerDeath(withDam);

        Assert.False(AnyStillDisabled(withWaves));
    }

    [Fact]
    public void ExemptStatus_SuppressesTheReleaseWhileTheStatusIsSet()
    {
        var exemptGame = NewGame();
        var exemptWaves = SpawnDisabledWaves(exemptGame, 2);
        var exemptDam = exemptGame.SpawnObject("StatusExemptDam", exemptGame.CivilianPlayer, Vector3.Zero);
        exemptDam.SetObjectStatus(ObjectStatus.CanAttack, true);

        PortedModuleTestKit.TriggerDeath(exemptDam);

        Assert.True(exemptWaves.All(w => w.IsDisabledByType(DisabledType.Default)));

        var plainGame = NewGame();
        var plainWaves = SpawnDisabledWaves(plainGame, 2);
        var plainDam = plainGame.SpawnObject("StatusExemptDam", plainGame.CivilianPlayer, Vector3.Zero);

        PortedModuleTestKit.TriggerDeath(plainDam);

        Assert.False(AnyStillDisabled(plainWaves));
    }

    [Fact]
    public void Release_IsIdempotent_AcrossSeveralDams()
    {
        // The effect is an idempotent per-object bit clear, so two dams dying in the same
        // world converge on the same state whatever the iteration order - the property that
        // makes the ObjectsAscendingId walk a contract choice rather than a correctness one.
        var game = NewGame();
        var waves = SpawnDisabledWaves(game, 3);
        var first = game.SpawnObject("WaterDam", game.CivilianPlayer, Vector3.Zero);
        var second = game.SpawnObject("WaterDam", game.CivilianPlayer, new Vector3(30, 0, 0));

        PortedModuleTestKit.TriggerDeath(first);
        game.Step();
        PortedModuleTestKit.TriggerDeath(second);

        Assert.False(AnyStillDisabled(waves));
    }

    // ---------------------------------------------------------------- the walk

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        SpawnDisabledWaves(game, 2);
        var dam = game.SpawnObject("WaterDam", game.CivilianPlayer, Vector3.Zero);
        var live = DamModuleOf(dam);

        // Mid-behavior: the dam has taken damage and the world has run frames, but the
        // death has not fired yet - the module is live and its object is still in the walk.
        PortedModuleTestKit.ApplyDamage(dam, amount: 200f);
        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }

        var shadowHost = game.SpawnObject("WaterDam", game.CivilianPlayer, new Vector3(100, 0, 0));
        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, DamModuleOf(shadowHost));

        // And again after the death has fired, before the reap: the post-death module must
        // still round-trip (its walk is state-free, and this is the test that says so).
        PortedModuleTestKit.TriggerDeath(dam);
        var postDeathShadow = game.SpawnObject("WaterDam", game.CivilianPlayer, new Vector3(200, 0, 0));
        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, DamModuleOf(postDeathShadow));
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        // Two identical games, identical script. Game B round-trips the DamDie module state
        // through Save->Load mid-behavior, before the death that matters; if the walk lost
        // or misread anything, B's release trajectory diverges from A's.
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 2);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static int[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xFEED);
        var waves = SpawnDisabledWaves(game, 3);
        var dam = game.SpawnObject("WaterDam", game.CivilianPlayer, Vector3.Zero);
        var module = DamModuleOf(dam);

        PortedModuleTestKit.ApplyDamage(dam, amount: 200f);

        // How many waves are still disabled after each frame: the whole observable output
        // of this module, sampled as a trajectory.
        var trajectory = new int[8];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                PortedModuleTestKit.Load(module, PortedModuleTestKit.Save(module));
            }

            if (i == 5)
            {
                PortedModuleTestKit.TriggerDeath(dam, DeathType.Flooded);
            }

            game.Step();
            trajectory[i] = waves.Count(w => w.IsDisabledByType(DisabledType.Default));
        }

        return trajectory;
    }
}
