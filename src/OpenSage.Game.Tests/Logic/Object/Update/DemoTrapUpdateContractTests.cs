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
        // GetRelationship reads Player.SetRelationship's table, not the Enemies/Allies
        // list AddEnemy populates (that list is unrelated bookkeeping) - see
        // Player.SetRelationship's doc comment.
        a.SetRelationship(b, RelationshipType.Enemies);
        b.SetRelationship(a, RelationshipType.Enemies);
    }

    /// <summary>
    /// GameObject.GetRelationship (which DemoTrapUpdate's proximity scan calls) short-circuits
    /// to Neutral whenever either object's Team is null - and HeadlessSimGame.SpawnObject never
    /// assigns one (same gap EmpUpdateContractTests documents for its own relationship-gated
    /// cases). Give each object its own singleton team, the same construction
    /// SabotageSupplyCenterCrateCollideContractTests uses, so a real (non-Neutral) relationship
    /// is actually observable.
    /// </summary>
    private static uint NextTestTeamId = 900;

    private static void AssignSingletonTeam(HeadlessSimGame game, GameObject obj, Player owner)
    {
        var id = NextTestTeamId++;
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

    /// <summary>
    /// Steps the game forward (bounded) until the trap's detonation weapon fires, to prove a
    /// later scan tick genuinely reaches detonation - used after a "does not detonate" branch
    /// to prove the earlier non-detonation was the specific filter under test doing its job,
    /// not the scan loop never running at all (e.g. a broken/empty partition query would also
    /// make the "does not detonate" assertion pass vacuously). ScanRate = 100ms is 1 logic
    /// frame at BFME2's 5Hz logic rate (IniParser.Fix64.ScanDurationLogicFrames), so a handful
    /// of extra steps is always enough; the cap just guards against an infinite loop if this
    /// regresses to never detonating.
    /// </summary>
    private static void StepUntilFired(HeadlessSimGame game, GameObject trap, WeaponSlot slot, int maxSteps = 20)
    {
        for (var i = 0; i < maxSteps && !Fired(trap, slot); i++)
        {
            game.Step();
        }

        Assert.True(Fired(trap, slot), $"Trap did not detonate within {maxSteps} steps.");
    }

    [Fact]
    public void FriendlyInRange_NoEnemies_AutoDetonationWithFriendsDisabled_DoesNotDetonate()
    {
        var game = NewGame();
        var trap = game.SpawnObject("DemoTrap", game.CivilianPlayer, Vector3.Zero);
        // Same owner as the trap: not ENEMIES. AutoDetonationWithFriendsInvolved = No (INI),
        // so a friendly in range aborts the scan outright (GPL: friends present, not allowed
        // to detonate with friends -> bail without detonating).
        var friendly = game.SpawnObject("EnemyVehicle", game.CivilianPlayer, new Vector3(10, 0, 0));
        AssignSingletonTeam(game, trap, game.CivilianPlayer);
        AssignSingletonTeam(game, friendly, game.CivilianPlayer);

        // The module's first live tick is the second Step() (SetWakeFrame(None) wakes it one
        // frame after spawn - see EnemyInRange_ProximityMode_DetonatesAndFiresWeapon above).
        game.Step();
        game.Step();

        Assert.False(Fired(trap, WeaponSlot.Tertiary));
        Assert.False(trap.IsDestroyed);

        // Prove the scan loop actually ran and reached the friendly-bailout branch (rather
        // than, say, never scanning at all): with the friendly walked out of range, a genuine
        // enemy in its place should still detonate on a later scan. The friendly has to LEAVE
        // - AutoDetonationWithFriendsInvolved = No means a friendly in range bails the whole
        // scan regardless of who else is standing there, so leaving it put would (correctly)
        // suppress detonation and prove nothing.
        friendly.SetTranslation(new Vector3(500, 0, 0));
        var enemy = game.SpawnObject("EnemyVehicle", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);
        AssignSingletonTeam(game, enemy, game.PlayerManager.NeutralPlayer);
        StepUntilFired(game, trap, WeaponSlot.Tertiary);
        Assert.True(trap.IsDestroyed);
    }

    [Fact]
    public void IgnoredKindOfInRange_DoesNotDetonate()
    {
        var game = NewGame();
        var trap = game.SpawnObject("DemoTrap", game.CivilianPlayer, Vector3.Zero);
        // IgnoreTargetTypes = INFANTRY (INI): an enemy of an ignored kind never counts.
        var infantry = game.SpawnObject("EnemyInfantry", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);
        AssignSingletonTeam(game, trap, game.CivilianPlayer);
        AssignSingletonTeam(game, infantry, game.PlayerManager.NeutralPlayer);

        // The module's first live tick is the second Step() (see
        // EnemyInRange_ProximityMode_DetonatesAndFiresWeapon above).
        game.Step();
        game.Step();

        Assert.False(Fired(trap, WeaponSlot.Tertiary));
        Assert.False(trap.IsDestroyed);

        // Prove the scan loop actually ran and reached the kind-filter branch: a
        // non-ignored enemy showing up should still detonate on a later scan.
        var vehicle = game.SpawnObject("EnemyVehicle", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        AssignSingletonTeam(game, vehicle, game.PlayerManager.NeutralPlayer);
        StepUntilFired(game, trap, WeaponSlot.Tertiary);
        Assert.True(trap.IsDestroyed);
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
        AssignSingletonTeam(game, trap, game.CivilianPlayer);
        AssignSingletonTeam(game, enemy, game.PlayerManager.NeutralPlayer);

        // The module's first live tick is the second Step() (see
        // EnemyInRange_ProximityMode_DetonatesAndFiresWeapon above).
        game.Step();
        game.Step();

        Assert.False(Fired(trap, WeaponSlot.Tertiary));
        Assert.False(trap.IsDestroyed);

        // Prove the scan loop actually ran and reached the airborne-filter branch: bringing
        // the same enemy down to the ground should still detonate on a later scan.
        enemy.SetTranslation(new Vector3(10, 0, 0));
        StepUntilFired(game, trap, WeaponSlot.Tertiary);
        Assert.True(trap.IsDestroyed);
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
