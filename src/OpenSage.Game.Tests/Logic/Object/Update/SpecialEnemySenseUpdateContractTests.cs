// Mocked-game unit tests for the SpecialEnemySenseUpdate port (api-freeze-v1 §6 fitness item
// 4): one test per behavior branch, [create -> tick -> observable effect], plus the
// mid-behavior save/load round-trip and the shadow-copy base test. Object definitions are
// parsed from INI text through the real parser, so the quantizing S5 ScanInterval parse and
// the ScanRange Fix64 parse are both on the tested path.
//
// The observable is the SPECIAL_ENEMY_NEAR model-condition flag the module drives on
// rising/falling edges (the client-side output; HeadlessSimGame carries a real GameClient so
// Drawable/ModelConditionFlags are live).
//
// Sleepy-update caveat: SpecialEnemySenseUpdate has a per-frame Update() and participates in
// the sleepy-update queue, so every case below that observes post-spawn state must call
// game.Step() at least twice before the first scan can have run - a freshly spawned module's
// first Update() runs on the object's second HeadlessSimGame.Step(), not the first. Reused
// verbatim from EnemyNearUpdateContractTests.StepPastFirstScan, since the ctor RNG stagger has
// the same [0, ScanInterval] range shape (ScanInterval = 1000ms -> 5 frames at 5 Hz, F6).

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class SpecialEnemySenseUpdateContractTests
{
    // Scanner: 100-unit scan range, scans every 1000 ms -> 5 frames at 5 Hz (F6). Filter
    // carries a KindOf inclusion so the filter gate itself is exercised, not just the
    // relationship gate.
    private const string Definitions = @"
Object Scanner
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpecialEnemySenseUpdate ModuleTag_Sense
    SpecialEnemyFilter = ANY +PIKE
    ScanRange = 100
    ScanInterval = 1000
  End
End

Object ScannerWithVision
  KindOf = INFANTRY
  VisionRange = 1000
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpecialEnemySenseUpdate ModuleTag_Sense
    SpecialEnemyFilter = ANY +PIKE
    ScanRange = 100
    ScanInterval = 1000
  End
End

Object PikeGrunt
  KindOf = INFANTRY PIKE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object SwordGrunt
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object Bunker
  KindOf = STRUCTURE PIKE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x5E5) // "SES"
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static SpecialEnemySenseUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<SpecialEnemySenseUpdate>().Single();

    private static bool SpecialEnemyNearFlag(GameObject obj) =>
        obj.ModelConditionFlags.Get(ModelConditionFlag.SpecialEnemyNear);

    // Makes the two players mutual enemies (the relationship the module reads, mirroring the
    // Player.Enemies set AutoHealBehavior/EnemyNearUpdate read).
    private static void MakeEnemies(Player a, Player b)
    {
        a.AddEnemy(b);
        b.AddEnemy(a);
    }

    /// <summary>
    /// Steps enough frames that a first scan is guaranteed for any ctor stagger in [0, 5]:
    /// the countdown hits zero at frame (stagger + 1) &lt;= 6, plus a margin.
    /// </summary>
    private static void StepPastFirstScan(HeadlessSimGame game)
    {
        for (var i = 0; i < 8; i++)
        {
            game.Step();
        }
    }

    [Fact]
    public void FilterMatchingEnemyInRange_RaisesSpecialEnemyNear()
    {
        var game = NewGame();
        var scanner = game.SpawnObject("Scanner", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("PikeGrunt", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        Assert.False(SpecialEnemyNearFlag(scanner)); // nothing scanned yet
        StepPastFirstScan(game);
        Assert.True(SpecialEnemyNearFlag(scanner));
    }

    [Fact]
    public void FilterNonMatchingEnemyInRange_DoesNotRaise()
    {
        var game = NewGame();
        var scanner = game.SpawnObject("Scanner", game.CivilianPlayer, Vector3.Zero);
        // SwordGrunt lacks PIKE: the filter gate rejects a KindOf-non-matching enemy that
        // EnemyNearUpdate (no filter) would have accepted.
        game.SpawnObject("SwordGrunt", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepPastFirstScan(game);
        Assert.False(SpecialEnemyNearFlag(scanner));
    }

    [Fact]
    public void EnemyOutOfScanRange_DoesNotRaise()
    {
        var game = NewGame();
        var scanner = game.SpawnObject("Scanner", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("PikeGrunt", game.PlayerManager.NeutralPlayer, new Vector3(500, 0, 0)); // > 100
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepPastFirstScan(game);
        Assert.False(SpecialEnemyNearFlag(scanner));
    }

    [Fact]
    public void EnemyWithinDefaultVisionButOutsideScanRange_DoesNotRaise()
    {
        // Discriminating case: ScannerWithVision has VisionRange = 1000 but ScanRange = 100.
        // A wrong implementation reading GameObject.VisionRange instead of _data.ScanRange
        // would wrongly raise the flag here at distance 500.
        var game = NewGame();
        var scanner = game.SpawnObject("ScannerWithVision", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("PikeGrunt", game.PlayerManager.NeutralPlayer, new Vector3(500, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepPastFirstScan(game);
        Assert.False(SpecialEnemyNearFlag(scanner));
    }

    [Fact]
    public void NonEnemyFilterMatchInRange_DoesNotRaise()
    {
        var game = NewGame();
        var scanner = game.SpawnObject("Scanner", game.CivilianPlayer, Vector3.Zero);
        // Same-owner (and no enemy relationship): the hardcoded enemy-relationship gate still
        // applies even though the filter itself has no ENEMIES keyword to drive it.
        game.SpawnObject("PikeGrunt", game.CivilianPlayer, new Vector3(10, 0, 0));

        StepPastFirstScan(game);
        Assert.False(SpecialEnemyNearFlag(scanner));
    }

    [Fact]
    public void FilterMatchingEnemyStructureInRange_Raises()
    {
        // F-SES-2 regression pin: unlike EnemyNearUpdate's hardcoded structure reject, a
        // filter-matching STRUCTURE enemy DOES trip the sense here - the filter alone decides
        // KindOf eligibility. This case would fail if a future edit carried
        // EnemyNearUpdate.IsVisibleEnemy's structure exclusion over by copy-paste.
        var game = NewGame();
        var scanner = game.SpawnObject("Scanner", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Bunker", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepPastFirstScan(game);
        Assert.True(SpecialEnemyNearFlag(scanner));
    }

    [Fact]
    public void EnemyDiesInRange_FallingEdgeClears()
    {
        var game = NewGame();
        var scanner = game.SpawnObject("Scanner", game.CivilianPlayer, Vector3.Zero);
        var enemy = game.SpawnObject("PikeGrunt", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepPastFirstScan(game);
        Assert.True(SpecialEnemyNearFlag(scanner)); // rising edge fired

        // Kill the enemy: the next scan finds no matching enemy and clears the flag.
        enemy.Kill();
        game.Step(); // reap the destroyed object
        for (var i = 0; i < 7; i++) // guarantee another scan window
        {
            game.Step();
        }
        Assert.False(SpecialEnemyNearFlag(scanner));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var scanner = game.SpawnObject("Scanner", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("PikeGrunt", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        // Drive real state into the module: some frames of countdown + a scan that set enemyNear.
        StepPastFirstScan(game);
        var live = ModuleOf(scanner);

        // The shadow is the same class over the same data on a second object, in a different
        // (untouched) state; Load must overwrite everything the walk carries.
        var shadowHost = game.SpawnObject("Scanner", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        // Two identical games, identical script. Game B round-trips the module state (and the
        // engine-owned wake frame, S6) through Save->Load mid-behavior; if the load path lost
        // or misread anything (the scan countdown or the enemyNear flag), B's flag trajectory
        // diverges from A's.
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 4);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static int[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xF00D);
        var scanner = game.SpawnObject("Scanner", game.CivilianPlayer, Vector3.Zero);
        var enemy = game.SpawnObject("PikeGrunt", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);
        var module = ModuleOf(scanner);

        var trajectory = new int[14];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk; // engine-owned, walk-carried (S6)
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            // Move the enemy out of and back into range mid-run so the flag actually toggles,
            // exercising both edges through the round-trip.
            enemy.UpdateTransform(i == 6 ? new Vector3(500, 0, 0) : new Vector3(10, 0, 0));
            enemy.UpdateColliders();

            game.Step();
            trajectory[i] = SpecialEnemyNearFlag(scanner) ? 1 : 0;
        }

        return trajectory;
    }
}
