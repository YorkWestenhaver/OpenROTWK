// Mocked-game unit tests for the DamageFieldUpdate port (see
// bfme2-workbench/research/modules-r13/specs/DamageFieldUpdateModuleData.md §4 for the full
// test plan this file implements). Object/Weapon/Upgrade definitions are parsed from INI text
// through the real parser, so the quantizing S5 parses (Radius -> Fix64, WeaponName ->
// LazyAssetReference<WeaponTemplate>) are on the tested path.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Mathematics;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class DamageFieldUpdateContractTests
{
    // 5 Hz (F6): DelayBetweenShots 400ms -> 2 frames.
    private const string Definitions = @"
Upgrade Upgrade_Spines
  Type = OBJECT
End

Weapon SpinesWeapon
  AttackRange       = 10
  DelayBetweenShots = 400
  DamageNugget
    Damage     = 20
    Radius     = 150
    DamageType = FORCE
    DeathType  = NORMAL
  End
End

Weapon NoDamageSpinesWeapon
  AttackRange       = 10
  DelayBetweenShots = 400
End

Object SpikyFortress
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 5000
  End
  Behavior = DamageFieldUpdate ModuleTag_Field
    Radius          = 100
    ObjectFilter    = ALL ENEMIES
    RequiredUpgrade = Upgrade_Spines
    FireWeaponNugget
      WeaponName = SpinesWeapon
      FireDelay  = 0
      OneShot    = No
    End
  End
End

Object InfantryOnlyFortress
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 5000
  End
  Behavior = DamageFieldUpdate ModuleTag_Field
    Radius          = 100
    ObjectFilter    = ENEMIES +INFANTRY
    RequiredUpgrade = Upgrade_Spines
    FireWeaponNugget
      WeaponName = SpinesWeapon
      FireDelay  = 0
      OneShot    = No
    End
  End
End

Object NoDamageNuggetFortress
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 5000
  End
  Behavior = DamageFieldUpdate ModuleTag_Field
    Radius          = 100
    ObjectFilter    = ALL ENEMIES
    RequiredUpgrade = Upgrade_Spines
    FireWeaponNugget
      WeaponName = NoDamageSpinesWeapon
      FireDelay  = 0
      OneShot    = No
    End
  End
End

Object Grunt
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
End

Object Truck
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xFEED)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static DamageFieldUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<DamageFieldUpdate>().Single();

    private static ActiveBody BodyOf(GameObject obj) =>
        Assert.IsType<ActiveBody>(obj.BodyModule, exactMatch: false);

    private static void MakeEnemies(Player a, Player b)
    {
        a.AddEnemy(b);
        b.AddEnemy(a);
    }

    private static void TriggerUpgrade(HeadlessSimGame game, DamageFieldUpdate module)
    {
        module.TryUpgrade(new UpgradeSet { game.AssetStore.Upgrades.GetByName("Upgrade_Spines") });
    }

    // ------------------------------------------------------------------ 1. upgrade gate off

    [Fact]
    public void UpgradeGate_NotTriggered_NoDamage()
    {
        var game = NewGame();
        var fortress = game.SpawnObject("SpikyFortress", game.CivilianPlayer, Vector3.Zero);
        var grunt = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        var body = BodyOf(grunt);
        var startingHealth = body.DamageCore.CurrentHealth;

        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }

        Assert.Equal(startingHealth, body.DamageCore.CurrentHealth);
        Assert.False(ModuleOf(fortress).Triggered);
    }

    // ------------------------------------------------------------------ 2. upgrade gate on, in-radius enemy damaged

    [Fact]
    public void UpgradeGate_Triggered_InRadiusEnemy_TakesDamage()
    {
        var game = NewGame();
        var fortress = game.SpawnObject("SpikyFortress", game.CivilianPlayer, Vector3.Zero);
        var grunt = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        var module = ModuleOf(fortress);
        TriggerUpgrade(game, module);

        var body = BodyOf(grunt);
        var startingHealth = body.DamageCore.CurrentHealth;

        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }

        var damageDealt = startingHealth - body.DamageCore.CurrentHealth;
        Assert.True(damageDealt > Fix64.Zero, "expected the in-radius enemy to take damage");
        Assert.Equal(Fix64.Zero, damageDealt % Fix(20));
    }

    // ------------------------------------------------------------------ 3. out-of-radius enemy takes none

    [Fact]
    public void OutOfRadiusEnemy_TakesNoDamage()
    {
        var game = NewGame();
        var fortress = game.SpawnObject("SpikyFortress", game.CivilianPlayer, Vector3.Zero);
        // AttackRange = 10 on the weapon must NOT be what gates this - only the module's own
        // Radius = 100.
        var farGrunt = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(150, 0, 0));
        var nearGrunt = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        var module = ModuleOf(fortress);
        TriggerUpgrade(game, module);

        var farBody = BodyOf(farGrunt);
        var nearBody = BodyOf(nearGrunt);
        var farStartingHealth = farBody.DamageCore.CurrentHealth;
        var nearStartingHealth = nearBody.DamageCore.CurrentHealth;

        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }

        Assert.Equal(farStartingHealth, farBody.DamageCore.CurrentHealth);
        // Prove the field is actually live this run.
        Assert.True(nearBody.DamageCore.CurrentHealth < nearStartingHealth);
    }

    // ------------------------------------------------------------------ 4. ObjectFilter-rejected candidate takes none

    [Fact]
    public void FilterRejectedCandidate_TakesNoDamage()
    {
        var game = NewGame();
        var fortress = game.SpawnObject("InfantryOnlyFortress", game.CivilianPlayer, Vector3.Zero);
        var truck = game.SpawnObject("Truck", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        var grunt = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(55, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        var module = ModuleOf(fortress);
        TriggerUpgrade(game, module);

        var truckBody = BodyOf(truck);
        var gruntBody = BodyOf(grunt);
        var truckStartingHealth = truckBody.DamageCore.CurrentHealth;
        var gruntStartingHealth = gruntBody.DamageCore.CurrentHealth;

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        Assert.Equal(truckStartingHealth, truckBody.DamageCore.CurrentHealth);
        Assert.True(gruntBody.DamageCore.CurrentHealth < gruntStartingHealth);
    }

    // ------------------------------------------------------------------ 5. allied/neutral/same-player take none

    [Fact]
    public void AlliedAndNeutralObjectsTakeNoDamage_DespiteAllInFilter()
    {
        // Regression test for the ObjectFilter relationship-bit hazard (spec §2.4): a naive
        // ObjectFilter.Matches-only implementation would damage its own army and allies.
        var game = NewGame();
        var fortress = game.SpawnObject("SpikyFortress", game.CivilianPlayer, Vector3.Zero);

        var enemyGrunt = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));

        // Standalone Player instances (not registered with PlayerManager, which only ever
        // stands up the two map players): AddAlly/AddEnemy operate on the Player's own
        // relationship sets, so this is sufficient to exercise the relationship gate.
        var alliedPlayer = new Player(10, null, new ColorRgb(0, 255, 0), game);
        var alliedGrunt = game.SpawnObject("Grunt", alliedPlayer, new Vector3(55, 0, 0));
        game.CivilianPlayer.AddAlly(alliedPlayer);
        alliedPlayer.AddAlly(game.CivilianPlayer);

        var samePlayerGrunt = game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(60, 0, 0));

        var thirdPlayer = new Player(11, null, new ColorRgb(0, 0, 255), game);
        var thirdPartyGrunt = game.SpawnObject("Grunt", thirdPlayer, new Vector3(65, 0, 0));

        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        var module = ModuleOf(fortress);
        TriggerUpgrade(game, module);

        var enemyBody = BodyOf(enemyGrunt);
        var alliedBody = BodyOf(alliedGrunt);
        var samePlayerBody = BodyOf(samePlayerGrunt);
        var thirdPartyBody = BodyOf(thirdPartyGrunt);

        var enemyStarting = enemyBody.DamageCore.CurrentHealth;
        var alliedStarting = alliedBody.DamageCore.CurrentHealth;
        var samePlayerStarting = samePlayerBody.DamageCore.CurrentHealth;
        var thirdPartyStarting = thirdPartyBody.DamageCore.CurrentHealth;

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        Assert.True(enemyBody.DamageCore.CurrentHealth < enemyStarting);
        Assert.Equal(alliedStarting, alliedBody.DamageCore.CurrentHealth);
        Assert.Equal(samePlayerStarting, samePlayerBody.DamageCore.CurrentHealth);
        Assert.Equal(thirdPartyStarting, thirdPartyBody.DamageCore.CurrentHealth);
    }

    // ------------------------------------------------------------------ 6. self is never damaged

    [Fact]
    public void SelfIsNeverDamaged()
    {
        var game = NewGame();
        var fortress = game.SpawnObject("SpikyFortress", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        var module = ModuleOf(fortress);
        TriggerUpgrade(game, module);

        var fortressBody = BodyOf(fortress);
        var startingHealth = fortressBody.DamageCore.CurrentHealth;

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        Assert.Equal(startingHealth, fortressBody.DamageCore.CurrentHealth);
    }

    // ------------------------------------------------------------------ 7. exact cadence + NextPulseFrame advance

    [Fact]
    public void Cadence_MatchesWeaponDelayBetweenShots()
    {
        var game = NewGame();
        var fortress = game.SpawnObject("SpikyFortress", game.CivilianPlayer, Vector3.Zero);
        var grunt = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        var module = ModuleOf(fortress);
        TriggerUpgrade(game, module);

        var body = BodyOf(grunt);
        var startingHealth = body.DamageCore.CurrentHealth;

        var previousNextPulseFrame = module.NextPulseFrame;
        var pulses = 0;

        for (var i = 0; i < 9; i++)
        {
            var healthBefore = body.DamageCore.CurrentHealth;
            game.Step();
            var healthAfter = body.DamageCore.CurrentHealth;

            if (healthAfter < healthBefore)
            {
                pulses++;
                Assert.Equal(Fix(20), healthBefore - healthAfter);
                // NextPulseFrame advances by exactly 2 (400ms at 5 Hz) per pulse.
                Assert.Equal(previousNextPulseFrame + new LogicFrameSpan(2), module.NextPulseFrame);
                previousNextPulseFrame = module.NextPulseFrame;
            }
        }

        Assert.True(pulses >= 3, $"expected several pulses over 9 frames at a 2-frame cadence, got {pulses}");
        Assert.Equal(Fix(20) * pulses, startingHealth - body.DamageCore.CurrentHealth);
    }

    // ------------------------------------------------------------------ 8. all matching candidates damaged per pulse

    [Fact]
    public void MultipleCandidates_AllMatchingTakeDamageInOnePulse()
    {
        var game = NewGame();
        var fortress = game.SpawnObject("SpikyFortress", game.CivilianPlayer, Vector3.Zero);
        var a = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(30, 0, 0));
        var b = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(40, 0, 0));
        var c = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        var module = ModuleOf(fortress);
        TriggerUpgrade(game, module);

        var bodyA = BodyOf(a);
        var bodyB = BodyOf(b);
        var bodyC = BodyOf(c);
        var startingHealth = bodyA.DamageCore.CurrentHealth;

        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }

        var lostA = startingHealth - bodyA.DamageCore.CurrentHealth;
        var lostB = startingHealth - bodyB.DamageCore.CurrentHealth;
        var lostC = startingHealth - bodyC.DamageCore.CurrentHealth;

        Assert.True(lostA > Fix64.Zero);
        Assert.Equal(lostA, lostB);
        Assert.Equal(lostA, lostC);
    }

    // ------------------------------------------------------------------ 9. weapon with no DamageNugget pulses without damage

    [Fact]
    public void WeaponWithNoDamageNugget_PulsesWithoutDamage()
    {
        var game = NewGame();
        var fortress = game.SpawnObject("NoDamageNuggetFortress", game.CivilianPlayer, Vector3.Zero);
        var grunt = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        var module = ModuleOf(fortress);
        TriggerUpgrade(game, module);

        var body = BodyOf(grunt);
        var startingHealth = body.DamageCore.CurrentHealth;

        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }

        Assert.Equal(startingHealth, body.DamageCore.CurrentHealth);
    }

    // ------------------------------------------------------------------ 10. dead candidates skipped

    [Fact]
    public void DeadOrDestroyedCandidatesAreSkipped()
    {
        var game = NewGame();
        var fortress = game.SpawnObject("SpikyFortress", game.CivilianPlayer, Vector3.Zero);
        var corpse = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        var alive = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(55, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        corpse.IsEffectivelyDead = true;

        var module = ModuleOf(fortress);
        TriggerUpgrade(game, module);

        var aliveBody = BodyOf(alive);
        var startingHealth = aliveBody.DamageCore.CurrentHealth;

        var exception = Record.Exception(() =>
        {
            for (var i = 0; i < 6; i++)
            {
                game.Step();
            }
        });

        Assert.Null(exception);
        Assert.True(aliveBody.DamageCore.CurrentHealth < startingHealth);
    }

    // ------------------------------------------------------------------ 11. base contract test - shadow copy CRC

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var fortress = game.SpawnObject("SpikyFortress", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        var live = ModuleOf(fortress);
        TriggerUpgrade(game, live);

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        var shadowHost = game.SpawnObject("SpikyFortress", game.CivilianPlayer, new Vector3(400, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    // ------------------------------------------------------------------ 12. save/load round-trip

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_PreservesGateAndCadence()
    {
        var game = NewGame();
        var fortress = game.SpawnObject("SpikyFortress", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        var live = ModuleOf(fortress);
        TriggerUpgrade(game, live);

        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }

        Assert.True(live.Triggered);

        var state = PortedModuleTestKit.Save(live);

        var shadowHost = game.SpawnObject("SpikyFortress", game.CivilianPlayer, new Vector3(400, 0, 0));
        var shadow = ModuleOf(shadowHost);
        Assert.False(shadow.Triggered);

        PortedModuleTestKit.Load(shadow, state);
        Assert.True(shadow.Triggered);
        Assert.Equal(live.NextPulseFrame, shadow.NextPulseFrame);
    }

    private static Fix64 Fix(int value) => new(value);
}
