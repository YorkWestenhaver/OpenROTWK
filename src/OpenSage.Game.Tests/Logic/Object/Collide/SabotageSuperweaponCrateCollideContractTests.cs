// Mocked-game contract tests for the SabotageSuperweaponCrateCollide port (R12): the real
// 'SabotageSuperweaponCrateCollide' INI name must produce a live runtime that gates on
// KindOf (FS_SUPERWEAPON / FS_STRATEGY_CENTER), IsEffectivelyDead, and the ENEMIES
// relationship, then resets every SpecialPowerModule on the victim through the landed
// ResetCountdown() (the GPL startPowerRecharge() equivalent) and retires itself.
//
// The ENEMIES gate is exercised through the IsValidToExecute(other, relationship) overload
// (see the file header on the production module): OpenSage's Team/Player relationship
// dictionaries are currently populated only by save-game load, so no live path exists yet to
// stand up a real ENEMIES relationship between two freshly-spawned HeadlessSimGame objects.

using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Collide;

public class SabotageSuperweaponCrateCollideContractTests
{
    private const string Definitions = @"
SpecialPower TestSuperweaponPower
  Enum = SPECIAL_SCUD_STORM
  ReloadTime = 1000
End

Object TestSuperweapon
  KindOf = STRUCTURE FS_SUPERWEAPON
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpecialPowerModule ModuleTag_SpecialPower
    SpecialPowerTemplate = TestSuperweaponPower
  End
End

Object TestStrategyCenter
  KindOf = STRUCTURE FS_STRATEGY_CENTER
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpecialPowerModule ModuleTag_SpecialPower
    SpecialPowerTemplate = TestSuperweaponPower
  End
End

Object TestOrdinaryStructure
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SpecialPowerModule ModuleTag_SpecialPower
    SpecialPowerTemplate = TestSuperweaponPower
  End
End

Object TestSaboteur
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
  Behavior = SabotageSuperweaponCrateCollide ModuleTag_Sabotage
  End
End
";

    private static (HeadlessSimGame Game, GameObject Saboteur) NewGame()
    {
        var game = new HeadlessSimGame(SageGame.CncGeneralsZeroHour, 0xCA7E);
        game.LoadIniText(Definitions);
        var saboteur = game.SpawnObject("TestSaboteur", game.CivilianPlayer, new Vector3(0, 0, 0));
        return (game, saboteur);
    }

