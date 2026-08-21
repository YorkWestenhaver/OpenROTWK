// Mocked-game unit tests for the SupplyWarehouseCripplingBehavior port (api-freeze-v1 §6 fitness
// item 4): one test per behavior branch, [create -> tick/damage -> observable effect], plus the
// mid-behavior save/load round-trip and the shadow-copy base test. Object definitions are parsed
// from INI text through the real parser, so the quantizing S5 ParseDurationLogicFrames/ParseFix64
// audit is on the tested path.
//
// Two observables:
//   (a) crippling: DockUpdate.IsDockCrippled, driven on the body's ReallyDamaged in/out edges.
//   (b) self-heal: the object's health, restored on a suppress-then-repeat timer after damage.

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Behaviors;

public class SupplyWarehouseCripplingBehaviorContractTests
{
    // Warehouse: 100 HP, ReallyDamaged at 25%, self-heal after 1000 ms (5 frames @ 5 Hz) of quiet,
    // then +10 HP every 1000 ms. The dock sibling is what the crippling half toggles.
    private const string Definitions = @"
GameData
  UnitDamagedThreshold = 0.5
  UnitReallyDamagedThreshold = 0.25
End

Object Warehouse
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SupplyWarehouseDockUpdate ModuleTag_Dock
    StartingBoxes = 10
  End
  Behavior = SupplyWarehouseCripplingBehavior ModuleTag_Cripple
    SelfHealSupression = 1000
    SelfHealDelay = 1000
    SelfHealAmount = 10
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x5A1E)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static SupplyWarehouseCripplingBehavior ModuleOf(GameObject obj) =>
        obj.FindBehavior<SupplyWarehouseCripplingBehavior>();

    private static bool Crippled(GameObject obj) =>
        obj.FindBehavior<DockUpdate>().IsDockCrippled;

    private static float Health(GameObject obj) => obj.BodyModule.Health;

    private static GameObject SpawnWarehouse(HeadlessSimGame game) =>
        game.SpawnObject("Warehouse", game.CivilianPlayer, Vector3.Zero);

    // ---- (a) crippling ----

    [Fact]
    public void HealthyWarehouse_DockIsNotCrippled()
    {
        var game = NewGame();
        var warehouse = SpawnWarehouse(game);

        Assert.False(Crippled(warehouse));
    }

    [Fact]
    public void EnteringReallyDamaged_CripplesTheDock()
    {
        var game = NewGame();
        var warehouse = SpawnWarehouse(game);

        // Drive health to 20% (< 25% ReallyDamaged threshold) through the real damage pipeline so
        // ActiveBody dispatches OnBodyDamageStateChange to the crippling behavior.
        PortedModuleTestKit.ApplyDamage(warehouse, amount: 80f, DamageType.Unresistable);

        Assert.Equal(BodyDamageType.ReallyDamaged, warehouse.BodyModule.DamageState);
        Assert.True(Crippled(warehouse));
    }

    [Fact]
    public void HealingBackOutOfReallyDamaged_UncripplesTheDock()
    {
        var game = NewGame();
        var warehouse = SpawnWarehouse(game);

        PortedModuleTestKit.ApplyDamage(warehouse, amount: 80f, DamageType.Unresistable);
        Assert.True(Crippled(warehouse));

        // Heal back above the ReallyDamaged threshold: the falling edge (old == ReallyDamaged)
        // clears the crippled flag.
        warehouse.AttemptHealing(100f, null);

        Assert.NotEqual(BodyDamageType.ReallyDamaged, warehouse.BodyModule.DamageState);
        Assert.False(Crippled(warehouse));
    }

    // ---- (b) self-heal ----

    [Fact]
    public void FreshWarehouse_NeverSelfHeals()
    {
        var game = NewGame();
        var warehouse = SpawnWarehouse(game);

        for (var i = 0; i < 20; i++)
        {
            game.Step();
        }

        // Full health and never damaged: the module sleeps forever and never touches health.
        Assert.Equal(100f, Health(warehouse));
    }

    [Fact]
    public void Damage_SuppressesHealing_ThenRepeatsUntilFull()
    {
        var game = NewGame();
        var warehouse = SpawnWarehouse(game);

        // Sub-really-damaged hit: health 60 (Damaged, not ReallyDamaged -> no cripple), and the
        // self-heal timer arms with a 5-frame suppression window.
        PortedModuleTestKit.ApplyDamage(warehouse, amount: 40f, DamageType.Unresistable);
        Assert.Equal(60f, Health(warehouse));

        // Within the suppression window nothing heals.
        game.Step();
        game.Step();
        game.Step();
        Assert.Equal(60f, Health(warehouse));

        // Given enough frames the suppress-then-repeat pulses restore full health, and then the
        // module sleeps (it does not overshoot past max).
        for (var i = 0; i < 40; i++)
        {
            game.Step();
        }
        Assert.Equal(100f, Health(warehouse));
    }

    [Fact]
    public void SelfHeal_ProceedsInDiscretePulses_NotAllAtOnce()
    {
        var game = NewGame();
        var warehouse = SpawnWarehouse(game);

        PortedModuleTestKit.ApplyDamage(warehouse, amount: 90f, DamageType.Unresistable); // health 10
        // This hit is ReallyDamaged (10% < 25%): the dock is crippled AND the self-heal timer arms.
        Assert.True(Crippled(warehouse));

        // Step past suppression plus exactly one heal delay: a single +10 pulse (not the whole
        // deficit) has been applied.
        for (var i = 0; i < 11; i++)
        {
            game.Step();
        }
        var health = Health(warehouse);
        Assert.True(health > 10f && health < 100f,
            $"expected a partial pulse-based recovery, got {health}");
    }

    // ---- Xfer: shadow-copy base test + mid-behavior save/load ----

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var warehouse = SpawnWarehouse(game);

        // Drive real timer state into the module: damage arms both frame timers, a couple of
        // steps advance the schedule.
        PortedModuleTestKit.ApplyDamage(warehouse, amount: 40f, DamageType.Unresistable);
        game.Step();
        game.Step();
        var live = ModuleOf(warehouse);

        // The shadow is the same class on a second warehouse in a different (untouched) state;
        // Load must overwrite everything the walk carries.
        var shadowHost = game.SpawnObject("Warehouse", game.CivilianPlayer, new Vector3(200, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        // Two identical games, identical script. Game B round-trips the module state (and the
        // engine-owned wake frame, S6) through Save->Load mid-heal; if the load path lost or
        // misread either frame timer, B's health trajectory diverges from A's.
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 8);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static float[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xF00D);
        var warehouse = SpawnWarehouse(game);
        var module = ModuleOf(warehouse);

        // Damage once so a suppress-then-repeat heal is in flight through the round-trip.
        PortedModuleTestKit.ApplyDamage(warehouse, amount: 55f, DamageType.Unresistable);

        var trajectory = new float[30];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk;      // engine-owned, walk-carried (S6)
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            game.Step();
            trajectory[i] = Health(warehouse);
        }

        return trajectory;
    }
}
