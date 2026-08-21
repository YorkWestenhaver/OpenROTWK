// Mocked-game unit tests for the AttachUpdate port (R13; api-freeze-v1 §6 fitness item 4):
// one test per behavior branch from research/modules-r13/specs/AttachUpdateModuleData.md §5,
// [create -> tick -> observable effect], plus the shadow-copy base test and a mid-behavior
// save/load round-trip.
//
// The observables are: AttachUpdate.IsAttached/CarrierId (private sim state exposed for tests,
// same convention EmpUpdate's ScaleOf/FieldCapture technique establishes but simpler here since
// the fields are plain bool/ObjectId), the carrier's ObjectStatus.HoldingTheRing flag, and the
// recorded FireRelativeEvaEvent calls (ISimEvents, RecordingSimEvents, extended for this port).
//
// Sleepy-update caveat (spec §5, load-bearing for every frame-counting assertion below): like
// EmpUpdate, AttachUpdate sets UpdateSleepTime.None in its ctor, which schedules its first wake
// at CurrentFrame + 1. HeadlessSimGame.Step() #1 only advances the frame counter from 0 to 1
// without running a tick, so a freshly spawned module's first real Update() call happens on the
// SECOND Step(), not the first.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class AttachUpdateContractTests
{
    private static readonly Vector3 Origin = new(0, 0, 0);

    private const string Definitions = @"
GameData
  Gravity = -1.0
End

Object TestRing
  KindOf = SELECTABLE IMMOBILE CRATE UNATTACKABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1.0
  End
  Behavior = AttachUpdate ModuleTag_Attach
    ObjectFilter          = ANY +PROJECTILE
    ScanRange              = 10
    ParentStatus           = HOLDING_THE_RING
    AlwaysTeleport         = No
    AnchorToTopOfGeometry  = No
    ParentOwnerAttachmentEvaEvent = RingPickedUpLocal
    ParentEnemyAttachmentEvaEvent = RingPickedUpEnemy
    ParentOwnerDiedEvaEvent       = LocalPlayerLosesRing
  End
End

Object TestRingTeleport
  KindOf = SELECTABLE IMMOBILE CRATE UNATTACKABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1.0
  End
  Behavior = AttachUpdate ModuleTag_Attach
    ObjectFilter   = ANY +PROJECTILE
    ScanRange      = 10
    ParentStatus   = HOLDING_THE_RING
    AlwaysTeleport = Yes
    ParentOwnerAttachmentEvaEvent = RingPickedUpLocal
    ParentEnemyAttachmentEvaEvent = RingPickedUpEnemy
    ParentOwnerDiedEvaEvent       = LocalPlayerLosesRing
  End
End

Object TestRingAnchored
  KindOf = SELECTABLE IMMOBILE CRATE UNATTACKABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1.0
  End
  Behavior = AttachUpdate ModuleTag_Attach
    ObjectFilter          = ANY +PROJECTILE
    ScanRange              = 10
    ParentStatus           = HOLDING_THE_RING
    AlwaysTeleport         = Yes
    AnchorToTopOfGeometry  = Yes
    ParentOwnerAttachmentEvaEvent = RingPickedUpLocal
    ParentEnemyAttachmentEvaEvent = RingPickedUpEnemy
    ParentOwnerDiedEvaEvent       = LocalPlayerLosesRing
  End
End

Object TestEligibleCarrier
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object TestEligibleCarrierBigGeometry
  KindOf = INFANTRY
  GeometryMajorRadius = 12
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object TestIneligibleCarrier
  KindOf = PROJECTILE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xA77AC4) // "attach"
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static AttachUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<AttachUpdate>().Single();

    // ---- case 1: no eligible carrier in range -> stays unattached ----

    [Fact]
    public void NoEligibleCarrierInRange_StaysUnattached()
    {
        var game = NewGame();
        var ring = game.SpawnObject("TestRing", game.CivilianPlayer, Origin);
        var module = ModuleOf(ring);

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        Assert.False(module.IsAttached);
        Assert.Equal(ObjectId.Invalid, module.CarrierId);
    }

    // ---- case 2: only an ineligible (filter-matched) candidate present -> never attaches ----

    [Fact]
    public void IneligibleCarrierOnly_NeverAttaches()
    {
        var game = NewGame();
        var ring = game.SpawnObject("TestRing", game.CivilianPlayer, Origin);
        var ineligible = game.SpawnObject("TestIneligibleCarrier", game.PlayerManager.NeutralPlayer, new Vector3(5, 0, 0));
        var module = ModuleOf(ring);

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        Assert.False(module.IsAttached);
        Assert.False(ineligible.TestStatus(ObjectStatus.HoldingTheRing));
    }

    // ---- case 3: eligible carrier attaches on the second step, sets status, fires owner Eva event ----

    [Fact]
    public void EligibleCarrierInRange_AttachesOnSecondStep_SetsStatusAndFiresOwnerEvaEvent()
    {
        var game = NewGame();
        var ring = game.SpawnObject("TestRing", game.CivilianPlayer, Origin);
        var carrier = game.SpawnObject("TestEligibleCarrier", game.CivilianPlayer, new Vector3(5, 0, 0));
        var module = ModuleOf(ring);
        var events = RecordingSimEvents.InstallOn(game);

        game.Step(); // frame 0 -> 1: no tick yet (sleepy-update caveat)
        Assert.False(module.IsAttached);

        game.Step(); // the second step: the module's first real tick runs
        Assert.True(module.IsAttached);
        Assert.Equal(carrier.Id, module.CarrierId);
        Assert.True(carrier.TestStatus(ObjectStatus.HoldingTheRing));

        Assert.Single(events.RelativeEvaEvents);
        var fired = events.RelativeEvaEvents[0];
        Assert.Equal(carrier.Id, fired.PerspectiveOwnerId);
        Assert.Equal("RingPickedUpLocal", fired.OwnerEventName);
        Assert.Null(fired.AlliedEventName);
        Assert.Equal("RingPickedUpEnemy", fired.EnemyEventName);
    }

    // ---- case 4: attachment Eva event is edge-triggered, never repeated ----

    [Fact]
    public void EligibleCarrierInRange_AttachesOnce_NoRepeatEvaEventOnSubsequentTicks()
    {
        var game = NewGame();
        var ring = game.SpawnObject("TestRing", game.CivilianPlayer, Origin);
        var carrier = game.SpawnObject("TestEligibleCarrier", game.CivilianPlayer, new Vector3(5, 0, 0));
        var events = RecordingSimEvents.InstallOn(game);

        game.Step();
        game.Step();
        Assert.Single(events.RelativeEvaEvents);

        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }

        Assert.Single(events.RelativeEvaEvents);
    }

    // ---- case 5: ScanRange is actually enforced, not "first candidate found anywhere" ----

    [Fact]
    public void ScanRangeExcludesFarCandidate()
    {
        var game = NewGame();
        var ring = game.SpawnObject("TestRing", game.CivilianPlayer, Origin); // ScanRange = 10
        var farCarrier = game.SpawnObject("TestEligibleCarrier", game.CivilianPlayer, new Vector3(15, 0, 0));
        var module = ModuleOf(ring);

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        Assert.False(module.IsAttached);
        Assert.False(farCarrier.TestStatus(ObjectStatus.HoldingTheRing));
    }

    // ---- case 6: carrier death drops the Ring, fires the died Eva event, and re-scanning resumes (F-ATU-1) ----

    [Fact]
    public void CarrierDies_DropsRing_FiresOwnerDiedEvaEvent_ReturnsToScanning()
    {
        var game = NewGame();
        var ring = game.SpawnObject("TestRing", game.CivilianPlayer, Origin);
        var carrier = game.SpawnObject("TestEligibleCarrier", game.CivilianPlayer, new Vector3(5, 0, 0));
        var module = ModuleOf(ring);
        var events = RecordingSimEvents.InstallOn(game);

        game.Step();
        game.Step();
        Assert.True(module.IsAttached);
        var carrierId = carrier.Id;

        PortedModuleTestKit.TriggerDeath(carrier);
        game.Step();

        Assert.False(module.IsAttached);
        Assert.False(carrier.TestStatus(ObjectStatus.HoldingTheRing));
        Assert.Contains(events.RelativeEvaEvents, e =>
            e.PerspectiveOwnerId == carrierId &&
            e.OwnerEventName == "LocalPlayerLosesRing" &&
            e.AlliedEventName == null &&
            e.EnemyEventName == null);

        // Re-scan actually resumes: a second eligible carrier in range gets picked up.
        var secondCarrier = game.SpawnObject("TestEligibleCarrier", game.CivilianPlayer, new Vector3(5, 0, 0));
        game.Step();
        game.Step();

        Assert.True(module.IsAttached);
        Assert.Equal(secondCarrier.Id, module.CarrierId);
        Assert.True(secondCarrier.TestStatus(ObjectStatus.HoldingTheRing));
    }

    // ---- case 7: AlwaysTeleport = Yes tracks the carrier's position exactly ----

    [Fact]
    public void AttachedTeleportBranch_TracksCarrierPositionExactly()
    {
        var game = NewGame();
        var ring = game.SpawnObject("TestRingTeleport", game.CivilianPlayer, Origin);
        var carrier = game.SpawnObject("TestEligibleCarrier", game.CivilianPlayer, new Vector3(5, 0, 0));

        game.Step();
        game.Step();
        Assert.Equal(carrier.Transform.Translation, ring.Transform.Translation);

        var moved = new Vector3(40, 20, 0);
        carrier.UpdateTransform(moved);
        carrier.UpdateColliders();
        game.Step();

        Assert.Equal(moved, ring.Transform.Translation);
    }

    // ---- case 8: AlwaysTeleport = No also tracks exactly - F-ATU-3, no smoothing-rate field exists ----

    [Fact]
    public void AttachedNoTeleportBranch_AlsoTracksExactly_F_ATU_3()
    {
        // F-ATU-3 (filed in AttachUpdate.cs): no smoothing-rate field exists anywhere in this
        // module's parse table, so this port snaps in both AlwaysTeleport branches. A future
        // reader who expects lag/smoothing on AlwaysTeleport = No should read this as a filed,
        // known gap, not a bug - see AttachUpdateModuleData.md F-ATU-3, load-bearing for the
        // live AOTR One Ring data, which authors AlwaysTeleport = No.
        var game = NewGame();
        var ring = game.SpawnObject("TestRing", game.CivilianPlayer, Origin); // AlwaysTeleport = No
        var carrier = game.SpawnObject("TestEligibleCarrier", game.CivilianPlayer, new Vector3(5, 0, 0));

        game.Step();
        game.Step();
        Assert.Equal(carrier.Transform.Translation, ring.Transform.Translation);

        var moved = new Vector3(-30, 60, 0);
        carrier.UpdateTransform(moved);
        carrier.UpdateColliders();
        game.Step();

        Assert.Equal(moved, ring.Transform.Translation);
    }

    // ---- case 9: AnchorToTopOfGeometry applies the F-ATU-2 Z-offset approximation ----

    [Fact]
    public void AnchorToTopOfGeometry_AppliesZOffset()
    {
        // F-ATU-2 (filed in AttachUpdate.cs): the Z offset is the carrier's
        // SimTransformBridge.PullGeometry(...).MajorRadius - the nearest already-exposed Fix64
        // geometry proxy, not necessarily the "true" top-of-bounding-box height. This test
        // pins the actual formula this port implements.
        var game = NewGame();
        var ring = game.SpawnObject("TestRingAnchored", game.CivilianPlayer, Origin);
        var carrier = game.SpawnObject("TestEligibleCarrierBigGeometry", game.CivilianPlayer, new Vector3(5, 0, 10));

        game.Step();
        game.Step();

        Assert.Equal(carrier.Transform.Translation.X, ring.Transform.Translation.X, precision: 4);
        Assert.Equal(carrier.Transform.Translation.Y, ring.Transform.Translation.Y, precision: 4);
        Assert.Equal(carrier.Transform.Translation.Z + carrier.Geometry.MajorRadius, ring.Transform.Translation.Z, precision: 4);
    }

    // ---- shadow-copy + save/load round-trip ----

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var ring = game.SpawnObject("TestRing", game.CivilianPlayer, Origin);
        var carrier = game.SpawnObject("TestEligibleCarrier", game.CivilianPlayer, new Vector3(5, 0, 0));
        var live = ModuleOf(ring);

        game.Step();
        game.Step();
        Assert.True(live.IsAttached);

        var shadowHost = game.SpawnObject("TestRing", game.CivilianPlayer, new Vector3(300, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically_PreAttach()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 0);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically_PostAttach()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryC = RunScenario(roundTripAtFrame: 3);
        Assert.Equal(trajectoryA, trajectoryC);
    }

    /// <summary>Records (IsAttached, CarrierId, Z) at each step, optionally round-tripping the
    /// module through Save/Load mid-trajectory. Two separate round-trip points are exercised by
    /// the two tests above (before AND after the attach transition, at step index 2) since
    /// _carrierId/_attached round-tripping correctly both pre- and post-attach are two distinct
    /// correctness claims (spec §5 case 11).</summary>
    private static (bool Attached, ObjectId CarrierId, float Z)[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xF00D);
        var ring = game.SpawnObject("TestRingTeleport", game.CivilianPlayer, Origin);
        var carrier = game.SpawnObject("TestEligibleCarrier", game.CivilianPlayer, new Vector3(5, 0, 0));
        var module = ModuleOf(ring);

        var trajectory = new (bool, ObjectId, float)[6];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk;
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            game.Step();
            trajectory[i] = (module.IsAttached, module.CarrierId, ring.Transform.Translation.Z);
        }

        return trajectory;
    }
}
