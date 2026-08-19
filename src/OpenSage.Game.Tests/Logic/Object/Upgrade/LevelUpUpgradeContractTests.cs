// Mocked-game contract tests for the LevelUpUpgrade port (R11 Track B): the triggered
// level grant, the LevelCap clamp, idempotence via the shared upgrade mux, and the
// shadow-copy base test. Definitions parse from INI text through the real parser.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Upgrade;

public class LevelUpUpgradeContractTests
{
    private const string Definitions = @"
Upgrade Upgrade_BasicTraining
  Type = PLAYER
End

Object TrainableUnit
  KindOf = INFANTRY
  IsTrainable = Yes
  ExperienceValue = 10 20 30 40
  ExperienceRequired = 0 100 200 300
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = LevelUpUpgrade ModuleTag_BasicTraining
    TriggeredBy = Upgrade_BasicTraining
    LevelsToGain = 1
    LevelCap = 2
  End
End

Object BigGainUnit
  KindOf = INFANTRY
  IsTrainable = Yes
  ExperienceValue = 10 20 30 40
  ExperienceRequired = 0 100 200 300
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = LevelUpUpgrade ModuleTag_BigGain
    TriggeredBy = Upgrade_BasicTraining
    LevelsToGain = 3
    LevelCap = 2
  End
End
";

    private static HeadlessSimGame NewGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0x1E7);
        game.LoadIniText(Definitions);
        return game;
    }

    private static UpgradeSet TrainingSet(HeadlessSimGame game) =>
        new UpgradeSet { game.AssetStore.Upgrades.GetByName("Upgrade_BasicTraining") };

    private static LevelUpUpgrade ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<LevelUpUpgrade>().Single();

    [Fact]
    public void Triggered_GainsOneLevel()
    {
        var game = NewGame();
        var unit = game.SpawnObject("TrainableUnit", game.CivilianPlayer, Vector3.Zero);
        Assert.Equal(VeterancyLevel.Regular, unit.ExperienceTracker.VeterancyLevel);

        ModuleOf(unit).TryUpgrade(TrainingSet(game));

        Assert.Equal(VeterancyLevel.Veteran, unit.ExperienceTracker.VeterancyLevel);
    }

    [Fact]
    public void LevelCap_ClampsTheGain()
    {
        var game = NewGame();
        var unit = game.SpawnObject("BigGainUnit", game.CivilianPlayer, Vector3.Zero);

        // LevelsToGain 3 would reach Heroic; LevelCap 2 stops at the second level (Veteran).
        ModuleOf(unit).TryUpgrade(TrainingSet(game));

        Assert.Equal(VeterancyLevel.Veteran, unit.ExperienceTracker.VeterancyLevel);
    }

    [Fact]
    public void SecondTrigger_IsIdempotent()
    {
        var game = NewGame();
        var unit = game.SpawnObject("TrainableUnit", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(unit);
        var upgrades = TrainingSet(game);

        module.TryUpgrade(upgrades);
        module.TryUpgrade(upgrades);

        Assert.Equal(VeterancyLevel.Veteran, unit.ExperienceTracker.VeterancyLevel);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var liveHost = game.SpawnObject("TrainableUnit", game.CivilianPlayer, Vector3.Zero);
        var live = ModuleOf(liveHost);
        live.TryUpgrade(TrainingSet(game));

        var shadowHost = game.SpawnObject("TrainableUnit", game.CivilianPlayer, new Vector3(50, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
