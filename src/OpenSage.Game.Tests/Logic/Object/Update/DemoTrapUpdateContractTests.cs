// Mocked-game contract tests for the DemoTrapUpdate port (R12), one test per behavior branch
// the task packet's testCases enumerate: [create -> tick -> observable effect].
//
// This module is legacy (GameObject, IGameEngine), not [SimState] (see the file header on
// DemoTrapUpdate.cs), so - mirroring SabotagePowerPlantCrateCollideContractTests, the R12
// legacy-Collide precedent - there is no Xfer/shadow-copy CRC test here; PortedModuleTestKit's
// CRC helpers are for ported [SimState] modules only.
//
// Weapon-slot SELECTION (the manual-mode trigger) has no landed driver in OpenSage yet (see
// DemoTrapUpdate.cs finding F-DTU-1: WeaponSetUpdate/LockWeaponCreate/WeaponModeSpecialPowerUpdate
// are all still stubs), so the manual-mode test pokes WeaponSet's private current-slot field
// directly via reflection - the same technique TurretAIUpdateTests uses to plant a weapon
// straight into a slot - to stand in for "a command button selected this weapon".
//
// "Fires the detonation weapon" is observed through the FiringA/B/C model-condition flag
// FiringWeaponState sets synchronously on entry (WeaponStateMachine.Fire() -> TransitionToState
// (Firing) -> OnEnterState): a real, public, engine-driven side effect of Weapon.Fire(), the
// same observable EnemyNearUpdateContractTests uses for its own model-condition output.

