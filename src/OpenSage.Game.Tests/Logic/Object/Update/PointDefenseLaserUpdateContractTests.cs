// Mocked-game unit tests for the PointDefenseLaserUpdate port (api-freeze-v1 §6 fitness
// item 4): one test per task-packet behavior (target acquisition, weapon firing cadence/ammo
// cycling, primary/secondary priority, velocity-prediction target selection, stealth gating,
// target-loss re-scan), plus the shadow-copy base test and a mid-state save/load round-trip.
// Object/Weapon definitions are parsed from INI text through the real parser, so the
// quantizing S5 parses (ScanRate/DelayBetweenShots/ClipReloadTime -> LogicFrameSpan,
// ScanRange/AttackRange/PredictTargetVelocityFactor -> Fix64) are on the tested path.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class PointDefenseLaserUpdateContractTests
{
    // 5 Hz (F6): DelayBetweenShots 200ms -> 1 frame, ClipReloadTime 400ms -> 2 frames,
    // ScanRate 1000ms -> 5 frames, DetectionRate 200ms -> 1 frame.
    private const string Definitions = @"
Weapon TestLaser
  AttackRange = 50
  ClipSize = 2
  AutoReloadsClip = Yes
  DelayBetweenShots = 200
  ClipReloadTime = 400
  DamageNugget
    Damage = 20
    Radius = 0
    DamageType = CRUSH
    DeathType = NORMAL
  End
End

Locomotor FastLoco
  Surfaces = GROUND
  Speed = 100
  TurnRate = 360
  Acceleration = 1000
  Braking = 1000
  Appearance = TWO_LEGS
  ZAxisBehavior = NO_Z_MOTIVE_FORCE
End

Object LaserPlatform
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = PointDefenseLaserUpdate ModuleTag_PDL
    WeaponTemplate = TestLaser
    PrimaryTargetTypes = INFANTRY
    SecondaryTargetTypes = VEHICLE
    ScanRate = 1000
    ScanRange = 300
    PredictTargetVelocityFactor = 6.0
  End
End

Object Grunt
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
End

Object FragileGrunt
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 15
  End
End

Object Truck
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
End

Object MovingGrunt
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Locomotor = SET_NORMAL FastLoco
End

Object Detector
  KindOf = STRUCTURE
  VisionRange = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
  End
  Behavior = StealthDetectorUpdate ModuleTag_Detect
    DetectionRate = 200
    DetectionRange = 20
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xFEED)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static PointDefenseLaserUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<PointDefenseLaserUpdate>().Single();

    private static ActiveBody BodyOf(GameObject obj) =>
        Assert.IsType<ActiveBody>(obj.BodyModule, exactMatch: false);

    private static void MakeEnemies(Player a, Player b)
    {
        a.AddEnemy(b);
        b.AddEnemy(a);
    }

    // ------------------------------------------------------------------ target acquisition

    [Fact]
    public void AcquiresClosestPrimaryTargetAfterScan()
    {
        var game = NewGame();
        var platform = game.SpawnObject("LaserPlatform", game.CivilianPlayer, Vector3.Zero);
        var near = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(30, 0, 0));
        var far = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(200, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        var module = ModuleOf(platform);
        Assert.Equal(near.Id, module.BestTargetId);
        Assert.NotEqual(far.Id, module.BestTargetId);
    }

    // ------------------------------------------------------------------ weapon firing / ammo cycling

    [Fact]
    public void FiresAtDelayIntervalAndCyclesAmmoThroughReload()
    {
        var game = NewGame();
        var platform = game.SpawnObject("LaserPlatform", game.CivilianPlayer, Vector3.Zero);
        var target = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(30, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        var body = BodyOf(target);
        var startingHealth = body.DamageCore.CurrentHealth;

        for (var i = 0; i < 12; i++)
        {
            game.Step();
        }

        var damageDealt = startingHealth - body.DamageCore.CurrentHealth;

        // ClipSize 2 * Damage 20 = 40 is everything one full clip can deal; more than that
        // over 12 frames (well past DelayBetweenShots(1) + ClipReloadTime(2) once) proves the
        // clip actually auto-reloaded and cycled, not just fired once and stalled.
        Assert.True(damageDealt > Fix(40), $"expected more than one clip's damage, got {damageDealt}");
    }

    // ------------------------------------------------------------------ priority targeting

    [Fact]
    public void EngagesPrimaryUntilEliminatedThenSecondary()
    {
        var game = NewGame();
        var platform = game.SpawnObject("LaserPlatform", game.CivilianPlayer, Vector3.Zero);
        var primary = game.SpawnObject("FragileGrunt", game.PlayerManager.NeutralPlayer, new Vector3(30, 0, 0));
        var secondary = game.SpawnObject("Truck", game.PlayerManager.NeutralPlayer, new Vector3(35, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        var secondaryBody = BodyOf(secondary);
        var secondaryStartingHealth = secondaryBody.DamageCore.CurrentHealth;

        // FragileGrunt (15 HP) dies to the first shot (20 damage); give it a few frames.
        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }

        Assert.True(primary.IsEffectivelyDead);
        // The secondary should not have been touched while the primary was alive and in range.
        Assert.Equal(secondaryStartingHealth, secondaryBody.DamageCore.CurrentHealth);

        // After the primary's death, the module drops it and re-engages: the secondary now
        // takes fire.
        for (var i = 0; i < 8; i++)
        {
            game.Step();
        }

        Assert.True(secondaryBody.DamageCore.CurrentHealth < secondaryStartingHealth);
    }

    // ------------------------------------------------------------------ range prediction

    [Fact]
    public void PredictsApproachingTargetOverACloserStationaryOne()
    {
        var game = NewGame();

        // Stationary target, real distance 120 (outside the 50-unit firing range).
        var stationary = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(120, 0, 0));

        // Approaching target, real distance 220 (farther than the stationary one) but closing
        // fast on the platform's future position. Give it a few frames of movement BEFORE the
        // platform ever scans, so it already carries real velocity at scan time.
        var approacher = game.SpawnObject("MovingGrunt", game.PlayerManager.NeutralPlayer, new Vector3(220, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        var loco = approacher.BehaviorModules.OfType<SimLocomotorUpdate>().Single();
        loco.SetTargetPosition(new FixVector3(Fix64.Zero, Fix64.Zero, Fix64.Zero), Fix64.FromDecimalLiteral("1000"));
        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }

        // Real distance now: stationary 120 (unchanged); approacher has closed some of its
        // 220 gap but is still farther than 120 - without prediction, the module would pick
        // the stationary target.
        var approacherPosition = loco.Physics.Position;
        Assert.True(approacherPosition.X > Fix64.FromDecimalLiteral("120"), "test setup: approacher must still be farther than the stationary target");

        // Now the platform spawns and takes its first scan: PredictTargetVelocityFactor (6)
        // projects the approacher's velocity forward, putting its PREDICTED position closer
        // than the stationary target's real one.
        var platform = game.SpawnObject("LaserPlatform", game.CivilianPlayer, Vector3.Zero);
        // SetWakeFrame(UpdateSleepTime.None) means "wake up next frame" (a 1-frame minimum
        // scheduling latency shared by every sleepy update module, GameLogic.cs), not
        // same-frame execution - a freshly spawned module's very first Update() lands on the
        // tick after the one it was created on, so this needs two steps, not one.
        game.Step();
        game.Step();

        var module = ModuleOf(platform);
        Assert.Equal(approacher.Id, module.BestTargetId);

        // Let it keep closing until it's actually within the 50-unit firing range, and confirm
        // the laser fires on it once it arrives (the fire gate itself uses the REAL distance
        // every frame, GPL-exact).
        var body = BodyOf(approacher);
        var startingHealth = body.DamageCore.CurrentHealth;
        for (var i = 0; i < 15; i++)
        {
            game.Step();
        }

        Assert.True(body.DamageCore.CurrentHealth < startingHealth);
    }

    // ------------------------------------------------------------------ stealth handling

    [Fact]
    public void DoesNotFireOnUndetectedStealthedEnemy_FiresOnceRevealed()
    {
        var game = NewGame();
        var platform = game.SpawnObject("LaserPlatform", game.CivilianPlayer, Vector3.Zero);
        // Within the laser's own 50-unit firing range, but outside the (separately spawned)
        // detector's 20-unit detection range.
        var enemy = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(30, 0, 0));
        enemy.SetObjectStatus(ObjectStatus.Stealthed, true);
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        var body = BodyOf(enemy);
        var startingHealth = body.DamageCore.CurrentHealth;

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        Assert.False(enemy.TestStatus(ObjectStatus.Detected));
        Assert.Equal(startingHealth, body.DamageCore.CurrentHealth);   // stealthed & undetected: no fire

        // Bring the enemy inside a detector's range: it gets revealed, and the laser (which
        // has kept scanning every ScanRate interval) picks it up and fires.
        game.SpawnObject("Detector", game.CivilianPlayer, new Vector3(30, 5, 0));

        for (var i = 0; i < 8; i++)
        {
            game.Step();
        }

        Assert.True(enemy.TestStatus(ObjectStatus.Detected));
        Assert.True(body.DamageCore.CurrentHealth < startingHealth);
    }

    // ------------------------------------------------------------------ target loss handling

    [Fact]
    public void ReScansAndReplacesTargetThatLeavesScanRange()
    {
        var game = NewGame();
        var platform = game.SpawnObject("LaserPlatform", game.CivilianPlayer, Vector3.Zero);
        var lost = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(30, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        var module = ModuleOf(platform);

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        Assert.Equal(lost.Id, module.BestTargetId);

        // Order it out of ScanRange (300) entirely.
        lost.UpdateTransform(new Vector3(1000, 0, 0));
        lost.UpdateColliders();

        // Bring in a replacement, within scan+fire range.
        var replacement = game.SpawnObject("Truck", game.PlayerManager.NeutralPlayer, new Vector3(25, 0, 0));

        for (var i = 0; i < 8; i++)
        {
            game.Step();
        }

        Assert.NotEqual(lost.Id, module.BestTargetId);
        Assert.Equal(replacement.Id, module.BestTargetId);
    }

    // ------------------------------------------------------------------ base contract tests

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var platform = game.SpawnObject("LaserPlatform", game.CivilianPlayer, Vector3.Zero);
        game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(30, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        var live = ModuleOf(platform);
        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        var shadowHost = game.SpawnObject("LaserPlatform", game.CivilianPlayer, new Vector3(400, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_PreservesTrackedTarget()
    {
        var game = NewGame();
        var platform = game.SpawnObject("LaserPlatform", game.CivilianPlayer, Vector3.Zero);
        var enemy = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(30, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        var module = ModuleOf(platform);
        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        Assert.Equal(enemy.Id, module.BestTargetId);

        var state = PortedModuleTestKit.Save(module);

        var shadowHost = game.SpawnObject("LaserPlatform", game.CivilianPlayer, new Vector3(400, 0, 0));
        var shadow = ModuleOf(shadowHost);
        Assert.Equal(ObjectId.Invalid, shadow.BestTargetId);   // freshly constructed: nothing tracked

        PortedModuleTestKit.Load(shadow, state);
        Assert.Equal(enemy.Id, shadow.BestTargetId);           // load carried the tracked target over
    }

    private static Fix64 Fix(int value) => new(value);
}
