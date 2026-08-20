// Mocked-game contract tests for the BunkerBusterBehavior port (R12), one test per behavior
// branch the task packet's testCases enumerate: [create -> tick/kill -> observable effect].
//
// This module is legacy (GameObject, IGameEngine), not [SimState] (see the file header on
// BunkerBusterBehavior.cs), so - mirroring DemoTrapUpdateContractTests, the R12 legacy-Update
// precedent - there is no Xfer/shadow-copy CRC test here; PortedModuleTestKit's CRC helpers are
// for ported [SimState] modules only.
//
// No Contain-category module is landed yet (every Contain module in this codebase is still
// [ParseOnly]), so GameObject.Contain can't be populated through real INI/object creation. The
// occupant-effect tests inject a minimal test-only IContainModule straight into the private
// backing field via reflection - the same technique DemoTrapUpdateContractTests uses to plant a
// weapon-slot selection - to stand in for "this target is a garrisoned building".
//
// "Fires the shockwave weapon" and "kills all occupants" are both observed the same way
// DemoTrapUpdateContractTests observes weapon fire: the FiringA model-condition flag
// WeaponStateMachine.Fire() sets synchronously on entry, on the weapon's ParentGameObject - which
// is the cached victim (not the missile) once a victim exists, making it double as the victim-
// tracking observable too.

using System;
using System.Numerics;
using System.Reflection;
using OpenSage.FX;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Behaviors;

