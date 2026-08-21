// Mocked-game contract tests for the EmotionTrackerUpdate partial port (R11 Track B):
// the fear scan edge (AfraidOf match inside FearScanDistance sets EMOTION_AFRAID, absence
// clears it), enemy-relationship gating, and the shadow-copy base test.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class EmotionTrackerUpdateContractTests
{
    // Scan every 1000 ms (5 frames); afraid of MONSTER kinds within 80.
    private const string Definitions = @"
Object FearfulUnit
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = EmotionTrackerUpdate ModuleTag_Emotion
    TauntAndPointUpdateDelay = 1000
    AfraidOf = NONE +MONSTER
    FearScanDistance = 80
  End
End

Object ScaryMonster
  KindOf = MONSTER
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xFEA);
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

    private static void MakeEnemies(Player a, Player b)
    {
        a.AddEnemy(b);
        b.AddEnemy(a);
    }

    private static EmotionTrackerUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<EmotionTrackerUpdate>().Single();

    [Fact]
    public void FearedEnemyInRange_SetsAfraid_AndModelCondition()
    {
        var game = NewGame();
        var unit = game.SpawnObject("FearfulUnit", game.CivilianPlayer, new Vector3(100, 100, 0));
        game.SpawnObject("ScaryMonster", game.PlayerManager.NeutralPlayer, new Vector3(140, 100, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepFrames(game, 6);

        Assert.True(ModuleOf(unit).IsAfraid);
        Assert.True(unit.ModelConditionFlags.Get(ModelConditionFlag.EmotionAfraid));
    }

    [Fact]
    public void NonEnemyMonster_DoesNotFrighten()
    {
        var game = NewGame();
        var unit = game.SpawnObject("FearfulUnit", game.CivilianPlayer, new Vector3(100, 100, 0));
        // Same owner: never an enemy, whatever the kind filter says.
        game.SpawnObject("ScaryMonster", game.CivilianPlayer, new Vector3(140, 100, 0));

        StepFrames(game, 6);

        Assert.False(ModuleOf(unit).IsAfraid);
    }

    [Fact]
    public void FearClears_WhenTheThreatDies()
    {
        var game = NewGame();
        var unit = game.SpawnObject("FearfulUnit", game.CivilianPlayer, new Vector3(100, 100, 0));
        var monster = game.SpawnObject("ScaryMonster", game.PlayerManager.NeutralPlayer, new Vector3(140, 100, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepFrames(game, 6);
        Assert.True(ModuleOf(unit).IsAfraid);

        monster.Kill();
        StepFrames(game, 6);

        Assert.False(ModuleOf(unit).IsAfraid);
        Assert.False(unit.ModelConditionFlags.Get(ModelConditionFlag.EmotionAfraid));
    }

    [Fact]
    public void OutOfScanRange_DoesNotFrighten()
    {
        var game = NewGame();
        var unit = game.SpawnObject("FearfulUnit", game.CivilianPlayer, new Vector3(100, 100, 0));
        game.SpawnObject("ScaryMonster", game.PlayerManager.NeutralPlayer, new Vector3(300, 100, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepFrames(game, 6);

        Assert.False(ModuleOf(unit).IsAfraid);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var liveHost = game.SpawnObject("FearfulUnit", game.CivilianPlayer, new Vector3(100, 100, 0));
        game.SpawnObject("ScaryMonster", game.PlayerManager.NeutralPlayer, new Vector3(140, 100, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);
        StepFrames(game, 6);
        var live = ModuleOf(liveHost);
        Assert.True(live.IsAfraid);

        var shadow = ModuleOf(game.SpawnObject("FearfulUnit", game.CivilianPlayer, new Vector3(400, 400, 0)));

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
