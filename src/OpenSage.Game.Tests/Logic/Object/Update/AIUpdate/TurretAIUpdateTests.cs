// R12 port contract tests for TurretAIUpdate (legacy/IGameEngine surface, owned directly by
// AIUpdate rather than the generic ModuleData.CreateModule dispatch). Drives the module's
// internal Update(BitArray<AutoAcquireEnemiesType>) directly against a real GameObject hosted
// by HeadlessSimGame (so GameClient/Drawable exist and GameObject.ModelConditionFlags works),
// with the logic frame and RNG stream advanced by hand for exact control over each test case.

using System.IO;
using System.Numerics;
using System.Reflection;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Mathematics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update.AIUpdate;

public class TurretAIUpdateTests
{
    private const string ObjectDefinitions = @"
Object TurretTestUnit
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 1)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(ObjectDefinitions);
        return game;
    }

    private static GameObject SpawnHost(HeadlessSimGame game) =>
        game.SpawnObject("TurretTestUnit", game.CivilianPlayer, Vector3.Zero);

    private static TurretAIUpdateModuleData BuildModuleData(
        float turretTurnRate = 0.01f,
        int naturalTurretAngle = 0,
        bool firesWhileTurning = false,
        uint minIdleScanFrames = 2,
        uint maxIdleScanFrames = 4,
        uint recenterTimeFrames = 3,
        bool initiallyDisabled = false)
    {
        var data = new TurretAIUpdateModuleData();
        SetProp(data, nameof(TurretAIUpdateModuleData.TurretTurnRate), turretTurnRate);
        SetProp(data, nameof(TurretAIUpdateModuleData.NaturalTurretAngle), naturalTurretAngle);
        SetProp(data, nameof(TurretAIUpdateModuleData.FiresWhileTurning), firesWhileTurning);
        SetProp(data, nameof(TurretAIUpdateModuleData.MinIdleScanInterval), new LogicFrameSpan(minIdleScanFrames));
        SetProp(data, nameof(TurretAIUpdateModuleData.MaxIdleScanInterval), new LogicFrameSpan(maxIdleScanFrames));
        SetProp(data, nameof(TurretAIUpdateModuleData.RecenterTime), new LogicFrameSpan(recenterTimeFrames));
        SetProp(data, nameof(TurretAIUpdateModuleData.InitiallyDisabled), initiallyDisabled);
        return data;
    }

    private static void SetProp(object target, string propertyName, object value)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);
        property!.SetValue(target, value);
    }

    private static void SetCurrentFrame(HeadlessSimGame game, uint frame)
    {
        var field = typeof(GameLogic).GetField("_currentFrame", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(game.GameLogic, new LogicFrame(frame));
    }

    /// <summary>Gives the host a real Weapon in the primary slot so CurrentWeapon/SetTarget work.</summary>
    private static Weapon AttachWeapon(GameObject gameObject, HeadlessSimGame game)
    {
        var weaponsField = typeof(WeaponSet).GetField("_weapons", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(weaponsField);
        var weapons = (Weapon[])weaponsField!.GetValue(gameObject.ActiveWeaponSet);
        var weapon = new Weapon(gameObject, new WeaponTemplate(), WeaponSlot.Primary, game.GameEngine);
        weapons[(int)WeaponSlot.Primary] = weapon;
        return weapon;
    }

    // ------------------------------------------------------------------------------------
    // 1. Idle scan-timer expiry -> ScanningForTargets -> (stub finds nothing) -> Idle with
    //    a new random wait interval inside [MinIdleScanInterval, MaxIdleScanInterval].
    // ------------------------------------------------------------------------------------
    [Fact]
    public void IdleScanTimerExpiry_NoTargetFound_ReturnsToIdleWithNewRandomWait()
    {
        var game = NewGame();
        var host = SpawnHost(game);
        var data = BuildModuleData(minIdleScanFrames: 2, maxIdleScanFrames: 4);
        var turret = new TurretAIUpdate(host, game.GameEngine, data);

        // Ctor starts in ScanningForTargets; first tick (no target, stub finds nothing)
        // draws the first wait and drops to Idle.
        SetCurrentFrame(game, 0);
        turret.Update(null);
        Assert.Equal(TurretAIUpdate.TurretAIStates.Idle, turret.State);
        var firstWait = turret.WaitUntil;
        Assert.InRange(firstWait.Value, data.MinIdleScanInterval.Value, data.MaxIdleScanInterval.Value);

        // Timer not yet expired: stays Idle.
        SetCurrentFrame(game, firstWait.Value - 1);
        turret.Update(null);
        Assert.Equal(TurretAIUpdate.TurretAIStates.Idle, turret.State);

        // Timer expires with AutoAcquireEnemiesWhenIdle = Yes -> ScanningForTargets this tick.
        SetCurrentFrame(game, firstWait.Value);
        var autoAcquire = new BitArray<AutoAcquireEnemiesType>(AutoAcquireEnemiesType.Yes);
        turret.Update(autoAcquire);
        Assert.Equal(TurretAIUpdate.TurretAIStates.ScanningForTargets, turret.State);

        // Next tick: scan stub finds nothing -> back to Idle with a fresh random wait,
        // still bounded by [Min, Max].
        var frameOfSecondDraw = firstWait.Value + 1;
        SetCurrentFrame(game, frameOfSecondDraw);
        turret.Update(autoAcquire);
        Assert.Equal(TurretAIUpdate.TurretAIStates.Idle, turret.State);
        var secondWaitSpan = turret.WaitUntil.Value - frameOfSecondDraw;
        Assert.InRange(secondWaitSpan, data.MinIdleScanInterval.Value, data.MaxIdleScanInterval.Value);
    }

    // ------------------------------------------------------------------------------------
    // 2. Object moving while attacking a target -> forced to Recentering, target cleared,
    //    wait RecenterTime frames.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void MovingWhileAttacking_ForcesRecentering_ClearsTargetAndWaitsRecenterTime()
    {
        var game = NewGame();
        var host = SpawnHost(game);
        var data = BuildModuleData(naturalTurretAngle: 0, recenterTimeFrames: 3);
        var turret = new TurretAIUpdate(host, game.GameEngine, data);
        var weapon = AttachWeapon(host, game);

        // Target directly ahead (yaw 0) so the turret aligns in a single tick and reaches
        // Attacking.
        weapon.SetTarget(new WeaponTarget(new Vector3(10f, 0f, 0f)));

        SetCurrentFrame(game, 0);
        turret.Update(null); // ScanningForTargets -> Turning (target already acquired)
        Assert.Equal(TurretAIUpdate.TurretAIStates.Turning, turret.State);

        turret.Update(null); // Turning -> Attacking (already aligned: deltaYaw == 0)
        Assert.Equal(TurretAIUpdate.TurretAIStates.Attacking, turret.State);

        // Now the object starts moving.
        host.ModelConditionFlags.Set(ModelConditionFlag.Moving, true);
        SetCurrentFrame(game, 10);
        turret.Update(null);

        Assert.Equal(TurretAIUpdate.TurretAIStates.Recentering, turret.State);
        Assert.Null(host.CurrentWeapon.CurrentTarget);
        Assert.Equal(10u + data.RecenterTime.Value, turret.WaitUntil.Value);
    }

    // ------------------------------------------------------------------------------------
    // 3. Turning, target within one TurretTurnRate step of alignment -> rotation completes in
    //    one further tick, FiresWhileTurning gates the Attacking model condition, state ->
    //    Attacking. GPL's friend_turnTowardsAngle (TurretAI.cpp:392-429) snaps only once the
    //    remaining angle is smaller than a single turn-rate step; it does not use a fixed
    //    angle threshold, so this exercises both branches of that rule.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void Turning_TargetWithinTurnRateStep_CompletesRotationAndTransitionsToAttacking()
    {
        var game = NewGame();
        var host = SpawnHost(game);
        var data = BuildModuleData(turretTurnRate: 0.01f, naturalTurretAngle: 0, firesWhileTurning: false);
        var turret = new TurretAIUpdate(host, game.GameEngine, data);
        var weapon = AttachWeapon(host, game);

        // Target straight ahead (targetYaw == 0); start the turret 0.005 rad off -- inside a
        // single 0.01 rad/frame turn-rate step, so GPL snaps to the target immediately.
        weapon.SetTarget(new WeaponTarget(new Vector3(10f, 0f, 0f)));
        host.TurretYaw = 0.005f;

        SetCurrentFrame(game, 0);
        turret.Update(null); // ScanningForTargets -> Turning
        Assert.Equal(TurretAIUpdate.TurretAIStates.Turning, turret.State);

        turret.Update(null); // Turning: |deltaYaw| = 0.005 < turnRate (0.01) -> snaps, -> Attacking
        Assert.Equal(TurretAIUpdate.TurretAIStates.Attacking, turret.State);
        Assert.Equal(0f, host.TurretYaw, 4);
        // FiresWhileTurning = false, so entering Attacking (not-while-turning) sets it here.
        Assert.True(host.ModelConditionFlags.Get(ModelConditionFlag.Attacking));
    }

    // ------------------------------------------------------------------------------------
    // 3b. A remaining angle larger than a single turn-rate step must NOT snap in one tick --
    //     it takes exactly ceil(offset / TurretTurnRate) Turning ticks, stepping by
    //     TurretTurnRate each time, before the final tick's remainder snaps and transitions
    //     to Attacking. This is the case OpenSage's old hardcoded 0.15 rad threshold got
    //     wrong for any TurretTurnRate below ~0.15 rad/frame (the normal case).
    // ------------------------------------------------------------------------------------
    [Fact]
    public void Turning_TargetBeyondTurnRateStep_RotatesGraduallyThenSnapsToAttacking()
    {
        var game = NewGame();
        var host = SpawnHost(game);
        var data = BuildModuleData(turretTurnRate: 0.01f, naturalTurretAngle: 0, firesWhileTurning: false);
        var turret = new TurretAIUpdate(host, game.GameEngine, data);
        var weapon = AttachWeapon(host, game);

        // 0.05 rad offset, well beyond a single 0.01 rad/frame step (and well under the old,
        // now-removed, hardcoded 0.15 rad snap threshold -- the exact case the old code got
        // wrong by snapping instantly instead of turning gradually).
        weapon.SetTarget(new WeaponTarget(new Vector3(10f, 0f, 0f)));
        host.TurretYaw = 0.05f;

        SetCurrentFrame(game, 0);
        turret.Update(null); // ScanningForTargets -> Turning
        Assert.Equal(TurretAIUpdate.TurretAIStates.Turning, turret.State);

        turret.Update(null); // still far from aligned: single turn-rate step, stays Turning
        Assert.Equal(TurretAIUpdate.TurretAIStates.Turning, turret.State);
        Assert.Equal(0.05f - data.TurretTurnRate, host.TurretYaw, 4);

        var ticks = 0;
        while (turret.State == TurretAIUpdate.TurretAIStates.Turning && ticks < 10)
        {
            turret.Update(null);
            ticks++;
        }

        Assert.Equal(TurretAIUpdate.TurretAIStates.Attacking, turret.State);
        Assert.True(ticks is >= 3 and < 10, $"expected several turn-rate-bounded ticks before snapping, got {ticks}");
        Assert.Equal(0f, host.TurretYaw, 4);
        Assert.True(host.ModelConditionFlags.Get(ModelConditionFlag.Attacking));
    }

    [Fact]
    public void Turning_TargetBeyondThreshold_KeepsTurningByTurretTurnRate()
    {
        var game = NewGame();
        var host = SpawnHost(game);
        var data = BuildModuleData(turretTurnRate: 0.01f, naturalTurretAngle: 0);
        var turret = new TurretAIUpdate(host, game.GameEngine, data);
        var weapon = AttachWeapon(host, game);

        // 15 degrees ~= 0.2618 rad, comfortably beyond the 0.15 rad threshold.
        host.TurretYaw = 0.2618f;
        weapon.SetTarget(new WeaponTarget(new Vector3(10f, 0f, 0f)));

        SetCurrentFrame(game, 0);
        turret.Update(null); // ScanningForTargets -> Turning
        turret.Update(null); // still beyond threshold: one turn-rate step, stays Turning

        Assert.Equal(TurretAIUpdate.TurretAIStates.Turning, turret.State);
        Assert.Equal(0.2618f - data.TurretTurnRate, host.TurretYaw, 4);
    }

    // ------------------------------------------------------------------------------------
    // 4. Attacking; target destroyed (CurrentTarget goes null) -> Recentering, waiting
    //    RecenterTime frames.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void Attacking_TargetLost_TransitionsToRecenteringWithRecenterTimeWait()
    {
        var game = NewGame();
        var host = SpawnHost(game);
        var data = BuildModuleData(naturalTurretAngle: 0, recenterTimeFrames: 3);
        var turret = new TurretAIUpdate(host, game.GameEngine, data);
        var weapon = AttachWeapon(host, game);

        weapon.SetTarget(new WeaponTarget(new Vector3(10f, 0f, 0f)));

        SetCurrentFrame(game, 0);
        turret.Update(null); // ScanningForTargets -> Turning
        turret.Update(null); // Turning -> Attacking (aligned)
        Assert.Equal(TurretAIUpdate.TurretAIStates.Attacking, turret.State);

        // Simulate the target object being destroyed: the weapon clears its target.
        weapon.SetTarget(null);

        SetCurrentFrame(game, 7);
        turret.Update(null);

        Assert.Equal(TurretAIUpdate.TurretAIStates.Recentering, turret.State);
        Assert.Equal(7u + data.RecenterTime.Value, turret.WaitUntil.Value);
    }

    // ------------------------------------------------------------------------------------
    // 5. Recentering timeout with the turret already aligned to NaturalTurretAngle
    //    (Rotate() returns false) -> Idle.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void Recentering_TimeoutAndAligned_TransitionsToIdle()
    {
        var game = NewGame();
        var host = SpawnHost(game);
        var data = BuildModuleData(naturalTurretAngle: 0, recenterTimeFrames: 3);
        var turret = new TurretAIUpdate(host, game.GameEngine, data);
        var weapon = AttachWeapon(host, game);

        weapon.SetTarget(new WeaponTarget(new Vector3(10f, 0f, 0f)));

        SetCurrentFrame(game, 0);
        turret.Update(null); // ScanningForTargets -> Turning
        turret.Update(null); // Turning -> Attacking (aligned, TurretYaw == 0 == natural angle)
        Assert.Equal(TurretAIUpdate.TurretAIStates.Attacking, turret.State);

        weapon.SetTarget(null);
        SetCurrentFrame(game, 5);
        turret.Update(null); // Attacking -> Recentering, wait until 5 + RecenterTime
        Assert.Equal(TurretAIUpdate.TurretAIStates.Recentering, turret.State);
        var waitUntil = turret.WaitUntil;

        // Before the wait elapses, Recentering does nothing.
        SetCurrentFrame(game, waitUntil.Value - 1);
        turret.Update(null);
        Assert.Equal(TurretAIUpdate.TurretAIStates.Recentering, turret.State);

        // Timeout reached; TurretYaw (0) is already the natural angle (0) so Rotate()
        // returns false immediately -> Idle.
        SetCurrentFrame(game, waitUntil.Value);
        turret.Update(null);
        Assert.Equal(TurretAIUpdate.TurretAIStates.Idle, turret.State);
    }

    // ------------------------------------------------------------------------------------
    // 6. Version-3 save/load round trip actually walks the module into Attacking (a real
    //    live target, a non-default state) and asserts the state machine's real mutable
    //    state -- _turretAIstate, _waitUntil, _currentTarget -- survives the round trip into
    //    a fresh instance, matching GPL's expectation that a saved/reloaded turret continues
    //    exactly where it left off instead of resetting to ScanningForTargets with the
    //    weapon's target orphaned (see Finding 1: TurretAI::xfer, TurretAI.cpp:343-378,
    //    persists the corresponding live state; the old version-2 format persisted seven
    //    fields belonging to none of this class's actual fields).
    // ------------------------------------------------------------------------------------
    [Fact]
    public void Load_RoundTrip_PreservesTurretStateWaitUntilAndCurrentTarget()
    {
        var game = NewGame();
        var host = SpawnHost(game);
        var targetHost = SpawnHost(game);
        var data = BuildModuleData(naturalTurretAngle: 0);
        var source = new TurretAIUpdate(host, game.GameEngine, data);
        var weapon = AttachWeapon(host, game);

        // An object-type target (not a bare position) so its identity can round-trip.
        weapon.SetTarget(new WeaponTarget(game.GameLogic, targetHost.Id));

        SetCurrentFrame(game, 0);
        source.Update(null); // ScanningForTargets -> Turning
        source.Update(null); // Turning -> Attacking (both hosts at Vector3.Zero: already aligned)
        Assert.Equal(TurretAIUpdate.TurretAIStates.Attacking, source.State);

        using var stream = new MemoryStream();
        using (var writer = new StateWriter(stream, game))
        {
            source.Load(writer);
        }

        stream.Position = 0;

        var destinationHost = SpawnHost(game);
        var destination = new TurretAIUpdate(destinationHost, game.GameEngine, data);
        using (var reader = new StateReader(stream, game))
        {
            destination.Load(reader);
        }

        // The bug this test guards against: without persisting these three fields, a fresh
        // instance would load back as ScanningForTargets (the constructor default) with a
        // null target, not Attacking with the same target -- and the very next tick's
        // `target != _currentTarget` check (Attacking case) would force a spurious re-Turning
        // transition that never happens in a continuous (non-save/loaded) run.
        Assert.Equal(TurretAIUpdate.TurretAIStates.Attacking, destination.State);
        Assert.Equal(source.WaitUntil.Value, destination.WaitUntil.Value);

        var destinationTarget = GetPrivate<WeaponTarget>(destination, "_currentTarget");
        Assert.NotNull(destinationTarget);
        Assert.Equal(WeaponTargetType.Object, destinationTarget.TargetType);
        Assert.Equal(targetHost.Id, destinationTarget.TargetObjectId);
    }

    // ------------------------------------------------------------------------------------
    // 6b. No target (Idle, freshly constructed): round trip must not fabricate a target.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void Load_RoundTrip_WithNoTarget_ReconstructsNullCurrentTarget()
    {
        var game = NewGame();
        var host = SpawnHost(game);
        var data = BuildModuleData(minIdleScanFrames: 2, maxIdleScanFrames: 4);
        var source = new TurretAIUpdate(host, game.GameEngine, data);

        SetCurrentFrame(game, 0);
        source.Update(null); // ScanningForTargets -> Idle (no target, stub finds nothing)
        Assert.Equal(TurretAIUpdate.TurretAIStates.Idle, source.State);

        using var stream = new MemoryStream();
        using (var writer = new StateWriter(stream, game))
        {
            source.Load(writer);
        }

        stream.Position = 0;

        var destinationHost = SpawnHost(game);
        var destination = new TurretAIUpdate(destinationHost, game.GameEngine, data);
        using (var reader = new StateReader(stream, game))
        {
            destination.Load(reader);
        }

        Assert.Equal(TurretAIUpdate.TurretAIStates.Idle, destination.State);
        Assert.Equal(source.WaitUntil.Value, destination.WaitUntil.Value);
        Assert.Null(GetPrivate<WeaponTarget>(destination, "_currentTarget"));
    }

    private static T GetPrivate<T>(object target, string fieldName)
    {
        var field = typeof(TurretAIUpdate).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (T)field!.GetValue(target);
    }
}