public class BunkerBusterBehaviorContractTests
{
    private const string Definitions = @"
Upgrade Upgrade_BunkerBust
  Type = PLAYER
End

FXList FX_Crash
End

FXList FX_Detonation
End

Weapon OccupantDamageWeapon
  AttackRange = 500
  ClipSize = 1
  DamageNugget
    Damage = 999
    DamageType = EXPLOSION
    DeathType = NORMAL
  End
End

Weapon ShockwaveWeapon
  AttackRange = 500
  ClipSize = 1
  DamageNugget
    Damage = 1
    DamageType = EXPLOSION
    DeathType = NORMAL
  End
End

Object BunkerBusterMissile
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
  Behavior = BunkerBusterBehavior ModuleTag_BBB
    UpgradeRequired = Upgrade_BunkerBust
    DetonationFX = FX_Detonation
    CrashThroughBunkerFX = FX_Crash
    CrashThroughBunkerFXFrequency = 800
    ShockwaveWeaponTemplate = ShockwaveWeapon
    OccupantDamageWeaponTemplate = OccupantDamageWeapon
  End
End

Object BunkerBusterMissileNoOccupantWeapon
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 10
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
  Behavior = BunkerBusterBehavior ModuleTag_BBB
    UpgradeRequired = Upgrade_BunkerBust
    DetonationFX = FX_Detonation
    ShockwaveWeaponTemplate = ShockwaveWeapon
  End
End

Object Bunker
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
End

Object Occupant
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xB055)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static void GrantUpgrade(HeadlessSimGame game)
    {
        game.CivilianPlayer.AddUpgrade(game.AssetStore.Upgrades.GetByName("Upgrade_BunkerBust"), UpgradeStatus.Completed);
    }

    private static bool Fired(GameObject obj) =>
        obj.ModelConditionFlags.Get(ModelConditionFlagUtility.GetFiringFlag((int)WeaponSlot.Primary));

    /// <summary>
    /// GameObject.Contain has no landed real setter (no Contain-category module is ported yet),
    /// so this reaches straight into the auto-property's compiler-generated backing field - the
    /// same reflection-injection technique DemoTrapUpdateContractTests.SelectWeaponSlot uses for
    /// WeaponSet's private current-slot field.
    /// </summary>
    private static void InjectContain(GameObject obj, IContainModule contain)
    {
        var field = typeof(GameObject).GetField("<Contain>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(obj, contain);
    }

    private sealed class FakeGarrisonContain : IContainModule
    {
        private readonly GameObject[] _items;

        public FakeGarrisonContain(params GameObject[] items) => _items = items;

        public bool IsGarrisonable => true;
        public bool IsImmuneToClearBuildingAttacks => false;
        public bool IsRiderChangeContain => false;
        public uint ContainCount => (uint)_items.Length;
        public float ContainedItemsMass => 0f;
        public ReadOnlySpan<GameObject> ContainedItems => _items;
        public void OrderAllPassengersToIdle(CommandSourceType commandType) { }
        public void OrderAllPassengersToHackInternet(CommandSourceType commandType) { }
    }

    /// <summary>
    /// Records how many times this nugget's Execute ran. FXNugget.Execute is `internal virtual`
    /// and this project has InternalsVisibleTo on OpenSage.Game, so a plain subclass observes
    /// FXList playback directly - no rendering/audio subsystem involved, so it's crash-safe in
    /// the headless host regardless of what FX kind production code would normally play.
    /// </summary>
    private sealed class SpyFXNugget : FXNugget
    {
        public int ExecuteCount;
        internal override void Execute(FXListExecutionContext context) => ExecuteCount++;
    }

    [Fact]
    public void UpgradeGate_NotResearched_OccupantsSurvive()
    {
        var game = NewGame();
        var missile = game.SpawnObject("BunkerBusterMissileNoOccupantWeapon", game.CivilianPlayer, Vector3.Zero);
        var bunker = game.SpawnObject("Bunker", game.PlayerManager.NeutralPlayer, new Vector3(100, 0, 0));
        var occupant = game.SpawnObject("Occupant", game.PlayerManager.NeutralPlayer, new Vector3(100, 0, 0));
        InjectContain(bunker, new FakeGarrisonContain(occupant));

        // UpgradeRequired = Upgrade_BunkerBust is configured but never granted.
        missile.AIUpdate.SetCurrentVictim(bunker.Id);
        game.Step();
        game.Step();

        missile.Kill();

        // GPL bustTheBunker returns before touching the container (or anything else) when the
        // required upgrade is missing.
        Assert.False(occupant.IsEffectivelyDead);
    }

    [Fact]
    public void UpgradeGate_Researched_FallbackKillsAllOccupants()
    {
        var game = NewGame();
        GrantUpgrade(game);
        var missile = game.SpawnObject("BunkerBusterMissileNoOccupantWeapon", game.CivilianPlayer, Vector3.Zero);
        var bunker = game.SpawnObject("Bunker", game.PlayerManager.NeutralPlayer, new Vector3(100, 0, 0));
        var occupant = game.SpawnObject("Occupant", game.PlayerManager.NeutralPlayer, new Vector3(100, 0, 0));
        InjectContain(bunker, new FakeGarrisonContain(occupant));

        missile.AIUpdate.SetCurrentVictim(bunker.Id);
        game.Step();
        game.Step();

        missile.Kill();

        // No OccupantDamageWeaponTemplate configured on this object: GPL falls back to
        // killAllContained() - an unconditional kill, independent of the occupant's health.
        Assert.True(occupant.IsEffectivelyDead);
    }

    [Fact]
    public void OccupantDamage_WeaponConfigured_AppliesConfiguredDamageInsteadOfInstantKill()
    {
        var game = NewGame();
        GrantUpgrade(game);
        var missile = game.SpawnObject("BunkerBusterMissile", game.CivilianPlayer, Vector3.Zero);
        var bunker = game.SpawnObject("Bunker", game.PlayerManager.NeutralPlayer, new Vector3(100, 0, 0));
        var occupant = game.SpawnObject("Occupant", game.PlayerManager.NeutralPlayer, new Vector3(100, 0, 0));
        InjectContain(bunker, new FakeGarrisonContain(occupant));

        missile.AIUpdate.SetCurrentVictim(bunker.Id);
        game.Step();
        game.Step();

        missile.Kill();

        // GPL hardcodes the applied amount to 100 regardless of the weapon's own Damage value;
        // the occupant's MaxHealth (500) survives that, unlike the unconditional-kill fallback.
        Assert.False(occupant.IsEffectivelyDead);
        Assert.Equal(400f, occupant.BodyModule.Health, 1);
    }

    [Fact]
    public void VictimTracking_CachesTargetOnce_EffectsAndShockwaveApplyToCachedTarget()
    {
        var game = NewGame();
        GrantUpgrade(game);
        var missile = game.SpawnObject("BunkerBusterMissile", game.CivilianPlayer, Vector3.Zero);
        var bunker = game.SpawnObject("Bunker", game.PlayerManager.NeutralPlayer, new Vector3(100, 0, 0));
        var decoyBunker = game.SpawnObject("Bunker", game.PlayerManager.NeutralPlayer, new Vector3(-100, 0, 0));
        var occupant = game.SpawnObject("Occupant", game.PlayerManager.NeutralPlayer, new Vector3(100, 0, 0));
        InjectContain(bunker, new FakeGarrisonContain(occupant));

        missile.AIUpdate.SetCurrentVictim(bunker.Id);
        game.Step();
        game.Step(); // BunkerBusterBehavior.Update caches _victimId = bunker.Id here (GPL: only while INVALID_ID).

        // The AI "retargets" afterwards; GPL's `if (m_victimID == INVALID_ID)` means the first
        // cached victim sticks for the rest of this object's life.
        missile.AIUpdate.SetCurrentVictim(decoyBunker.Id);
        game.Step();

        missile.Kill();

        // objectForFX becomes the cached victim (bunker), not the decoy and not the missile
        // itself, so the shockwave temp weapon's ParentGameObject - and therefore its Firing
        // flag - lands on the originally-cached bunker.
        Assert.True(Fired(bunker));
        Assert.False(Fired(decoyBunker));
        Assert.False(Fired(missile));
    }

    [Fact]
    public void CrashThroughFX_PlaysOnlyDuringSelfDestructAtConfiguredFrequency()
    {
        var game = NewGame();
        var missile = game.SpawnObject("BunkerBusterMissile", game.CivilianPlayer, Vector3.Zero);

        var spy = new SpyFXNugget();
        game.AssetStore.FXLists.GetByName("FX_Crash").Nuggets.Add(spy);

        // Without OBJECT_STATUS_MISSILE_KILLING_SELF, the frequency gate alone must never play it.
        for (var i = 0; i < 20; i++)
        {
            game.Step();
        }
        Assert.Equal(0, spy.ExecuteCount);

        missile.SetObjectStatus(ObjectStatus.MissingKillingSelf, true);

        for (var i = 0; i < 20; i++)
        {
            game.Step();
        }

        // CrashThroughBunkerFXFrequency = 800ms -> ceil(800 * 5 / 1000) = 4 logic frames (BFME2's
        // 5Hz logic rate): frame % 4 == 1 recurs multiple times across 20 more frames.
        Assert.True(spy.ExecuteCount >= 2);
    }
}
