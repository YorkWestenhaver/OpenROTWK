// Mocked-game contract tests for the BloodthirstyUpdate port (R11 Track B): the sacrifice
// entry point (filter + mutual-BloodthirstyUpdate gate + experience banking), the
// NumToSacrifice budget, and the shadow-copy base test.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class BloodthirstyUpdateContractTests
{
    private const string Definitions = @"
Object BloodthirstyOrc
  KindOf = INFANTRY
  IsTrainable = Yes
  ExperienceValue = 10 20 30 40
  ExperienceRequired = 0 20 40 60
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = BloodthirstyUpdate ModuleTag_Bloodthirsty
    SacrificeFilter = ALL +INFANTRY
    NumToSacrifice = 2
    ExperienceModifier = 2.00
  End
End

Object PlainVictim
  KindOf = INFANTRY
  IsTrainable = Yes
  ExperienceValue = 10 20 30 40
  ExperienceRequired = 0 20 40 60
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, 0xB1D);
        game.LoadIniText(Definitions);
        return game;
    }

    private static BloodthirstyUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<BloodthirstyUpdate>().Single();

    [Fact]
    public void Sacrifice_KillsVictim_AndBanksScaledExperience()
    {
        var game = NewGame();
        var eater = game.SpawnObject("BloodthirstyOrc", game.CivilianPlayer, Vector3.Zero);
        var victim = game.SpawnObject("BloodthirstyOrc", game.CivilianPlayer, new Vector3(10, 0, 0));

        // Victim worth 10 at Regular, ExperienceModifier 2.00 -> 20 experience = Veteran.
        Assert.True(ModuleOf(eater).Sacrifice(victim));
        game.Step();

        Assert.True(victim.IsEffectivelyDead || victim.IsDestroyed);
        Assert.Equal(20, eater.ExperienceTracker.CurrentExperience);
        Assert.Equal(VeterancyLevel.Veteran, eater.ExperienceTracker.VeterancyLevel);
        Assert.Equal(1, ModuleOf(eater).NumSacrificed);
    }

    [Fact]
    public void Victim_WithoutBloodthirstyUpdate_IsRefused()
    {
        var game = NewGame();
        var eater = game.SpawnObject("BloodthirstyOrc", game.CivilianPlayer, Vector3.Zero);
        var victim = game.SpawnObject("PlainVictim", game.CivilianPlayer, new Vector3(10, 0, 0));

        // "To sacrifice or be sacrificed, you must have a BloodthirstyUpdate".
        Assert.False(ModuleOf(eater).Sacrifice(victim));
        Assert.False(victim.IsEffectivelyDead);
        Assert.Equal(0, eater.ExperienceTracker.CurrentExperience);
    }

    [Fact]
    public void NumToSacrifice_CapsTheBudget()
    {
        var game = NewGame();
        var eater = game.SpawnObject("BloodthirstyOrc", game.CivilianPlayer, Vector3.Zero);
        var module = ModuleOf(eater);

        for (var i = 0; i < 3; i++)
        {
            var victim = game.SpawnObject("BloodthirstyOrc", game.CivilianPlayer, new Vector3(10 + i, 0, 0));
            var accepted = module.Sacrifice(victim);
            Assert.Equal(i < 2, accepted);
        }

        Assert.Equal(2, module.NumSacrificed);
        Assert.False(module.CanSacrifice);
    }

    [Fact]
    public void SelfAndDeadVictims_AreRefused()
    {
        var game = NewGame();
        var eater = game.SpawnObject("BloodthirstyOrc", game.CivilianPlayer, Vector3.Zero);
        var victim = game.SpawnObject("BloodthirstyOrc", game.CivilianPlayer, new Vector3(10, 0, 0));
        victim.Kill();

        var module = ModuleOf(eater);
        Assert.False(module.Sacrifice(eater));
        Assert.False(module.Sacrifice(victim));
        Assert.Equal(0, module.NumSacrificed);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var live = ModuleOf(game.SpawnObject("BloodthirstyOrc", game.CivilianPlayer, Vector3.Zero));
        var victim = game.SpawnObject("BloodthirstyOrc", game.CivilianPlayer, new Vector3(10, 0, 0));
        live.Sacrifice(victim);

        var shadow = ModuleOf(game.SpawnObject("BloodthirstyOrc", game.CivilianPlayer, new Vector3(50, 0, 0)));

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
        Assert.Equal(1, shadow.NumSacrificed);
    }
}