    [Fact]
    public void NonSuperweaponNonStrategyCenterStructure_IsRejected()
    {
        var (game, saboteur) = NewGame();
        var victim = game.SpawnObject("TestOrdinaryStructure", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = saboteur.FindBehavior<SabotageSuperweaponCrateCollide>();

        Assert.False(module.IsValidToExecute(victim, RelationshipType.Enemies));
    }

    [Theory]
    [InlineData(RelationshipType.Allies)]
    [InlineData(RelationshipType.Neutral)]
    public void NonEnemyRelationship_IsRejected(RelationshipType relationship)
    {
        var (game, saboteur) = NewGame();
        var victim = game.SpawnObject("TestSuperweapon", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = saboteur.FindBehavior<SabotageSuperweaponCrateCollide>();

        Assert.False(module.IsValidToExecute(victim, relationship));
    }

    [Fact]
    public void EffectivelyDeadSuperweapon_IsRejected()
    {
        var (game, saboteur) = NewGame();
        var victim = game.SpawnObject("TestSuperweapon", game.CivilianPlayer, new Vector3(10, 0, 0));
        victim.IsEffectivelyDead = true;
        var module = saboteur.FindBehavior<SabotageSuperweaponCrateCollide>();

        Assert.False(module.IsValidToExecute(victim, RelationshipType.Enemies));
    }

    [Fact]
    public void ValidEnemySuperweapon_IsAccepted()
    {
        var (game, saboteur) = NewGame();
        var victim = game.SpawnObject("TestSuperweapon", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = saboteur.FindBehavior<SabotageSuperweaponCrateCollide>();

        Assert.True(module.IsValidToExecute(victim, RelationshipType.Enemies));
    }

    [Fact]
    public void ValidCollision_ResetsAllSpecialPowers()
    {
        var (game, saboteur) = NewGame();
        var victim = game.SpawnObject("TestSuperweapon", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = saboteur.FindBehavior<SabotageSuperweaponCrateCollide>();
        var specialPower = victim.FindBehavior<SpecialPowerModule>();

        // Let the power fully recharge so ResetCountdown() (the startPowerRecharge()
        // equivalent) has an observable effect: sabotage puts it back into recharge.
        for (var i = 0; i < 40 && !specialPower.Ready; i++)
        {
            game.Step();
        }
        Assert.True(specialPower.Ready);

        Assert.True(module.IsValidToExecute(victim, RelationshipType.Enemies));

        var result = module.ExecuteCrateBehavior(victim);

        Assert.True(result);
        Assert.False(specialPower.Ready);
    }

    [Fact]
    public void StrategyCenterVictim_AlsoResetsAllSpecialPowers()
    {
        var (game, saboteur) = NewGame();
        var victim = game.SpawnObject("TestStrategyCenter", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = saboteur.FindBehavior<SabotageSuperweaponCrateCollide>();
        var specialPower = victim.FindBehavior<SpecialPowerModule>();

        for (var i = 0; i < 40 && !specialPower.Ready; i++)
        {
            game.Step();
        }
        Assert.True(specialPower.Ready);

        Assert.True(module.IsValidToExecute(victim, RelationshipType.Enemies));
        Assert.True(module.ExecuteCrateBehavior(victim));
        Assert.False(specialPower.Ready);
    }

    /// <summary>
    /// The real end-to-end OnCollide dispatch (used by live gameplay collisions): with two
    /// freshly-spawned objects, <see cref="GameObject.GetRelationship"/> is Neutral (OpenSage's
    /// Team/Player relationship dictionaries are populated only by save-game load today, see
    /// the file header), so the module's own real-relationship overload correctly refuses to
    /// execute and the saboteur survives - it does not self-destruct on a non-enemy touch.
    /// </summary>
    [Fact]
    public void OnCollide_RealDefaultRelationship_DoesNotExecuteOrDestroySaboteur()
    {
        var (game, saboteur) = NewGame();
        var victim = game.SpawnObject("TestSuperweapon", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = saboteur.FindBehavior<SabotageSuperweaponCrateCollide>();
        var specialPower = victim.FindBehavior<SpecialPowerModule>();
        for (var i = 0; i < 40 && !specialPower.Ready; i++)
        {
            game.Step();
        }
        Assert.True(specialPower.Ready);

        module.OnCollide(victim, victim.Translation, Vector3.UnitZ);

        Assert.False(saboteur.IsDestroyed);
        Assert.True(specialPower.Ready); // untouched: the real relationship gate refused.
    }

    [Fact]
    public void OnCollide_NullOther_DoesNotThrowOrDestroySaboteur()
    {
        var (_, saboteur) = NewGame();
        var module = saboteur.FindBehavior<SabotageSuperweaponCrateCollide>();

        module.OnCollide(null, Vector3.Zero, Vector3.Zero);

        Assert.False(saboteur.IsDestroyed);
    }

    [Fact]
    public void ExecuteCrateBehavior_WithNoSpecialPowerModules_StillSucceeds()
    {
        var (game, saboteur) = NewGame();
        // A superweapon-kinded object with no SpecialPowerModule attached: the reset loop
        // must simply see zero modules, not throw.
        game.LoadIniText(@"
Object TestBareSuperweapon
  KindOf = STRUCTURE FS_SUPERWEAPON
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
");
        var victim = game.SpawnObject("TestBareSuperweapon", game.CivilianPlayer, new Vector3(10, 0, 0));
        var module = saboteur.FindBehavior<SabotageSuperweaponCrateCollide>();

        Assert.Empty(victim.FindBehaviors<SpecialPowerModule>());
        Assert.True(module.ExecuteCrateBehavior(victim));
    }
}