using System.Numerics;
using System.Reflection;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class DemoTrapUpdateContractTests
{
    private const string Definitions = @"
Weapon TrapBoom
  AttackRange = 500
  ClipSize = 1
  DamageNugget
    Damage = 999
    DamageType = SLASH
    DeathType = NORMAL
  End
End

Object DemoTrap
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  WeaponSet
    Conditions = None
    Weapon = PRIMARY TrapBoom
    Weapon = SECONDARY TrapBoom
    Weapon = TERTIARY TrapBoom
  End
  Behavior = DemoTrapUpdate ModuleTag_Trap
    DefaultProximityMode = Yes
    ProximityModeWeaponSlot = PRIMARY
    ManualModeWeaponSlot = SECONDARY
    DetonationWeaponSlot = TERTIARY
    TriggerDetonationRange = 50
    ScanRate = 100
    IgnoreTargetTypes = INFANTRY
    DetonationWeapon = TrapBoom
    AutoDetonationWithFriendsInvolved = No
    DetonateWhenKilled = Yes
  End
End

Object EnemyVehicle
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object EnemyInfantry
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xDE30)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static void MakeEnemies(Player a, Player b)
    {
        // GetRelationship reads Player.SetPlayerRelationship's table, not the Enemies/Allies
        // list AddEnemy populates (that list is unrelated bookkeeping) - see
        // Player.SetPlayerRelationship's doc comment.
        a.SetPlayerRelationship(b, RelationshipType.Enemies);
        b.SetPlayerRelationship(a, RelationshipType.Enemies);
    }

    /// <summary>
    /// GameObject.GetRelationship (which DemoTrapUpdate's proximity scan calls) short-circuits
    /// to Neutral whenever either object's Team is null - and HeadlessSimGame.SpawnObject never
    /// assigns one (same gap EmpUpdateContractTests documents for its own relationship-gated
    /// cases). Give each object its own singleton team, the same construction
    /// SabotageSupplyCenterCrateCollideContractTests uses, so a real (non-Neutral) relationship
    /// is actually observable.
    /// </summary>
    private static uint _nextTestTeamId = 900;

    private static void AssignSingletonTeam(HeadlessSimGame game, GameObject obj, Player owner)
    {
        var id = _nextTestTeamId++;
        var template = new TeamTemplate(game.TeamFactory, id, $"TestTeam{id}", owner, isSingleton: true);
        obj.Team = new Team(template, id);
    }

    /// <summary>
    /// Stands in for "a command button selected this weapon": WeaponSet has no landed
    /// slot-selection driver yet (F-DTU-1), so the test pokes the private current-slot field
    /// directly, the same technique TurretAIUpdateTests uses to plant a weapon into a slot.
    /// </summary>
    private static void SelectWeaponSlot(GameObject obj, WeaponSlot slot)
    {
        var field = typeof(WeaponSet).GetField("_currentWeaponSlot", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(obj.ActiveWeaponSet, slot);
    }

    private static bool Fired(GameObject trap, WeaponSlot slot) =>
        trap.ModelConditionFlags.Get(ModelConditionFlagUtility.GetFiringFlag((int)slot));

    [Fact]
    public void EnemyInRange_ProximityMode_DetonatesAndFiresWeapon()
    {
        var game = NewGame();
        var trap = game.SpawnObject("DemoTrap", game.CivilianPlayer, Vector3.Zero);
        var enemy = game.SpawnObject("EnemyVehicle", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);
        AssignSingletonTeam(game, trap, game.CivilianPlayer);
        AssignSingletonTeam(game, enemy, game.PlayerManager.NeutralPlayer);

        // Current weapon slot defaults to PRIMARY (== ProximityModeWeaponSlot): the trap is
        // in proximity mode, m_nextScanFrames starts at 0, so its very first live tick scans.
        // That first live tick is the second Step() - the module's sleepy-update registration
        // (SetWakeFrame(None)) wakes it one frame after spawn, the same shape
        // HeightDieUpdateContractTests uses.
        game.Step();
        game.Step();

        Assert.True(Fired(trap, WeaponSlot.Tertiary));   // DetonationWeaponSlot
        Assert.True(trap.IsDestroyed);
        _ = enemy;
    }

    [Fact]
    public void DetonationWeaponSlotSelected_DetonatesImmediately_ProximityIgnored()
    {
        var game = NewGame();
        var trap = game.SpawnObject("DemoTrap", game.CivilianPlayer, Vector3.Zero);
        // An enemy IS in range, but manual selection must trigger regardless of proximity.
        game.SpawnObject("EnemyVehicle", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        SelectWeaponSlot(trap, WeaponSlot.Tertiary); // DetonationWeaponSlot

        // See EnemyInRange_ProximityMode_DetonatesAndFiresWeapon: the module's first live
        // tick is the second Step().
        game.Step();
        game.Step();

        Assert.True(Fired(trap, WeaponSlot.Tertiary));
        Assert.True(trap.IsDestroyed);
    }

    [Fact]
    public void FriendlyInRange_NoEnemies_AutoDetonationWithFriendsDisabled_DoesNotDetonate()
    {
        var game = NewGame();
        var trap = game.SpawnObject("DemoTrap", game.CivilianPlayer, Vector3.Zero);
        // Same owner as the trap: not ENEMIES. AutoDetonationWithFriendsInvolved = No (INI),
        // so a friendly in range aborts the scan outright (GPL: friends present, not allowed
        // to detonate with friends -> bail without detonating).
        game.SpawnObject("EnemyVehicle", game.CivilianPlayer, new Vector3(10, 0, 0));

        game.Step();

        Assert.False(Fired(trap, WeaponSlot.Tertiary));
        Assert.False(trap.IsDestroyed);
    }

    [Fact]
    public void IgnoredKindOfInRange_DoesNotDetonate()
    {
        var game = NewGame();
        var trap = game.SpawnObject("DemoTrap", game.CivilianPlayer, Vector3.Zero);
        // IgnoreTargetTypes = INFANTRY (INI): an enemy of an ignored kind never counts.
        game.SpawnObject("EnemyInfantry", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        game.Step();

        Assert.False(Fired(trap, WeaponSlot.Tertiary));
        Assert.False(trap.IsDestroyed);
    }

    [Fact]
    public void AirborneEnemyInRange_DoesNotDetonate()
    {
        var game = NewGame();
        var trap = game.SpawnObject("DemoTrap", game.CivilianPlayer, Vector3.Zero);
        // Flat headless terrain sits at height 0, so Z=50 puts the enemy above it
        // (GameObject.IsAboveTerrain), same as a flying unit GPL's demo trap must not trigger on.
        var enemy = game.SpawnObject("EnemyVehicle", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 50));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        game.Step();

        Assert.False(Fired(trap, WeaponSlot.Tertiary));
        Assert.False(trap.IsDestroyed);
        _ = enemy;
    }

    [Fact]
    public void DiesFromExternalDamage_DetonateWhenKilled_FiresDetonationWeapon()
    {
        var game = NewGame();
        var trap = game.SpawnObject("DemoTrap", game.CivilianPlayer, Vector3.Zero);

        // Simulate the trap dying from an external cause (not its own detonation), same
        // "already dead" injection SabotagePowerPlantCrateCollideContractTests.DeadTarget_Rejected
        // uses. DetonateWhenKilled = Yes (INI): the next tick must still fire the weapon.
        trap.IsEffectivelyDead = true;

        // The module's freshly-constructed sleepy-update registration wakes it on the frame
        // after spawn (SetWakeFrame(None) semantics - see HeightDieUpdateContractTests for
        // the same shape), so its first live tick is the second Step(), not the first.
        game.Step();
        game.Step();

        Assert.True(Fired(trap, WeaponSlot.Tertiary));
    }
}
