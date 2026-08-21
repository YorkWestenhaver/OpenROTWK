// Mocked-game unit tests for the EnemyNearUpdate port (api-freeze-v1 §6 fitness item 4): one
// test per behavior branch, [create -> tick -> observable effect], plus the mid-behavior
// save/load round-trip and the shadow-copy base test. Object definitions are parsed from INI
// text through the real parser, so the quantizing S5 ScanDelayTime parse is on the tested path.
//
// The observable is the ENEMY_NEAR model-condition flag the module drives on rising/falling
// edges (the client-side output; HeadlessSimGame carries a real GameClient so Drawable/
// ModelConditionFlags are live).

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class EnemyNearUpdateContractTests
{
    // Scanner: 100-unit vision, scans every 1000 ms -> 5 frames at 5 Hz (F6).
    private const string Definitions = @"
Object Scanner
  KindOf = INFANTRY
  VisionRange = 100
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = EnemyNearUpdate ModuleTag_Enemy
    ScanDelayTime = 1000
  End
End

Object Grunt
  KindOf = INFANTRY
  VisionRange = 100
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object Bunker
  KindOf = STRUCTURE
  VisionRange = 100
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xE00)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static EnemyNearUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<EnemyNearUpdate>().Single();

    private static bool EnemyNearFlag(GameObject obj) =>
        obj.ModelConditionFlags.Get(ModelConditionFlag.EnemyNear);

    // Makes the two players mutual enemies (the relationship the module reads, mirroring the
    // Player.Enemies set AutoHealBehavior reads for allies).
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
    public void EnemyInVisionRange_RaisesEnemyNear()
    {
        var game = NewGame();
        var scanner = game.SpawnObject("Scanner", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        Assert.False(EnemyNearFlag(scanner));   // nothing scanned yet
        StepPastFirstScan(game);
        Assert.True(EnemyNearFlag(scanner));
    }

    [Fact]
    public void EnemyOutOfVisionRange_DoesNotRaise()
    {
        var game = NewGame();
        var scanner = game.SpawnObject("Scanner", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(500, 0, 0)); // > 100
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepPastFirstScan(game);
        Assert.False(EnemyNearFlag(scanner));
    }

    [Fact]
    public void NonEnemyInRange_DoesNotRaise()
    {
        var game = NewGame();
        var scanner = game.SpawnObject("Scanner", game.CivilianPlayer, Vector3.Zero);
        // Same-owner (and no enemy relationship): an ally is never "enemy near".
        game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(10, 0, 0));

        StepPastFirstScan(game);
        Assert.False(EnemyNearFlag(scanner));
    }

    [Fact]
    public void EnemyBuildingInRange_IsRejected()
    {
        var game = NewGame();
        var scanner = game.SpawnObject("Scanner", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Bunker", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        // GPL filterRejectBuildings under CAN_SEE: a STRUCTURE enemy does not trip EnemyNear.
        StepPastFirstScan(game);
        Assert.False(EnemyNearFlag(scanner));
    }

    [Fact]
    public void EnemyDiesInRange_FallingEdgeClearsEnemyNear()
    {
        var game = NewGame();
        var scanner = game.SpawnObject("Scanner", game.CivilianPlayer, Vector3.Zero);
        var enemy = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepPastFirstScan(game);
        Assert.True(EnemyNearFlag(scanner));    // rising edge fired

        // Kill the enemy: the next scan finds no live enemy and clears the flag.
        enemy.Kill();
        game.Step();                            // reap the destroyed object
        for (var i = 0; i < 7; i++)             // guarantee another scan window
        {
            game.Step();
        }
        Assert.False(EnemyNearFlag(scanner));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var scanner = game.SpawnObject("Scanner", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
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
        var enemy = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);
        var module = ModuleOf(scanner);

        var trajectory = new int[14];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk;     // engine-owned, walk-carried (S6)
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            // Move the enemy out of and back into range mid-run so the flag actually toggles,
            // exercising both edges through the round-trip.
            enemy.UpdateTransform(i == 6 ? new Vector3(500, 0, 0) : new Vector3(10, 0, 0));
            enemy.UpdateColliders();

            game.Step();
            trajectory[i] = EnemyNearFlag(scanner) ? 1 : 0;
        }

        return trajectory;
    }
}
