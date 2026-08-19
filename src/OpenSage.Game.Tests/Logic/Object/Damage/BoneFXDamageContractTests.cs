// BoneFXDamage R7 contract tests (template v1.1 §5): the create-time pairing requirement and
// the body-damage-state relay into the paired BoneFXUpdate, exercised on HeadlessSimGame with
// real parsed INI so the S1 Fix64 damage/armor/health chain and the ActiveBody →
// IDamageModule.OnBodyDamageStateChange dispatch are on the tested path. One test per behavioral
// branch, plus the version-only Xfer contract walk (shadow-copy CRC + mid-state save/load).
//
// Behavioral reference: generals-gpl GeneralsMD BoneFXDamage.cpp (semantics only). The claims
// under test: (a) BoneFXDamage requires a BoneFXUpdate sibling at creation (GPL onObjectCreated
// throws when it is absent); (b) each Pristine→Damaged→ReallyDamaged crossing (and the healing
// direction) is relayed into BoneFXUpdate.ChangeBodyDamageState; (c) individual sub-threshold
// hits do NOT relay (onDamage is empty); (d) the module carries no own sim state.

using System;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Damage;

public class BoneFXDamageContractTests
{
    // MaxHealth 100 with thresholds 50%/10%: 40 HP => Damaged, 5 HP => ReallyDamaged.
    private const string Definitions = @"
GameData
  UnitDamagedThreshold = 0.5
  UnitReallyDamagedThreshold = 0.1
End

Object BoneFXVictim
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
    InitialHealth = 100
  End
  Behavior = BoneFXUpdate ModuleTag_BoneFXUpdate
  End
  Behavior = BoneFXDamage ModuleTag_BoneFXDamage
  End
End

Object NoBoneFXUpdateVictim
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
    InitialHealth = 100
  End
  Behavior = BoneFXDamage ModuleTag_BoneFXDamage
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xB0FEu)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static GameObject Spawn(HeadlessSimGame game, string definition)
        => game.SpawnObject(definition, game.CivilianPlayer, new Vector3(0, 0, 0));

    private static ActiveBody BodyOf(GameObject gameObject)
        => Assert.IsType<ActiveBody>(gameObject.BodyModule);

    private static BoneFXUpdate BoneFXOf(GameObject gameObject)
        => Assert.IsType<BoneFXUpdate>(gameObject.FindBehavior<BoneFXUpdate>());

    private static BoneFXDamage BoneFXDamageOf(GameObject gameObject)
        => Assert.IsType<BoneFXDamage>(gameObject.FindBehavior<BoneFXDamage>());

    private static Fix64 Fix(int value) => new(value);

    private static CombatDamageInput Damage(int amount, GameObject source = null)
        => new()
        {
            SourceId = source?.Id ?? ObjectId.Invalid,
            DamageType = DamageType.Unresistable,
            Amount = Fix(amount),
        };

    private static CombatDamageInput Heal(int amount)
        => new()
        {
            SourceId = ObjectId.Invalid,
            DamageType = DamageType.Healing,
            Amount = Fix(amount),
        };

    // ================================================================
    // Create-time pairing requirement (GPL onObjectCreated)
    // ================================================================

    [Fact]
    public void Creation_WithBoneFXUpdate_Succeeds()
    {
        var game = NewGame();
        var victim = Spawn(game, "BoneFXVictim");

        // Both halves of the pair resolved; the damage module is a live DamageModule.
        Assert.NotNull(BoneFXDamageOf(victim));
        Assert.NotNull(BoneFXOf(victim));
        Assert.Equal(BodyDamageType.Pristine, BoneFXOf(victim).CurrentBodyState);
    }

    [Fact]
    public void Creation_WithoutBoneFXUpdate_Throws()
    {
        var game = NewGame();

        // GPL: BoneFXDamage::onObjectCreated throws INI_INVALID_DATA when no BoneFXUpdate exists.
        var ex = Assert.Throws<InvalidOperationException>(() => Spawn(game, "NoBoneFXUpdateVictim"));
        Assert.Contains("BoneFXUpdate", ex.Message);
    }

    // ================================================================
    // The state-change relay (GPL onBodyDamageStateChange)
    // ================================================================

    [Fact]
    public void DamageStateCrossing_RelaysNewStateToBoneFXUpdate()
    {
        var game = NewGame();
        var victim = Spawn(game, "BoneFXVictim");

        // 60 dmg => 40 HP => 40% < 50% => Damaged. The body dispatches
        // OnBodyDamageStateChange to the IDamageModule (BoneFXDamage), which relays into the
        // sibling BoneFXUpdate.
        victim.AttemptCombatDamage(Damage(60));

        Assert.Equal(BodyDamageType.Damaged, BodyOf(victim).DamageState);
        Assert.Equal(BodyDamageType.Damaged, BoneFXOf(victim).CurrentBodyState);
    }

    [Fact]
    public void SuccessiveCrossings_EachRelayTheLatestState()
    {
        var game = NewGame();
        var victim = Spawn(game, "BoneFXVictim");

        victim.AttemptCombatDamage(Damage(60));   // 40 HP => Damaged
        Assert.Equal(BodyDamageType.Damaged, BoneFXOf(victim).CurrentBodyState);

        victim.AttemptCombatDamage(Damage(35));   // 5 HP => ReallyDamaged
        Assert.Equal(BodyDamageType.ReallyDamaged, BodyOf(victim).DamageState);
        Assert.Equal(BodyDamageType.ReallyDamaged, BoneFXOf(victim).CurrentBodyState);
        Assert.False(victim.IsEffectivelyDead);
    }

    [Fact]
    public void HealingCrossing_RelaysTheHealedStateBack()
    {
        var game = NewGame();
        var victim = Spawn(game, "BoneFXVictim");

        victim.AttemptCombatDamage(Damage(95));   // 5 HP => ReallyDamaged
        Assert.Equal(BodyDamageType.ReallyDamaged, BoneFXOf(victim).CurrentBodyState);

        // Heal back above 50% => Pristine again; the healing-direction dispatch relays too.
        victim.AttemptCombatDamage(Heal(90));      // 95 HP => Pristine
        Assert.Equal(BodyDamageType.Pristine, BodyOf(victim).DamageState);
        Assert.Equal(BodyDamageType.Pristine, BoneFXOf(victim).CurrentBodyState);
    }

    [Fact]
    public void SubThresholdHit_DoesNotChangeState_NoSpuriousRelay()
    {
        var game = NewGame();
        var victim = Spawn(game, "BoneFXVictim");

        // 10 dmg => 90 HP => still Pristine (> 50%): no state transition, so no relay fires and
        // the sibling stays Pristine. (GPL onDamage() is empty; only state crossings relay.)
        victim.AttemptCombatDamage(Damage(10));

        Assert.Equal(BodyDamageType.Pristine, BodyOf(victim).DamageState);
        Assert.Equal(BodyDamageType.Pristine, BoneFXOf(victim).CurrentBodyState);
    }

    // ================================================================
    // Xfer contract walk (version-only; no own sim state)
    // ================================================================

    [Fact]
    public void HasSimXfer_And_ShadowCopyCrcMatches_MidBehavior()
    {
        var game = NewGame();
        var live = Spawn(game, "BoneFXVictim");
        var shadow = Spawn(game, "BoneFXVictim");

        Assert.True(BoneFXDamageOf(live).HasSimXfer);

        // Drive the live body through a state crossing; the shadow stays pristine. BoneFXDamage
        // itself holds no state, so the walk (version-only) is CRC-identical regardless.
        live.AttemptCombatDamage(Damage(60));

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(
            BoneFXDamageOf(live), BoneFXDamageOf(shadow));
    }

    [Fact]
    public void SaveLoad_RoundTrips_MidBehavior()
    {
        var game = NewGame();
        var live = Spawn(game, "BoneFXVictim");
        live.AttemptCombatDamage(Damage(60));   // Damaged

        var state = PortedModuleTestKit.Save(BoneFXDamageOf(live));
        var restored = Spawn(game, "BoneFXVictim");
        PortedModuleTestKit.Load(BoneFXDamageOf(restored), state);

        // Version-only walk: byte-stable and CRC-equal after the round trip.
        Assert.Equal(
            PortedModuleTestKit.LiveCrc(BoneFXDamageOf(live)),
            PortedModuleTestKit.LiveCrc(BoneFXDamageOf(restored)));
    }
}
