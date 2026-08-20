// Mocked-game unit tests for the SpectreGunshipUpdate port (api-freeze-v1 §6 fitness item 4):
// one test per task-packet behavior (orbit insertion, gattling target acquisition - both the
// player-override reticle and the non-human attack-area fallback, howitzer volley cadence,
// departure/self-destruct, player aim-override constraint, early termination), plus the
// shadow-copy base test and a mid-state save/load round-trip.
//
// This module has no ported order-pipeline activation seam yet (file header on the port
// itself), so tests drive it through its public Activate/SetOverrideDestination/Disconnect
// entry points directly rather than through a SpecialPowerAtLocationApplicator order.

using System.Linq;
using System.Numerics;
using System.Reflection;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class SpectreGunshipUpdateContractTests
{
    // All distances/positions chosen so the "isFairDistanceFromShip" gate (ship-to-candidate
    // > GunshipOrbitRadius * 0.75 = 150) and the orbit-insertion gate (ship-to-target <
    // GunshipOrbitRadius = 200) are both satisfiable at once: the gunship spawns at (190,0,0),
    // the cast target is the origin.
    private const string Definitions = @"
Weapon TestHowitzer
  AttackRange = 999
  ClipSize = 1
  DelayBetweenShots = 1
  DamageNugget
    Damage = 15
    Radius = 0
    DamageType = EXPLOSION
    DeathType = NORMAL
  End
End

Object Gattling
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object SpectreGunship
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = SpectreGunshipUpdate ModuleTag_Gunship
    GattlingTemplateName = Gattling
    HowitzerWeaponTemplate = TestHowitzer
    GunshipOrbitRadius = 200
    AttackAreaRadius = 300
    TargetingReticleRadius = 50
    OrbitInsertionSlope = 0.7
    StrafingIncrement = 500
    RandomOffsetForHowitzer = 10
    HowitzerFiringRate = 200
    HowitzerFollowLag = 0
    OrbitTime = 400
  End
End

Object Enemy
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC0FFEE)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static SpectreGunshipUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<SpectreGunshipUpdate>().Single();

    private static ActiveBody BodyOf(GameObject obj) =>
        Assert.IsType<ActiveBody>(obj.BodyModule, exactMatch: false);

    private static void MakeEnemies(Player a, Player b)
    {
        a.AddEnemy(b);
        b.AddEnemy(a);
    }

    private static readonly FixVector3 Target = FixVector3.Zero;
    private static readonly Vector3 ShipSpawn = new(190, 0, 0);

    private static GameObject SpawnActivatedGunship(HeadlessSimGame game)
    {
        var gunship = game.SpawnObject("SpectreGunship", game.CivilianPlayer, ShipSpawn);
        ModuleOf(gunship).Activate(Target);
        return gunship;
    }

    /// <summary>Test-only: flips the (not-yet-wired, see the port's file header)
    /// ObjectPrivateStatusFlags.OffMap bit via reflection, since no production setter exists.</summary>
    private static void MarkOffMap(GameObject gameObject)
    {
        var field = typeof(GameObject).GetField("_privateStatus", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        var enumType = field.FieldType;
        var offMap = System.Convert.ToInt32(System.Enum.Parse(enumType, "OffMap"));
        var current = System.Convert.ToInt32(field.GetValue(gameObject));
        field.SetValue(gameObject, System.Enum.ToObject(enumType, current | offMap));
    }

    // ------------------------------------------------------------------ orbit insertion

    [Fact]
    public void Activate_EntersInsertingAndComputesSatellitePosition()
    {
        var game = NewGame();
        var gunship = game.SpawnObject("SpectreGunship", game.CivilianPlayer, new Vector3(500, 0, 0));
        var module = ModuleOf(gunship);

        module.Activate(Target);
        Assert.Equal(GunshipStatus.Inserting, module.Status);

        game.Step();
        game.Step();

        // perigee = (500,0,0) normalized = (1,0,0); apogee = (0,1,0); n1=0.7,n2=0.3;
        // declination = (0.7, 0.3, 0) * orbitRadius(200) = (140, 60, 0).
        var expected = new FixVector3(Fix64.FromDecimalLiteral("140"), Fix64.FromDecimalLiteral("60"), Fix64.Zero);
        var delta = module.SatellitePosition - expected;
        Assert.True(delta.Length() < Fix64.One, $"expected ~{expected}, got {module.SatellitePosition}");
    }

    [Fact]
    public void TransitionsInsertingToOrbiting_WhenWithinOrbitalRadius()
    {
        var game = NewGame();
        var gunship = SpawnActivatedGunship(game);
        var module = ModuleOf(gunship);

        game.Step();
        game.Step();

        Assert.Equal(GunshipStatus.Orbiting, module.Status);
        Assert.True(module.OrbitEscapeFrame.Value > 0);
    }

    // ------------------------------------------------------------------ gattling targeting

    [Fact]
    public void GattlingAcquiresPlayerOverrideTargetWithinReticle()
    {
        var game = NewGame();
        var gunship = SpawnActivatedGunship(game);
        var module = ModuleOf(gunship);
        var enemy = game.SpawnObject("Enemy", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }

        Assert.Equal(GunshipStatus.Orbiting, module.Status);
        Assert.Equal(enemy.Id, module.AcquiredTargetId);
        // Reticle-search success does NOT overwrite PositionToShootAt (GPL-exact): it stays at
        // the (unmoved) override destination, which Activate seeded to the cast target.
        Assert.Equal(module.OverrideTargetDestination, module.PositionToShootAt);
    }

    [Fact]
    public void GattlingAutoAcquiresWithinAttackAreaForNonHumanPlayer()
    {
        var game = NewGame();
        // CivilianPlayer.IsHuman is false (Data.Map.Player.CreateCivilianPlayer), matching the
        // task packet's "non-human players" auto-acquire condition.
        var gunship = SpawnActivatedGunship(game);
        var module = ModuleOf(gunship);
        Assert.False(gunship.Owner.IsHuman);

        // Outside the 50-unit reticle around the override destination (the origin), inside the
        // 300-unit attack area, and far enough from the ship (290 > 150).
        var enemy = game.SpawnObject("Enemy", game.PlayerManager.NeutralPlayer, new Vector3(-100, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }

        Assert.Equal(GunshipStatus.Orbiting, module.Status);
        Assert.Equal(enemy.Id, module.AcquiredTargetId);
        // The attack-area fallback DOES snap PositionToShootAt to the acquired target (GPL-exact).
        var enemyPosition = SimTransformBridge.PullPosition(enemy);
        Assert.True((module.PositionToShootAt - enemyPosition).Length() < Fix64.One);
    }

    // ------------------------------------------------------------------ howitzer volley

    [Fact]
    public void HowitzerFiresAfterLagThreshold_DamagingAcquiredTarget()
    {
        var game = NewGame();
        var gunship = SpawnActivatedGunship(game);
        var module = ModuleOf(gunship);
        var enemy = game.SpawnObject("Enemy", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        var body = BodyOf(enemy);
        var startingHealth = body.DamageCore.CurrentHealth;

        // HowitzerFollowLag=0, HowitzerFiringRate=1 frame: the first re-evaluation tick
        // acquires and winds the counter to 1 (too late to fire that same tick, GPL-exact
        // ordering); the second tick's fire-gate then sees counter(1) > lag(0) and fires.
        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        Assert.True(module.OkToFireHowitzerCounter > 0);
        Assert.True(body.DamageCore.CurrentHealth < startingHealth,
            "expected the howitzer volley to have damaged the acquired target");

        // Impact position is the gattling aim point plus a draw in [-offset, offset] on x/y.
        var delta = module.HowitzerImpactPosition - module.GattlingTargetPosition;
        Assert.True(Fix64.Abs(delta.X) <= Fix64.FromDecimalLiteral("10"));
        Assert.True(Fix64.Abs(delta.Y) <= Fix64.FromDecimalLiteral("10"));
    }

    // ------------------------------------------------------------------ departure

    [Fact]
    public void DepartureTrigger_DestroysGattlingImmediatelyAndSelfOnceOffMap()
    {
        var game = NewGame();
        var gunship = SpawnActivatedGunship(game);
        var module = ModuleOf(gunship);

        // Enough ticks to reach Orbiting and then exceed OrbitTime (400ms -> 2 frames).
        for (var i = 0; i < 8; i++)
        {
            game.Step();
        }

        Assert.Equal(GunshipStatus.Departing, module.Status);
        Assert.True(module.GattlingId.IsInvalid, "the contained gattling should be destroyed at orbit expiry");
        Assert.False(gunship.IsDestroyed, "the gunship itself only destructs once truly off-map");

        MarkOffMap(gunship);
        game.Step();

        Assert.True(gunship.IsDestroyed);
        Assert.Equal(GunshipStatus.Idle, module.Status);
    }

    // ------------------------------------------------------------------ player aim override

    [Fact]
    public void PlayerOverrideConstrainedToAttackRadiusBoundary()
    {
        var game = NewGame();
        var gunship = SpawnActivatedGunship(game);
        var module = ModuleOf(gunship);

        module.SetOverrideDestination(new FixVector3(Fix64.FromDecimalLiteral("1000"), Fix64.Zero, Fix64.Zero));

        game.Step();
        game.Step();

        // constraintRadius = AttackAreaRadius(300) - TargetingReticleRadius(50) = 250.
        var distance = (module.OverrideTargetDestination - module.InitialTargetPosition).Length();
        Assert.True(Fix64.Abs(distance - Fix64.FromDecimalLiteral("250")) < Fix64.One,
            $"expected the override destination clamped to radius ~250, got distance {distance}");
    }

    [Fact]
    public void OverrideDestinationIgnoredOnceDeparting()
    {
        var game = NewGame();
        var gunship = SpawnActivatedGunship(game);
        var module = ModuleOf(gunship);

        for (var i = 0; i < 8; i++)
        {
            game.Step();
        }

        Assert.Equal(GunshipStatus.Departing, module.Status);
        var beforeOverride = module.OverrideTargetDestination;

        module.SetOverrideDestination(new FixVector3(Fix64.FromDecimalLiteral("42"), Fix64.Zero, Fix64.Zero));

        Assert.Equal(beforeOverride, module.OverrideTargetDestination);
    }

    // ------------------------------------------------------------------ early termination

    [Fact]
    public void Disconnect_DestroysGattlingAndParksModule()
    {
        var game = NewGame();
        var gunship = SpawnActivatedGunship(game);
        var module = ModuleOf(gunship);

        game.Step();
        Assert.False(module.GattlingId.IsInvalid, "test setup: gattling should have spawned on Activate");

        module.Disconnect();

        Assert.Equal(GunshipStatus.Idle, module.Status);
        Assert.True(module.GattlingId.IsInvalid);

        // Idempotent: a second Disconnect() is a harmless no-op.
        module.Disconnect();
        Assert.Equal(GunshipStatus.Idle, module.Status);
    }

    [Fact]
    public void GunshipDestruction_CleansUpGattling()
    {
        var game = NewGame();
        var gunship = SpawnActivatedGunship(game);
        var module = ModuleOf(gunship);
        game.Step();
        Assert.False(module.GattlingId.IsInvalid);

        PortedModuleTestKit.TriggerDeath(gunship);

        game.Step();

        Assert.True(gunship.IsEffectivelyDead);
        Assert.Equal(GunshipStatus.Idle, module.Status);
        Assert.True(module.GattlingId.IsInvalid);
    }

    // ------------------------------------------------------------------ base contract tests

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var gunship = SpawnActivatedGunship(game);
        var enemy = game.SpawnObject("Enemy", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        var live = ModuleOf(gunship);
        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }

        var shadowHost = game.SpawnObject("SpectreGunship", game.CivilianPlayer, new Vector3(-500, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_PreservesOrbitState()
    {
        var game = NewGame();
        var gunship = SpawnActivatedGunship(game);

        var module = ModuleOf(gunship);
        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }

        Assert.Equal(GunshipStatus.Orbiting, module.Status);

        var state = PortedModuleTestKit.Save(module);

        var shadowHost = game.SpawnObject("SpectreGunship", game.CivilianPlayer, new Vector3(-500, 0, 0));
        var shadow = ModuleOf(shadowHost);
        Assert.Equal(GunshipStatus.Idle, shadow.Status);   // freshly constructed: parked

        PortedModuleTestKit.Load(shadow, state);
        Assert.Equal(GunshipStatus.Orbiting, shadow.Status);
        Assert.Equal(module.OrbitEscapeFrame, shadow.OrbitEscapeFrame);
    }
}
