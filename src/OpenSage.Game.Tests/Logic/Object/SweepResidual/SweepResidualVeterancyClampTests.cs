// R15 L1-11 (sweep ratchet), residual crash class da4d89eb:
// "ActiveBody.OnVeterancyLevelChanged / ArgumentOutOfRangeException (Parameter 'newLevel')",
// 2 of the 9 stage-A failures in the frozen 20-map AotR sweep at main 9bde4556
// ("map good redhorn" and "map sp good blue mountains", both on CampaignCelebrimbor).
//
// Root cause: the engine models four veterancy levels (VeterancyLevel.Regular..Heroic) and
// sizes every per-level table from that enum - GameData.HealthBonus, ObjectDefinition's
// ExperienceRequired/ExperienceValue (VeterancyValues), and the promotion-sound and armor-set
// switches in ActiveBody. BFME2/AotR content is not limited to four: an ExperienceLevelCreate
// block may declare LevelToGrant above 3, and ExperienceLevelCreateBehavior.OnCreate casts it
// straight to the enum (`GameObject.Rank = (VeterancyLevel)_moduleData.LevelToGrant`). The
// out-of-enum value reached ActiveBody's promotion-sound switch, whose default arm threw, and
// the exception propagated out of Scene3D.LoadObjects and terminated the process during map
// load - before the sim loop ever started.
//
// Fixed behavior asserted here: ExperienceTracker clamps an unsupported level into the
// supported range (reporting the content gap once), so the object promotes to Heroic and the
// map keeps loading. Widening VeterancyLevel itself is a separate, much larger port.

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.SweepResidual;

public class SweepResidualVeterancyClampTests
{
    private const string Definitions = @"
Upgrade Upgrade_Veterancy_VETERAN
  Type = OBJECT
End

Upgrade Upgrade_Veterancy_ELITE
  Type = OBJECT
End

Upgrade Upgrade_Veterancy_HEROIC
  Type = OBJECT
End

; The AotR shape of the crash: a rank the engine's four-level model cannot represent.
Object SweepOverRankedHero
  KindOf = INFANTRY SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ExperienceLevelCreate ModuleTag_Rank
    LevelToGrant = 7
  End
End

; Control: a rank the engine does model.
Object SweepHeroicHero
  KindOf = INFANTRY SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = ExperienceLevelCreate ModuleTag_Rank
    LevelToGrant = 3
  End
End
";

    private static HeadlessSimGame NewGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xB17E);
        game.LoadIniText(Definitions);
        return game;
    }

    [Theory]
    [InlineData(VeterancyLevel.Regular, true)]
    [InlineData(VeterancyLevel.Veteran, true)]
    [InlineData(VeterancyLevel.Elite, true)]
    [InlineData(VeterancyLevel.Heroic, true)]
    [InlineData((VeterancyLevel)4, false)]
    [InlineData((VeterancyLevel)7, false)]
    [InlineData((VeterancyLevel)(-1), false)]
    public void IsSupported_TracksTheFourModelledLevels(VeterancyLevel level, bool expected)
    {
        Assert.Equal(expected, VeterancyLevelSupport.IsSupported(level));
    }

    [Theory]
    [InlineData((VeterancyLevel)4, VeterancyLevel.Heroic)]
    [InlineData((VeterancyLevel)7, VeterancyLevel.Heroic)]
    [InlineData((VeterancyLevel)99, VeterancyLevel.Heroic)]
    [InlineData((VeterancyLevel)(-1), VeterancyLevel.Regular)]
    [InlineData(VeterancyLevel.Elite, VeterancyLevel.Elite)]
    public void Clamp_BringsUnsupportedLevelsIntoRange(VeterancyLevel requested, VeterancyLevel expected)
    {
        Assert.Equal(expected, VeterancyLevelSupport.Clamp(requested));
    }

    [Fact]
    public void ExperienceLevelCreate_AboveHeroic_DoesNotThrow_AndPromotesToHeroic()
    {
        var game = NewGame();

        // The regression: this used to throw ArgumentOutOfRangeException out of
        // ActiveBody.OnVeterancyLevelChanged, during GameLogic.CreateObject's OnCreate pass.
        var hero = game.SpawnObject("SweepOverRankedHero", game.CivilianPlayer, Vector3.Zero);

        Assert.Equal(VeterancyLevel.Heroic, hero.Rank);
    }

    [Fact]
    public void ExperienceLevelCreate_AboveHeroic_LeavesTheObjectAliveAndUsable()
    {
        var game = NewGame();

        var hero = game.SpawnObject("SweepOverRankedHero", game.CivilianPlayer, Vector3.Zero);

        // Degraded, not fatal: the object exists, so map load continues past it.
        Assert.Contains(hero, game.GameLogic.Objects);
    }

    [Fact]
    public void ExperienceLevelCreate_AtHeroic_IsUnaffectedByTheClamp()
    {
        var game = NewGame();

        var hero = game.SpawnObject("SweepHeroicHero", game.CivilianPlayer, Vector3.Zero);

        Assert.Equal(VeterancyLevel.Heroic, hero.Rank);
    }
}
