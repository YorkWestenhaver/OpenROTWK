// Mocked-game unit tests for the AutoHealBehavior pilot port (api-freeze-v1 §6 fitness
// item 4): one test per INI-configurable behavior branch, [create -> tick -> observable
// effect], plus the mid-behavior save/load round-trip and the shadow-copy base test.
// Object definitions are parsed from INI text through the real parser, so the quantizing
// S5 parse functions are on the tested path.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Behaviors;

public class AutoHealContractTests
{
    // 5 Hz (F6): HealingDelay 400 ms -> 2 frames; StartHealingDelay 1000 ms -> 5 frames.
    private const string Definitions = @"
Object SelfHealer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoHealBehavior ModuleTag_Heal
    StartsActive = Yes
    HealingAmount = 5
    HealingDelay = 400
  End
End

Object DelayedSelfHealer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoHealBehavior ModuleTag_Heal
    StartsActive = Yes
    HealingAmount = 5
    HealingDelay = 200
    StartHealingDelay = 1000
  End
End

Object AuraHealer
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = AutoHealBehavior ModuleTag_Heal
    StartsActive = Yes
    HealingAmount = 10
    HealingDelay = 200
    Radius = 30
    KindOf = INFANTRY
    SkipSelfForHealing = Yes
  End
End

Object BurstHealer
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = AutoHealBehavior ModuleTag_Heal
    StartsActive = Yes
    HealingAmount = 25
    HealingDelay = 200
    Radius = 30
    SingleBurst = Yes
    SkipSelfForHealing = Yes
  End
End

Object Hospital
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = AutoHealBehavior ModuleTag_Heal
    StartsActive = Yes
    HealingAmount = 3
    HealingDelay = 200
    AffectsWholePlayer = Yes
    SkipSelfForHealing = Yes
  End
End

Upgrade Upgrade_Regeneration
  Type = PLAYER
End

Object UpgradeHealer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AutoHealBehavior ModuleTag_Heal
    TriggeredBy = Upgrade_Regeneration
    HealingAmount = 5
    HealingDelay = 200
  End
End

Object Grunt
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object WarMachine
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xB00)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static void Damage(GameObject target, float amount)
    {
        target.AttemptDamage(new DamageInfoInput(null)
        {
            DamageType = DamageType.Explosion,
            DeathType = DeathType.Normal,
            Amount = amount,
        });
    }

    private static AutoHealBehavior HealModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<AutoHealBehavior>().Single();

    [Fact]
    public void SelfHeal_PulsesEveryDelay_AndSleepsAtFullHealth()
    {
        var game = NewGame();
        var healer = game.SpawnObject("SelfHealer", game.CivilianPlayer, Vector3.Zero);

        Damage(healer, 20f);
        Assert.Equal(80f, healer.BodyModule.Health);

        // 2-frame delay, 5 hp per pulse: after 10 frames at most 5 pulses have fired and
        // health is back at 100; the exact first-pulse frame depends on the ctor stagger
        // draw, which is the point of the trajectory checks in the harness scenario.
        for (var i = 0; i < 12; i++)
        {
            game.Step();
        }
        Assert.Equal(100f, healer.BodyModule.Health);

        // At full health the module sleeps; health stays pinned.
        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }
        Assert.Equal(100f, healer.BodyModule.Health);
    }

    [Fact]
    public void StartHealingDelay_DamageResetsTheTimer()
    {
        var game = NewGame();
        var healer = game.SpawnObject("DelayedSelfHealer", game.CivilianPlayer, Vector3.Zero);

        Damage(healer, 50f);
        var damagedHealth = healer.BodyModule.Health;

        // StartHealingDelay is 5 frames: for the next 4 frames nothing may heal.
        for (var i = 0; i < 4; i++)
        {
            game.Step();
            Assert.Equal(damagedHealth, healer.BodyModule.Health);
        }

        // After the delay expires the 1-frame pulse train runs.
        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }
        Assert.True(healer.BodyModule.Health > damagedHealth);
    }

    [Fact]
    public void Radius_HealsDamagedAllyInRange_NotEnemies_NotOutOfRange()
    {
        var game = NewGame();
        var healer = game.SpawnObject("AuraHealer", game.CivilianPlayer, Vector3.Zero);
        var nearAlly = game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(10, 0, 0));
        var farAlly = game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(500, 0, 0));
        var enemy = game.SpawnObject("Grunt", game.PlayerManager.Players[0], new Vector3(-10, 0, 0));

        Damage(nearAlly, 50f);
        Damage(farAlly, 50f);
        Damage(enemy, 50f);

        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }

        Assert.True(nearAlly.BodyModule.Health > 50f);          // healed
        Assert.Equal(50f, farAlly.BodyModule.Health);           // out of range
        Assert.Equal(50f, enemy.BodyModule.Health);             // wrong owner
        Assert.Equal(healer.Id, nearAlly.HealedByObjectId);     // sole-benefactor claim
    }

    [Fact]
    public void Radius_KindOfFilterExcludesNonMatching()
    {
        var game = NewGame();
        game.SpawnObject("AuraHealer", game.CivilianPlayer, Vector3.Zero);
        var vehicle = game.SpawnObject("WarMachine", game.CivilianPlayer, new Vector3(10, 0, 0));

        Damage(vehicle, 50f);
        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }

        Assert.Equal(50f, vehicle.BodyModule.Health);           // KindOf = INFANTRY only
    }

    [Fact]
    public void SingleBurst_FiresOnceThenSleepsForever()
    {
        var game = NewGame();
        game.SpawnObject("BurstHealer", game.CivilianPlayer, Vector3.Zero);
        var ally = game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(10, 0, 0));

        Damage(ally, 60f);

        for (var i = 0; i < 10; i++)
        {
            game.Step();
        }

        // Exactly one 25 hp pulse, despite 10 frames at a 1-frame delay.
        Assert.Equal(65f, ally.BodyModule.Health);
    }

    [Fact]
    public void AffectsWholePlayer_HealsOwnedObjectsAnywhere_NotOtherPlayers()
    {
        var game = NewGame();
        game.SpawnObject("Hospital", game.CivilianPlayer, Vector3.Zero);
        var farOwn = game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(5000, 5000, 0));
        var foreign = game.SpawnObject("Grunt", game.PlayerManager.Players[0], new Vector3(10, 0, 0));

        Damage(farOwn, 30f);
        Damage(foreign, 30f);

        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }

        Assert.True(farOwn.BodyModule.Health > 70f);            // radius-independent
        Assert.Equal(70f, foreign.BodyModule.Health);           // other player untouched
    }

    [Fact]
    public void UpgradeGated_DoesNotHealUntilTriggered()
    {
        var game = NewGame();
        var healer = game.SpawnObject("UpgradeHealer", game.CivilianPlayer, Vector3.Zero);
        var module = HealModuleOf(healer);

        Damage(healer, 40f);
        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }
        Assert.Equal(60f, healer.BodyModule.Health);            // not triggered, no healing

        var upgrades = new UpgradeSet
        {
            game.AssetStore.Upgrades.GetByName("Upgrade_Regeneration"),
        };
        module.TryUpgrade(upgrades);

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }
        Assert.True(healer.BodyModule.Health > 60f);            // healing after the trigger
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var healer = game.SpawnObject("DelayedSelfHealer", game.CivilianPlayer, Vector3.Zero);
        var live = HealModuleOf(healer);

        // Drive real state into the module: triggered flag on (StartsActive), damage
        // re-arm pending, some pulses fired.
        Damage(healer, 50f);
        for (var i = 0; i < 8; i++)
        {
            game.Step();
        }

        // The shadow is the same class over the same data on a second object, in a
        // different (untouched) state; Load must overwrite everything the walk carries.
        var shadowHost = game.SpawnObject("DelayedSelfHealer", game.CivilianPlayer, new Vector3(100, 0, 0));
        var shadow = HealModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        // Two identical games, identical script. Game B round-trips the module state (and
        // the engine-owned wake frame, S6) through Save->Load mid-behavior; if the load
        // path lost or misread anything, B's continuation diverges from A's.
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 4);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static float[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xFEED);
        var healer = game.SpawnObject("DelayedSelfHealer", game.CivilianPlayer, Vector3.Zero);
        var module = HealModuleOf(healer);

        Damage(healer, 50f);

        var trajectory = new float[14];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk;     // engine-owned, walk-carried (S6)
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            game.Step();
            trajectory[i] = healer.BodyModule.Health;
        }

        return trajectory;
    }
}
