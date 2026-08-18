// Unit tests for the Die-batch death-trigger helper (experiment-round-4 §4.1: "Die modules
// need a death-trigger helper - build it once for the batch"). These test the HELPER, not any
// Die port: they prove that PortedModuleTestKit.TriggerDeath really drives GameObject.OnDie
// over an object's real Die modules, that the death type it carries is the one the Die
// filters see, and that sub-lethal damage does not fire them.
//
// The observable effect used throughout is DestroyDie (the legacy module, unported at the time
// of writing): it calls GameLogic.DestroyObject, so "the Die module ran" is visible as
// GameObject.IsDestroyed without needing a test-only module registered in the module factory.

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Die;

public class DeathTriggerKitTests
{
    private const string Definitions = @"
Object DieTestGrunt
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = DestroyDie ModuleTag_Die
  End
End

Object DieTestBurnOnly
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = DestroyDie ModuleTag_Die
    DeathTypes = NONE +BURNED
  End
End

Object DieTestNoDieModule
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: 0xD1Eu);
        game.LoadIniText(Definitions);
        return game;
    }

    [Fact]
    public void TriggerDeath_KillsTheObjectAndRunsItsDieModules()
    {
        var game = NewGame();
        var (victim, result) = PortedModuleTestKit.SpawnAndKill(
            game, "DieTestGrunt", game.CivilianPlayer, new Vector3(0, 0, 0));

        Assert.Equal(100f, result.HealthBefore);
        Assert.Equal(0f, result.HealthAfter);
        Assert.True(result.Died);

        // DestroyDie ran: the object left the world through GameLogic.DestroyObject.
        Assert.True(result.Destroyed);
        Assert.True(victim.IsDestroyed);
    }

    [Fact]
    public void SubLethalDamage_DoesNotRunDieModules()
    {
        var game = NewGame();
        var victim = game.SpawnObject("DieTestGrunt", game.CivilianPlayer, new Vector3(0, 0, 0));

        var result = PortedModuleTestKit.ApplyDamage(victim, amount: 10f);

        Assert.False(result.Died);
        Assert.False(result.Destroyed);
        Assert.False(victim.IsDestroyed);
        Assert.Equal(90f, result.HealthAfter);
    }

    [Fact]
    public void DeathTypeReachesTheDieFilter()
    {
        // DeathTypes = NONE +BURNED, so a Normal death must not fire the module...
        var game = NewGame();
        var survivor = game.SpawnObject("DieTestBurnOnly", game.CivilianPlayer, new Vector3(0, 0, 0));
        var normal = PortedModuleTestKit.TriggerDeath(survivor, DeathType.Normal);
        Assert.True(normal.Died);
        Assert.False(survivor.IsDestroyed);

        // ...and a Burned death must.
        var burned = game.SpawnObject("DieTestBurnOnly", game.CivilianPlayer, new Vector3(10, 0, 0));
        var result = PortedModuleTestKit.TriggerDeath(burned, DeathType.Burned);
        Assert.True(result.Died);
        Assert.True(burned.IsDestroyed);
    }

    [Fact]
    public void DamageTypeIsCarriedToTheBody()
    {
        // Healing is a damage TYPE, and ActiveBody routes it to healing, not damage: proof
        // that the helper's damageType argument reaches the body's dispatch rather than being
        // swallowed. (A Die task uses this to kill through armor with Unresistable.)
        var game = NewGame();
        var victim = game.SpawnObject("DieTestGrunt", game.CivilianPlayer, new Vector3(0, 0, 0));
        PortedModuleTestKit.ApplyDamage(victim, amount: 40f);
        Assert.Equal(60f, victim.BodyModule.Health);

        var healed = PortedModuleTestKit.ApplyDamage(
            victim, amount: 25f, DamageType.Healing, DeathType.None);

        Assert.False(healed.Died);
        Assert.Equal(85f, victim.BodyModule.Health);
    }

    [Fact]
    public void DeathFiresOnlyOnce()
    {
        // The >0 -> <=0 crossing is what OnDie hangs off, so a second lethal blow on a corpse
        // must not re-run the Die modules. A Die port that counts (CreateObjectDie spawning
        // twice, CreateCrateDie drawing twice) depends on this.
        var game = NewGame();
        var victim = game.SpawnObject("DieTestNoDieModule", game.CivilianPlayer, new Vector3(0, 0, 0));

        var first = PortedModuleTestKit.TriggerDeath(victim);
        Assert.True(first.Died);

        var second = PortedModuleTestKit.ApplyDamage(victim, amount: 500f);
        Assert.False(second.Died);
        Assert.Equal(0f, second.HealthBefore);
    }

    [Fact]
    public void DamageSourceIsCarriedIntoTheDeath()
    {
        // CrushDie / EjectPilotDie / CreateObjectDie all read damageInput.SourceID, so the
        // helper must let a test name the killer.
        var game = NewGame();
        var killer = game.SpawnObject("DieTestGrunt", game.CivilianPlayer, new Vector3(50, 0, 0));
        var victim = game.SpawnObject("DieTestNoDieModule", game.CivilianPlayer, new Vector3(0, 0, 0));

        PortedModuleTestKit.TriggerDeath(victim, DeathType.Normal, DamageType.Unresistable, killer);

        Assert.Equal(killer.Id, victim.BodyModule.LastDamageInfo.Value.Request.SourceID);
    }
}
