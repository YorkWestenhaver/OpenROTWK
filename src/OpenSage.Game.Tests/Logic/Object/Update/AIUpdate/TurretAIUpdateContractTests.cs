// R13.5 contract tests for TurretAIUpdate's ControlledWeaponSlots filtering and for AIUpdate's
// dual-turret (Turret + AltTurret) instantiation. Before this change ControlledWeaponSlots was
// parsed and never read, and AltTurret was parsed and never instantiated, so both turrets of a
// dual-turret object would have tracked the object's single CurrentWeapon target.
//
// These drive the modules' internal Update(BitArray<AutoAcquireEnemiesType>) directly against a
// real GameObject hosted by HeadlessSimGame, with the logic frame advanced by hand, matching the
// harness style of TurretAIUpdateTests.

using System.IO;
using System.Numerics;
using System.Reflection;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Mathematics;
using Xunit;
using OwnerAIUpdate = OpenSage.Logic.Object.AIUpdate;

namespace OpenSage.Tests.Logic.Object.Update.AIUpdate;

public class TurretAIUpdateContractTests
{
    private const string ObjectDefinitions = @"
Object TurretSlotTestUnit
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    /// <summary>Fast enough that a single Rotate() step always snaps straight onto the target yaw.</summary>
    private const float SnapTurnRate = 10f;

