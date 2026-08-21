// EvacuateDamage R13 contract tests (spec packet: bfme2-workbench/research/modules-r13/specs/
// EvacuateDamageModuleData.md), mirroring BoneFXDamageContractTests's shape: HeadlessSimGame +
// LoadIniText, one test per behavioral branch, plus the version-only Xfer contract walk (shadow
// copy CRC + mid-state save/load).
//
// Behavioral reference: no GPL EvacuateDamage class exists (BFME-only addition); the action it
// gates is GPL's documented orderAllPassengersToExit primitive ("this is the game Evacuate" -
// ContainModule.h/OpenContain.h). The claims under test: (a) a matching
// WeaponThatCausesEvacuation gate empties the container via the already-landed
// OpenContainModule.Evacuate(); (b) a non-matching weapon, or damage with no weapon template at
// all, is a no-op; (c) an empty container is a safe no-op; (d) OpenContainModule.Evacuate() only
// *queues* passengers - the sleepy-update caveat means a freshly spawned container's own Update()
// (which drains the queue) needs a second HeadlessSimGame.Step() beyond the damage frame; (e)
// repeated matching hits after evacuation are safe; (f) OnHealing/OnBodyDamageStateChange are
// left as the inherited no-ops; (g) the module carries no own sim state.

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Damage;

