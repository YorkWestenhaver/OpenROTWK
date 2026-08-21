// R12 port contract tests for NeutronMissileUpdate (legacy/IGameEngine surface; see the
// module header for why - Update/AIUpdate is not yet in the SimCore Fix64 quarantine).
// Drives the module directly against a real GameObject hosted by HeadlessSimGame (so
// GameClient/Drawable exist and GetWeaponLaunchBoneTransform/InstanceMatrix work), with
// the logic frame advanced by hand for exact per-frame control, mirroring the
// TurretAIUpdateTests pattern for the same legacy substrate.
//
// One behavioral discrepancy from the task packet, filed not invented: the GPL doAttack()
// computes an `angleCoeff` from the turn angle but never actually applies it to speed -
// it is dead code in the reference C++ (GeneralsMD GameLogic/Object/Update/
// NeutronMissileUpdate.cpp, doAttack()). This port faithfully omits using it too, so the
// turn-rate-clamp test below checks only the clamped heading, not a speed reduction.

using System.Numerics;
using System.Reflection;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class NeutronMissileUpdateContractTests
{
    private const string ObjectDefinitions = @"
Object Launcher
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = PhysicsBehavior ModuleTag_Physics
  End
End

Object Enemy
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object TestMissile
  KindOf = PROJECTILE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 1)
    {
        var game = new HeadlessSimGame(SageGame.CncGeneralsZeroHour, seed);
        game.LoadIniText(ObjectDefinitions);
        return game;
    }

    private static GameObject SpawnLauncher(HeadlessSimGame game) =>
        game.SpawnObject("Launcher", game.CivilianPlayer, Vector3.Zero);

    private static GameObject SpawnEnemy(HeadlessSimGame game) =>
        game.SpawnObject("Enemy", game.CivilianPlayer, new Vector3(1f, 1f, 1f));

    private static GameObject SpawnMissileHost(HeadlessSimGame game) =>
        game.SpawnObject("TestMissile", game.CivilianPlayer, Vector3.Zero);

    private static NeutronMissileUpdateModuleData BuildModuleData(
        float distanceToTravelBeforeTurning = 0f,
        float maxTurnRateRadiansPerFrame = 1000f,
        float forwardDamping = 0f,
        float relativeSpeed = 1f,
        float targetFromDirectlyAbove = 0f,
        float specialAccelFactor = 1f,
        uint specialSpeedTimeFrames = 0,
        float specialSpeedHeight = 0f,
        float specialJitterDistance = 0f)
    {
        var data = new NeutronMissileUpdateModuleData();
        SetProp(data, nameof(NeutronMissileUpdateModuleData.DistanceToTravelBeforeTurning), distanceToTravelBeforeTurning);
        SetProp(data, nameof(NeutronMissileUpdateModuleData.MaxTurnRate), maxTurnRateRadiansPerFrame);
        SetProp(data, nameof(NeutronMissileUpdateModuleData.ForwardDamping), forwardDamping);
        SetProp(data, nameof(NeutronMissileUpdateModuleData.RelativeSpeed), relativeSpeed);
        SetProp(data, nameof(NeutronMissileUpdateModuleData.TargetFromDirectlyAbove), targetFromDirectlyAbove);
        SetProp(data, nameof(NeutronMissileUpdateModuleData.SpecialAccelFactor), specialAccelFactor);
        SetProp(data, nameof(NeutronMissileUpdateModuleData.SpecialSpeedTime), new LogicFrameSpan(specialSpeedTimeFrames));
        SetProp(data, nameof(NeutronMissileUpdateModuleData.SpecialSpeedHeight), specialSpeedHeight);
        SetProp(data, nameof(NeutronMissileUpdateModuleData.SpecialJitterDistance), specialJitterDistance);
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

    // ------------------------------------------------------------------------------------
    // 1. Fires from a launcher: PreLaunch -> Launch (on the fire call) -> Attack (on the
    //    first Update tick), positioned at the launch-bone attach point (Identity fallback
    //    in this headless host, so the origin), carrying the launcher's captured velocity,
    //    armed.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void Fire_TransitionsPreLaunchToLaunchToAttack_ArmedWithLauncherVelocity()
    {
        var game = NewGame();
        var launcher = SpawnLauncher(game);
        var launcherPhysics = launcher.FindBehavior<PhysicsBehavior>();
        Assert.NotNull(launcherPhysics);
        launcherPhysics.AddVelocityTo(new Vector3(5f, 0f, 0f));

        var missileHost = SpawnMissileHost(game);
        var data = BuildModuleData();
        var missile = new NeutronMissileUpdate(missileHost, game.GameEngine, data);

        Assert.Equal(NeutronMissileUpdate.MissileState.PreLaunch, missile.State);

        missile.ProjectileLaunchAtObjectOrPosition(
            victim: null,
            victimPos: new Vector3(100f, 0f, 0f),
            launcher: launcher,
            wslot: WeaponSlot.Primary,
            specificBarrelToUse: 0);

        Assert.Equal(NeutronMissileUpdate.MissileState.Launch, missile.State);
        Assert.False(missile.IsArmed);

        SetCurrentFrame(game, 0);
        missile.Update();

        Assert.Equal(NeutronMissileUpdate.MissileState.Attack, missile.State);
        Assert.True(missile.IsArmed);
        Assert.Equal(new Vector3(5f, 0f, 0f), missile.Velocity);
        Assert.Equal(launcher.Id, missile.LauncherId);
    }

    // ------------------------------------------------------------------------------------
    // 2. DistanceToTravelBeforeTurning = 1000: the missile keeps its post-launch heading
    //    frozen while cumulative travel is under the threshold, then steers toward an
    //    off-axis target once the threshold is used up (which happens, in this geometry,
    //    after 1050 units have been travelled).
    // ------------------------------------------------------------------------------------
    [Fact]
    public void DistanceToTravelBeforeTurning_FreezesHeadingUntilThresholdConsumed()
    {
        var game = NewGame();
        var launcher = SpawnLauncher(game);
        var missileHost = SpawnMissileHost(game);
        var data = BuildModuleData(
            distanceToTravelBeforeTurning: 1000f,
            maxTurnRateRadiansPerFrame: 1000f, // effectively unlimited once turning starts
            relativeSpeed: 50f);
        var missile = new NeutronMissileUpdate(missileHost, game.GameEngine, data);

        missile.ProjectileLaunchAtObjectOrPosition(
            victim: null,
            victimPos: new Vector3(0f, 10000f, 0f), // far off-axis from the launch heading
            launcher: launcher,
            wslot: WeaponSlot.Primary,
            specificBarrelToUse: 0);

        SetCurrentFrame(game, 0);
        missile.Update(); // Launch -> Attack

        var frozenHeading = missileHost.LookDirection;

        // Ticks 1-6: cumulative travel is 50+100+150+200+250+300 = 1050 units by the end of
        // tick 6, but the *check* uses noTurnDistLeft as of the *start* of each tick, which
        // is still > 0 through tick 6 (last value checked: 250 at the start of tick 6) -
        // heading stays frozen for all of ticks 1-6.
        for (uint frame = 1; frame <= 6; frame++)
        {
            SetCurrentFrame(game, frame);
            missile.Update();
            Assert.Equal(frozenHeading, missileHost.LookDirection);
        }

        // Tick 7: noTurnDistLeft went negative during tick 6's bookkeeping, so this tick
        // steers - the heading changes.
        SetCurrentFrame(game, 7);
        missile.Update();
        Assert.NotEqual(frozenHeading, missileHost.LookDirection);
    }

    // ------------------------------------------------------------------------------------
    // 3. Steering respects MaxTurnRate: turning toward a target that requires more than one
    //    frame's worth of turn clamps the heading change to exactly maxTurnRate for that
    //    frame (checked via the dot-product angle, independent of turn-axis details).
    // ------------------------------------------------------------------------------------
    [Fact]
    public void Steering_ClampsHeadingChangeToMaxTurnRate()
    {
        var game = NewGame();
        var launcher = SpawnLauncher(game);
        var missileHost = SpawnMissileHost(game);
        const float maxTurnRate = 0.3f; // radians/frame; well under the 45-degree full turn needed
        var data = BuildModuleData(
            distanceToTravelBeforeTurning: 0f,
            maxTurnRateRadiansPerFrame: maxTurnRate,
            relativeSpeed: 10f);
        var missile = new NeutronMissileUpdate(missileHost, game.GameEngine, data);

        missile.ProjectileLaunchAtObjectOrPosition(
            victim: null,
            victimPos: new Vector3(1000f, 1000f, 0f), // 45 degrees off the post-launch +X heading
            launcher: launcher,
            wslot: WeaponSlot.Primary,
            specificBarrelToUse: 0);

        SetCurrentFrame(game, 0);
        missile.Update(); // Launch -> Attack

        var headingBeforeSteering = missileHost.LookDirection;

        SetCurrentFrame(game, 1);
        missile.Update(); // first steering tick: clamp to maxTurnRate

        var cosTurned = Vector3.Dot(headingBeforeSteering, missileHost.LookDirection);
        var angleTurned = System.MathF.Acos(System.Math.Clamp(cosTurned, -1f, 1f));
        Assert.Equal(maxTurnRate, angleTurned, 3);
    }

    // ------------------------------------------------------------------------------------
    // 4. TargetFromDirectlyAbove = 500, target at (0,0,100): the missile approaches the
    //    intermediate position (0,0,600) first (climbing straight up, since it launches
    //    from the origin directly below it), then - once it arrives - velocity flips to a
    //    halved-speed, straight-down approach toward the real target.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void TargetFromDirectlyAbove_ApproachesIntermediatePositionThenDivesToTarget()
    {
        var game = NewGame();
        var launcher = SpawnLauncher(game);
        var missileHost = SpawnMissileHost(game);
        var data = BuildModuleData(
            distanceToTravelBeforeTurning: 0f,
            maxTurnRateRadiansPerFrame: 1000f,
            relativeSpeed: 100f,
            targetFromDirectlyAbove: 500f);
        var missile = new NeutronMissileUpdate(missileHost, game.GameEngine, data);

        missile.ProjectileLaunchAtObjectOrPosition(
            victim: null,
            victimPos: new Vector3(0f, 0f, 100f),
            launcher: launcher,
            wslot: WeaponSlot.Primary,
            specificBarrelToUse: 0);

        SetCurrentFrame(game, 0);
        missile.Update(); // Launch -> Attack, at (0,0,0)

        Assert.False(missile.ReachedIntermediatePosition);

        // Frames 1-4 climb straight up (0 -> 100 -> 300 -> 600); the 4th Update call's
        // top-of-frame check lands exactly on the intermediate position (0,0,600) and
        // flips to the straight-down dive within that same tick.
        for (uint frame = 1; frame <= 4; frame++)
        {
            SetCurrentFrame(game, frame);
            missile.Update();
        }

        Assert.True(missile.ReachedIntermediatePosition);
        Assert.Equal(400f, missileHost.Translation.Z, 2); // 600 (snap) - 200 (this tick's dive)
        Assert.True(missile.Velocity.Z < 0f); // now heading down toward the real target
    }

    // ------------------------------------------------------------------------------------
    // 5. An armed missile detonates on collision: state -> Dead, marked for killing
    //    (Hidden, and NoCollisions status set), delivery decal cleared, returns true. Its
    //    own launcher is exempt from triggering a detonation.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void ArmedMissile_CollidesWithEnemy_DetonatesAndReturnsTrue()
    {
        var game = NewGame();
        var launcher = SpawnLauncher(game);
        var enemy = SpawnEnemy(game);
        var missileHost = SpawnMissileHost(game);
        var data = BuildModuleData();
        var missile = new NeutronMissileUpdate(missileHost, game.GameEngine, data);

        missile.ProjectileLaunchAtObjectOrPosition(
            victim: null,
            victimPos: new Vector3(100f, 0f, 0f),
            launcher: launcher,
            wslot: WeaponSlot.Primary,
            specificBarrelToUse: 0);

        SetCurrentFrame(game, 0);
        missile.Update(); // Launch -> Attack, armed

        // Hitting the launcher itself is a no-op.
        Assert.True(missile.HandleCollision(launcher));
        Assert.Equal(NeutronMissileUpdate.MissileState.Attack, missile.State);

        var result = missile.HandleCollision(enemy);

        Assert.True(result);
        Assert.Equal(NeutronMissileUpdate.MissileState.Dead, missile.State);
        Assert.False(missile.DeliveryDecalActive);
        Assert.True(missileHost.Hidden);
        Assert.True(missileHost.TestStatus(ObjectStatus.NoCollisions));
    }

    // ------------------------------------------------------------------------------------
    // 6. Special-acceleration launch phase: for SpecialSpeedTime frames, height climbs by
    //    the closed-form (accelFactor * timeFrac)^2 / accelFactor * SpecialSpeedHeight
    //    curve (accelFactor = 1 here, so height = timeFrac^2 * SpecialSpeedHeight), and the
    //    lateral jitter's amplitude bound shrinks as timeFrac approaches 1.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void SpecialAccelerationPhase_ClimbsByClosedFormHeight_JitterAmplitudeShrinks()
    {
        var game = NewGame();
        var launcher = SpawnLauncher(game);
        var missileHost = SpawnMissileHost(game);
        var data = BuildModuleData(
            distanceToTravelBeforeTurning: 0f,
            maxTurnRateRadiansPerFrame: 1000f,
            relativeSpeed: 0f, // isolate the special-phase Z override from ordinary thrust
            specialAccelFactor: 1f,
            specialSpeedTimeFrames: 10,
            specialSpeedHeight: 100f,
            specialJitterDistance: 20f);
        var missile = new NeutronMissileUpdate(missileHost, game.GameEngine, data);

        missile.ProjectileLaunchAtObjectOrPosition(
            victim: null,
            victimPos: new Vector3(100f, 0f, 0f),
            launcher: launcher,
            wslot: WeaponSlot.Primary,
            specificBarrelToUse: 0);

        SetCurrentFrame(game, 0);
        missile.Update(); // Launch -> Attack; frameAtLaunch = 0, heightAtLaunch = 0

        for (uint frame = 1; frame <= 9; frame++)
        {
            SetCurrentFrame(game, frame);
            missile.Update();

            var timeFrac = frame / 10f;
            var expectedZ = timeFrac * timeFrac * 100f;
            Assert.Equal(expectedZ, missileHost.Translation.Z, 1);

            // jitterLocal = (0, r1*amplitude, r2*amplitude) with r1,r2 in [-1,1], rotated
            // into world space (length-preserving) - the worst case is both components at
            // their extreme, giving sqrt(2)*amplitude.
            var amplitude = (1f - timeFrac) * 20f;
            var jitterBound = amplitude * System.MathF.Sqrt(2f);
            var jitter = missileHost.Drawable.InstanceMatrix.Translation;
            Assert.True(jitter.Length() <= jitterBound + 0.01f);
        }
    }

    // ------------------------------------------------------------------------------------
    // 7. If the launch vehicle is destroyed/removed before the missile's first Launch-state
    //    tick fires, the missile detects the null launcher lookup and self-destructs rather
    //    than crashing on a missing launch-bone transform.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void LauncherDestroyedDuringLaunch_MissileSelfDestructs()
    {
        var game = NewGame();
        var launcher = SpawnLauncher(game);
        var missileHost = SpawnMissileHost(game);
        var data = BuildModuleData();
        var missile = new NeutronMissileUpdate(missileHost, game.GameEngine, data);

        missile.ProjectileLaunchAtObjectOrPosition(
            victim: null,
            victimPos: new Vector3(100f, 0f, 0f),
            launcher: launcher,
            wslot: WeaponSlot.Primary,
            specificBarrelToUse: 0);

        Assert.Equal(NeutronMissileUpdate.MissileState.Launch, missile.State);

        // The launcher is gone by the time the missile's first Launch tick runs: destroyed
        // and reaped (DeleteDestroyed), so GetObjectById(_launcherId) returns null - matching
        // what the real per-frame game loop does between destroying an object and the next
        // logic tick.
        game.GameLogic.DestroyObject(launcher);
        game.GameLogic.DeleteDestroyed();

        SetCurrentFrame(game, 0);
        missile.Update();

        Assert.Equal(ObjectId.Invalid, missile.LauncherId);
        Assert.True(missileHost.IsDestroyed);
    }
}