    private static HeadlessSimGame NewGame(uint seed = 1)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(ObjectDefinitions);
        return game;
    }

    private static GameObject SpawnHost(HeadlessSimGame game) =>
        game.SpawnObject("TurretSlotTestUnit", game.CivilianPlayer, Vector3.Zero);

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

    private static TurretAIUpdateModuleData BuildTurretData(params WeaponSlot[] controlledWeaponSlots)
    {
        var data = new TurretAIUpdateModuleData();
        SetProp(data, nameof(TurretAIUpdateModuleData.TurretTurnRate), SnapTurnRate);
        SetProp(data, nameof(TurretAIUpdateModuleData.NaturalTurretAngle), 0);
        SetProp(data, nameof(TurretAIUpdateModuleData.MinIdleScanInterval), new LogicFrameSpan(2));
        SetProp(data, nameof(TurretAIUpdateModuleData.MaxIdleScanInterval), new LogicFrameSpan(4));
        SetProp(data, nameof(TurretAIUpdateModuleData.RecenterTime), new LogicFrameSpan(3));

        if (controlledWeaponSlots.Length > 0)
        {
            SetProp(
                data,
                nameof(TurretAIUpdateModuleData.ControlledWeaponSlots),
                new BitArray<WeaponSlot>(controlledWeaponSlots));
        }

        return data;
    }

    private static AIUpdateModuleData BuildAIData(
        TurretAIUpdateModuleData turret,
        TurretAIUpdateModuleData altTurret,
        bool turretsLinked = false)
    {
        var data = new AIUpdateModuleData();
        SetProp(data, nameof(AIUpdateModuleData.Turret), turret);
        SetProp(data, nameof(AIUpdateModuleData.AltTurret), altTurret);
        SetProp(data, nameof(AIUpdateModuleData.TurretsLinked), turretsLinked);
        return data;
    }

    /// <summary>Puts a real Weapon in <paramref name="slot"/> so CurrentWeapon/SetTarget work there.</summary>
    private static Weapon AttachWeapon(GameObject gameObject, HeadlessSimGame game, WeaponSlot slot)
    {
        var weaponsField = typeof(WeaponSet).GetField("_weapons", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(weaponsField);
        var weapons = (Weapon[])weaponsField!.GetValue(gameObject.ActiveWeaponSet);
        var weapon = new Weapon(gameObject, new WeaponTemplate(), slot, game.GameEngine);
        weapons[(int)slot] = weapon;
        return weapon;
    }

    /// <summary>Walks a turret from its constructed state through Turning into Attacking.</summary>
    private static void TickIntoAttacking(TurretAIUpdate turret)
    {
        turret.Update(null); // ScanningForTargets -> Turning (target already acquired)
        turret.Update(null); // Turning -> Attacking (SnapTurnRate aligns in one step)
    }

    // ------------------------------------------------------------------------------------
    // 1. ControlledWeaponSlots names a slot other than the object's current weapon: the turret
    //    must aim for its own slot's weapon, not for CurrentWeapon. This is the whole point of
    //    the field - e.g. a mounted archer whose bow (SECONDARY) sits on the turret while the
    //    sword (PRIMARY) does not.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void ControlledWeaponSlots_TracksItsOwnSlotsWeapon_NotTheCurrentWeapon()
    {
        var game = NewGame();
        var host = SpawnHost(game);
        var turret = new TurretAIUpdate(host, game.GameEngine, BuildTurretData(WeaponSlot.Secondary));

        var primary = AttachWeapon(host, game, WeaponSlot.Primary);
        var secondary = AttachWeapon(host, game, WeaponSlot.Secondary);

        // Primary (the object's current weapon) points straight ahead; secondary points 90 degrees
        // to the side. A turret that ignored ControlledWeaponSlots would end up at yaw 0.
        primary.SetTarget(new WeaponTarget(new Vector3(10f, 0f, 0f)));
        secondary.SetTarget(new WeaponTarget(new Vector3(0f, 10f, 0f)));

        Assert.Same(host.CurrentWeapon, primary);
        Assert.Same(secondary, turret.ControlledWeapon);

        SetCurrentFrame(game, 0);
        TickIntoAttacking(turret);

        Assert.Equal(TurretAIUpdate.TurretAIStates.Attacking, turret.State);
        Assert.Equal(MathUtility.PiOver2, turret.TurretYaw, 4);
    }

    // ------------------------------------------------------------------------------------
    // 1b. The slot filter is a filter, not a preference: if no weapon occupies any slot this
    //     turret controls, it has no target at all and idles rather than borrowing the current
    //     weapon's target.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void ControlledWeaponSlots_NoWeaponInAControlledSlot_HasNoTargetAndIdles()
    {
        var game = NewGame();
        var host = SpawnHost(game);
        var turret = new TurretAIUpdate(host, game.GameEngine, BuildTurretData(WeaponSlot.Tertiary));

        var primary = AttachWeapon(host, game, WeaponSlot.Primary);
        primary.SetTarget(new WeaponTarget(new Vector3(10f, 0f, 0f)));

        Assert.Null(turret.ControlledWeapon);

        SetCurrentFrame(game, 0);
        turret.Update(null);

        // ScanningForTargets with no target of its own, and the scan stub finds nothing -> Idle.
        Assert.Equal(TurretAIUpdate.TurretAIStates.Idle, turret.State);
        Assert.Equal(0f, turret.TurretYaw, 4);
    }

    // ------------------------------------------------------------------------------------
    // 1c. An omitted ControlledWeaponSlots restricts nothing: the many single-turret INI blocks
    //     that never name their slots must keep tracking the current weapon.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void ControlledWeaponSlots_Unspecified_TracksTheCurrentWeapon()
    {
        var game = NewGame();
        var host = SpawnHost(game);
        var turret = new TurretAIUpdate(host, game.GameEngine, BuildTurretData());

        var primary = AttachWeapon(host, game, WeaponSlot.Primary);
        primary.SetTarget(new WeaponTarget(new Vector3(0f, 10f, 0f)));

        Assert.Same(primary, turret.ControlledWeapon);

        SetCurrentFrame(game, 0);
        TickIntoAttacking(turret);

        Assert.Equal(TurretAIUpdate.TurretAIStates.Attacking, turret.State);
        Assert.Equal(MathUtility.PiOver2, turret.TurretYaw, 4);
    }

    // ------------------------------------------------------------------------------------
    // 2. AltTurret is instantiated, and the two turrets track independently: each aims for the
    //    target of the weapon in the slot it controls, and each keeps its own angle (the main
    //    turret is the one the object's TurretYaw represents).
    // ------------------------------------------------------------------------------------
    [Fact]
    public void DualTurrets_EachTrackItsOwnSlotsTargetIndependently()
    {
        var game = NewGame();
        var host = SpawnHost(game);
        var aiUpdate = new OwnerAIUpdate(
            host,
            game.GameEngine,
            BuildAIData(BuildTurretData(WeaponSlot.Primary), BuildTurretData(WeaponSlot.Secondary)));

        var mainTurret = aiUpdate.GetTurretAIUpdate(OwnerAIUpdate.WhichTurretType.Main);
        var altTurret = aiUpdate.GetTurretAIUpdate(OwnerAIUpdate.WhichTurretType.Alt);
        Assert.NotNull(mainTurret);
        Assert.NotNull(altTurret);

        var primary = AttachWeapon(host, game, WeaponSlot.Primary);
        var secondary = AttachWeapon(host, game, WeaponSlot.Secondary);
        primary.SetTarget(new WeaponTarget(new Vector3(10f, 0f, 0f)));   // yaw 0
        secondary.SetTarget(new WeaponTarget(new Vector3(0f, 10f, 0f))); // yaw +90 degrees

        Assert.Same(primary, mainTurret.ControlledWeapon);
        Assert.Same(secondary, altTurret.ControlledWeapon);

        SetCurrentFrame(game, 0);
        TickIntoAttacking(mainTurret);
        TickIntoAttacking(altTurret);

        Assert.Equal(TurretAIUpdate.TurretAIStates.Attacking, mainTurret.State);
        Assert.Equal(TurretAIUpdate.TurretAIStates.Attacking, altTurret.State);

        // The bug this guards: both turrets ending up on the same (current weapon's) target.
        Assert.Equal(0f, mainTurret.TurretYaw, 4);
        Assert.Equal(MathUtility.PiOver2, altTurret.TurretYaw, 4);

        // Only the main turret writes through to the object's single turret angle.
        Assert.Equal(mainTurret.TurretYaw, host.TurretYaw, 4);
    }

    // ------------------------------------------------------------------------------------
    // 3. TurretsLinked collapses the slot filter (GPL TurretAI::isWeaponSlotOkToFire returns
    //    true unconditionally when the owner's turrets are linked): both turrets fire with, and
    //    aim at, the owner's current weapon's target, so they share target and angle.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void LinkedTurrets_ShareTheCurrentWeaponsTargetAndAngle()
    {
        var game = NewGame();
        var host = SpawnHost(game);
        var aiUpdate = new OwnerAIUpdate(
            host,
            game.GameEngine,
            BuildAIData(
                BuildTurretData(WeaponSlot.Primary),
                BuildTurretData(WeaponSlot.Secondary),
                turretsLinked: true));

        Assert.True(aiUpdate.AreTurretsLinked);

        var mainTurret = aiUpdate.GetTurretAIUpdate(OwnerAIUpdate.WhichTurretType.Main);
        var altTurret = aiUpdate.GetTurretAIUpdate(OwnerAIUpdate.WhichTurretType.Alt);

        var primary = AttachWeapon(host, game, WeaponSlot.Primary);
        var secondary = AttachWeapon(host, game, WeaponSlot.Secondary);
        primary.SetTarget(new WeaponTarget(new Vector3(0f, 10f, 0f)));  // yaw +90 degrees
        secondary.SetTarget(new WeaponTarget(new Vector3(10f, 0f, 0f))); // yaw 0

        // Linked: the alt turret ignores its own SECONDARY slot and follows the current weapon.
        Assert.Same(primary, mainTurret.ControlledWeapon);
        Assert.Same(primary, altTurret.ControlledWeapon);

        SetCurrentFrame(game, 0);
        TickIntoAttacking(mainTurret);
        TickIntoAttacking(altTurret);

        Assert.Equal(MathUtility.PiOver2, mainTurret.TurretYaw, 4);
        Assert.Equal(mainTurret.TurretYaw, altTurret.TurretYaw, 4);
    }

    // ------------------------------------------------------------------------------------
    // 4. The alt turret's angle is state of its own (it has no home on GameObject), so it has to
    //    survive a save/load round trip like the rest of the module's state.
    // ------------------------------------------------------------------------------------
    [Fact]
    public void Load_RoundTrip_PreservesAltTurretYaw()
    {
        var game = NewGame();
        var host = SpawnHost(game);
        var aiUpdate = new OwnerAIUpdate(
            host,
            game.GameEngine,
            BuildAIData(BuildTurretData(WeaponSlot.Primary), BuildTurretData(WeaponSlot.Secondary)));

        var source = aiUpdate.GetTurretAIUpdate(OwnerAIUpdate.WhichTurretType.Alt);

        AttachWeapon(host, game, WeaponSlot.Primary).SetTarget(new WeaponTarget(new Vector3(10f, 0f, 0f)));
        AttachWeapon(host, game, WeaponSlot.Secondary).SetTarget(new WeaponTarget(new Vector3(0f, 10f, 0f)));

        SetCurrentFrame(game, 0);
        TickIntoAttacking(source);
        Assert.Equal(MathUtility.PiOver2, source.TurretYaw, 4);

        using var stream = new MemoryStream();
        using (var writer = new StateWriter(stream, game))
        {
            source.Load(writer);
        }

        stream.Position = 0;

        var destinationHost = SpawnHost(game);
        var destinationAI = new OwnerAIUpdate(
            destinationHost,
            game.GameEngine,
            BuildAIData(BuildTurretData(WeaponSlot.Primary), BuildTurretData(WeaponSlot.Secondary)));
        var destination = destinationAI.GetTurretAIUpdate(OwnerAIUpdate.WhichTurretType.Alt);

        using (var reader = new StateReader(stream, game))
        {
            destination.Load(reader);
        }

        Assert.Equal(TurretAIUpdate.TurretAIStates.Attacking, destination.State);
        Assert.Equal(source.TurretYaw, destination.TurretYaw, 4);

        // The alt turret's yaw is genuinely its own: it never touched the object's turret angle.
        Assert.Equal(0f, destinationHost.TurretYaw, 4);
    }
}