public class EvacuateDamageContractTests
{
    private const string Definitions = @"
GameData
  UnitDamagedThreshold = 0.5
  UnitReallyDamagedThreshold = 0.1
End

Weapon MordorCatapultHumanHeads
  ClipSize = 1
  DelayBetweenShots = 1000
  ClipReloadTime = 1000
End

Weapon TrollBoulder
  ClipSize = 1
  DelayBetweenShots = 1000
  ClipReloadTime = 1000
End

Object EvacInfantry
  KindOf = INFANTRY SELECTABLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
    InitialHealth = 50
  End
End

Object EvacTower
  KindOf = STRUCTURE IMMOBILE
  Geometry = BOX
  GeometryMajorRadius = 20
  GeometryMinorRadius = 20
  GeometryHeight = 20
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
    InitialHealth = 500
  End
  Behavior = GarrisonContain ModuleTag_Contain
    ContainMax = 10
    AllowInsideKindOf = INFANTRY
  End
  Behavior = EvacuateDamage ModuleTag_Evac
    WeaponThatCausesEvacuation = MordorCatapultHumanHeads
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xE7A6u)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static GameObject SpawnTower(HeadlessSimGame game)
        => game.SpawnObject("EvacTower", game.CivilianPlayer, new Vector3(0, 0, 0));

    private static GameObject SpawnInfantry(HeadlessSimGame game, float x)
        => game.SpawnObject("EvacInfantry", game.CivilianPlayer, new Vector3(x, 0, 0));

    private static OpenContainModule ContainOf(GameObject tower)
        => Assert.IsType<GarrisonContain>(tower.FindBehavior<OpenContainModule>());

    private static EvacuateDamage EvacuateDamageOf(GameObject tower)
        => Assert.IsType<EvacuateDamage>(tower.FindBehavior<EvacuateDamage>());

    private static Fix64 Fix(int value) => new(value);

    private static CombatDamageInput Damage(int amount, WeaponTemplate weaponTemplate = null)
        => new()
        {
            SourceId = ObjectId.Invalid,
            DamageType = DamageType.Unresistable,
            Amount = Fix(amount),
            SourceWeaponTemplate = weaponTemplate,
        };

    // ================================================================
    // Case 1: matching weapon evacuates all passengers (with the sleepy-drain second Step())
    // ================================================================

    [Fact]
    public void MatchingWeapon_EvacuatesAllPassengers_AfterDrainStep()
    {
        var game = NewGame();
        var tower = SpawnTower(game);
        var contain = ContainOf(tower);
        var a = SpawnInfantry(game, 10);
        var b = SpawnInfantry(game, 20);
        Assert.True(contain.CanAddUnit(a));
        contain.Add(a);
        contain.Add(b);
        Assert.Equal(2, contain.OccupiedSlots);

        var weapon = game.AssetStore.WeaponTemplates.GetByName("MordorCatapultHumanHeads");
        tower.AttemptCombatDamage(Damage(10, weapon));

        game.Step(); // damage frame / sleepy wake
        game.Step(); // container's own Update() drains the evac queue

        Assert.Equal(0, contain.OccupiedSlots);
        Assert.Empty(contain.ContainedObjectIds);
        Assert.Equal(ObjectId.Invalid, a.ContainerId);
        Assert.Equal(ObjectId.Invalid, b.ContainerId);
    }

    // ================================================================
    // Case 2: non-matching weapon does nothing
    // ================================================================

    [Fact]
    public void NonMatchingWeapon_DoesNotEvacuate()
    {
        var game = NewGame();
        var tower = SpawnTower(game);
        var contain = ContainOf(tower);
        contain.Add(SpawnInfantry(game, 10));
        contain.Add(SpawnInfantry(game, 20));
        Assert.Equal(2, contain.OccupiedSlots);

        var weapon = game.AssetStore.WeaponTemplates.GetByName("TrollBoulder");
        tower.AttemptCombatDamage(Damage(10, weapon));

        game.Step();
        game.Step();

        Assert.Equal(2, contain.OccupiedSlots);
    }

    // ================================================================
    // Case 3: no weapon template on the damage (environmental) does nothing
    // ================================================================

    [Fact]
    public void NoWeaponTemplate_DoesNotEvacuate_AndDoesNotThrow()
    {
        var game = NewGame();
        var tower = SpawnTower(game);
        var contain = ContainOf(tower);
        contain.Add(SpawnInfantry(game, 10));
        Assert.Equal(1, contain.OccupiedSlots);

        var exception = Record.Exception(() =>
        {
            tower.AttemptCombatDamage(Damage(10)); // SourceWeaponTemplate == null
            game.Step();
            game.Step();
        });

        Assert.Null(exception);
        Assert.Equal(1, contain.OccupiedSlots);
    }

    // ================================================================
    // Case 4: empty container, matching weapon - no-op, no throw
    // ================================================================

    [Fact]
    public void EmptyContainer_MatchingWeapon_NoOpNoThrow()
    {
        var game = NewGame();
        var tower = SpawnTower(game);
        var contain = ContainOf(tower);
        Assert.Equal(0, contain.OccupiedSlots);

        var weapon = game.AssetStore.WeaponTemplates.GetByName("MordorCatapultHumanHeads");

        var exception = Record.Exception(() =>
        {
            tower.AttemptCombatDamage(Damage(10, weapon));
            game.Step();
            game.Step();
        });

        Assert.Null(exception);
        Assert.Equal(0, contain.OccupiedSlots);
    }

    // ================================================================
    // Case 5: sleepy-update timing - queued but not yet drained after exactly one Step()
    // ================================================================

    [Fact]
    public void MatchingWeapon_AfterOneStep_StillQueued_NotYetDrained()
    {
        var game = NewGame();
        var tower = SpawnTower(game);
        var contain = ContainOf(tower);
        contain.Add(SpawnInfantry(game, 10));
        contain.Add(SpawnInfantry(game, 20));

        var weapon = game.AssetStore.WeaponTemplates.GetByName("MordorCatapultHumanHeads");
        tower.AttemptCombatDamage(Damage(10, weapon));

        game.Step();
        // Proves the queue-vs-drain distinction: the evacuate request has fired, but the
        // container's own Update() has not yet run to drain _evacQueue.
        Assert.Equal(2, contain.OccupiedSlots);

        game.Step();
        Assert.Equal(0, contain.OccupiedSlots);
    }

    // ================================================================
    // Case 6: repeated matching hits after evacuation are safe
    // ================================================================

    [Fact]
    public void RepeatedMatchingHits_AfterEvacuation_AreSafeNoOps()
    {
        var game = NewGame();
        var tower = SpawnTower(game);
        var contain = ContainOf(tower);
        contain.Add(SpawnInfantry(game, 10));

        var weapon = game.AssetStore.WeaponTemplates.GetByName("MordorCatapultHumanHeads");
        tower.AttemptCombatDamage(Damage(10, weapon));
        game.Step();
        game.Step();
        Assert.Equal(0, contain.OccupiedSlots);

        var exception = Record.Exception(() =>
        {
            tower.AttemptCombatDamage(Damage(10, weapon));
            game.Step();
            game.Step();
        });

        Assert.Null(exception);
        Assert.Equal(0, contain.OccupiedSlots);
    }

    // ================================================================
    // Case 8: OnHealing / OnBodyDamageStateChange are no-ops
    // ================================================================

    [Fact]
    public void HealingAndBodyStateCrossing_AreNoOps()
    {
        var game = NewGame();
        var tower = SpawnTower(game);
        var contain = ContainOf(tower);
        contain.Add(SpawnInfantry(game, 10));
        contain.Add(SpawnInfantry(game, 20));

        // Cross a body-damage-state threshold with a non-matching weapon: 300 dmg on 500 max =>
        // 40% < 50% => Damaged. OnBodyDamageStateChange fires on the module, but it is left as
        // the inherited no-op, so nothing evacuates.
        var weapon = game.AssetStore.WeaponTemplates.GetByName("TrollBoulder");
        tower.AttemptCombatDamage(Damage(300, weapon));
        game.Step();
        game.Step();
        Assert.Equal(2, contain.OccupiedSlots);

        // Healing likewise fires OnHealing, left as the inherited no-op.
        tower.AttemptHealing(Fix(50), null);
        game.Step();
        game.Step();
        Assert.Equal(2, contain.OccupiedSlots);
    }

    // ================================================================
    // Case 7: Xfer/CRC contract walk (version-only; no own sim state)
    // ================================================================

    [Fact]
    public void HasSimXfer_And_ShadowCopyCrcMatches_MidBehavior()
    {
        var game = NewGame();
        var live = SpawnTower(game);
        var shadow = SpawnTower(game);

        Assert.True(EvacuateDamageOf(live).HasSimXfer);

        var weapon = game.AssetStore.WeaponTemplates.GetByName("MordorCatapultHumanHeads");
        ContainOf(live).Add(SpawnInfantry(game, 10));
        live.AttemptCombatDamage(Damage(10, weapon));
        game.Step();
        game.Step();

        // EvacuateDamage itself holds no state - the evacuated/not-evacuated distinction lives
        // entirely in the sibling OpenContainModule's own xfer, not this module's - so the
        // walk (version-only) is CRC-identical regardless of the live/shadow evacuation state.
        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(
            EvacuateDamageOf(live), EvacuateDamageOf(shadow));
    }

    [Fact]
    public void SaveLoad_RoundTrips_MidBehavior()
    {
        var game = NewGame();
        var live = SpawnTower(game);
        var weapon = game.AssetStore.WeaponTemplates.GetByName("MordorCatapultHumanHeads");
        ContainOf(live).Add(SpawnInfantry(game, 10));
        live.AttemptCombatDamage(Damage(10, weapon));
        game.Step();
        game.Step();

        var state = PortedModuleTestKit.Save(EvacuateDamageOf(live));
        var restored = SpawnTower(game);
        PortedModuleTestKit.Load(EvacuateDamageOf(restored), state);

        // Version-only walk: byte-stable and CRC-equal after the round trip.
        Assert.Equal(
            PortedModuleTestKit.LiveCrc(EvacuateDamageOf(live)),
            PortedModuleTestKit.LiveCrc(EvacuateDamageOf(restored)));
    }
}
