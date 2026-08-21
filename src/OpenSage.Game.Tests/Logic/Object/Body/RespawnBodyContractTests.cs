// RespawnBody R8 contract tests (Body-batch), on HeadlessSimGame with real parsed INI so the
// audited parse path (ObjectFilter.Parse for PermanentlyKilledByFilter, the F-R7-2 InitialHealth
// default) and the S1 Fix64 kill-resolution chain are on the tested path.
//
// Behavioral reference: BFME2-only class, ABSENT from generals-gpl - binary-derived spec behavioral
// facts only, clean-room fresh code. The determinism-relevant fact under test is the ONE this
// Body owns: on the killing blow, the killer is tested against PermanentlyKilledByFilter and the
// permanence verdict is latched as sim state and folded into the Objects CRC channel (shadow-copy
// CRC + CRC-participation + mid-state save/load continuation). The broader respawn lifecycle is
// out of scope on this tip (finding F-RSB-1).
//
// R14 CONTRACT UPDATE (respawn seam, wave-2a adversarial review finding H1; owner-ratified as
// dr-0033). The R8 contract this file locked in is REOPENED here on purpose, and the tests
// below are the citation for why:
//
//   * The permanence verdict is now resolved from the killing blow through the public
//     ResolvePermanenceForDeath, which the seam calls from inside GameObject.OnDie. The old
//     "resolve after base.AttemptDamage returns" ordering was too late to be usable: ActiveBody
//     calls obj.OnDie from INSIDE that base call, so a claim predicate reading the latch saw
//     false for every death, permanent ones included. The post-base check survives as the
//     FALLBACK for a RespawnBody with no revive-lifecycle module, so the observable verdict for
//     every case this file already covered is UNCHANGED - see the four kill tests below, which
//     are byte-for-byte the R8 assertions.
//   * A second bool of sim state (_permanenceResolved) enforces "exactly once per death" now
//     that there are two entry points, and Revive() clears it so a SECOND death resolves on its
//     own killing blow. Xfer is therefore version 2 and folds both bools.
//   * Revive(healthPercent) is the specified exit from the dead state (review finding H4):
//     GameObject.IsEffectivelyDead is recomputed by ActiveBody from the Fix64 health ledger on
//     every health change, so restoring health through the body IS what clears it.

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Body;

public class RespawnBodyContractTests
{
    // Filter = NONE +STRUCTURE: a structure killer makes the death permanent; anything else
    // (infantry, no source) leaves the hero respawn-eligible. A separate hero omits the filter
    // entirely, and a third omits InitialHealth to exercise the F-R7-2 default carry.
    private const string Definitions = @"
GameData
  UnitDamagedThreshold = 0.5
  UnitReallyDamagedThreshold = 0.1
End

Armor BaseArmor
  Armor = DEFAULT 100%
End

Object RespawnHero
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  ArmorSet
    Conditions = None
    Armor = BaseArmor
  End
  Body = RespawnBody ModuleTag_Body
    MaxHealth = 100
    InitialHealth = 100
    PermanentlyKilledByFilter = NONE +STRUCTURE
  End
End

Object RespawnHeroNoFilter
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = RespawnBody ModuleTag_Body
    MaxHealth = 100
    InitialHealth = 100
  End
End

Object RespawnHeroDefaultHealth
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = RespawnBody ModuleTag_Body
    MaxHealth = 250
    PermanentlyKilledByFilter = NONE +STRUCTURE
  End
End

Object StructureKiller
  KindOf = STRUCTURE
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
End

Object InfantryKiller
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC0FFEEu)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static GameObject Spawn(HeadlessSimGame game, string definition = "RespawnHero")
        => game.SpawnObject(definition, game.CivilianPlayer, new Vector3(0, 0, 0));

    private static RespawnBody BodyOf(GameObject gameObject)
        => Assert.IsType<RespawnBody>(gameObject.BodyModule);

    private static Fix64 Fix(int value) => new(value);

    private static CombatDamageInput Damage(
        int amount, GameObject source = null, DamageType type = DamageType.Magic)
        => new()
        {
            SourceId = source?.Id ?? ObjectId.Invalid,
            DamageType = type,
            Amount = Fix(amount),
            Kill = false,
        };

    // ================================================================
    // Item 1 - ModuleData audit
    // ================================================================

    [Fact]
    public void PermanentlyKilledByFilter_IsParsed_IntoTheStructureInclude()
    {
        var game = NewGame();
        var data = Assert.IsType<RespawnBodyModuleData>(
            game.AssetStore.ObjectDefinitions.GetByName("RespawnHero")
                .Behaviors["ModuleTag_Body"].Data);

        Assert.NotNull(data.PermanentlyKilledByFilter);
        // A structure matches; a non-structure (infantry) does not.
        var structureKiller = Spawn(game, "StructureKiller");
        var infantryKiller = Spawn(game, "InfantryKiller");
        Assert.True(data.PermanentlyKilledByFilter.Matches(structureKiller));
        Assert.False(data.PermanentlyKilledByFilter.Matches(infantryKiller));
    }

    [Fact]
    public void InitialHealth_DefaultsToMaxHealth_WhenOmitted()
    {
        // F-R7-2 / F-HB-1 carry: the shadowing Parse must re-apply ApplyHealthDefaults, else the
        // body would spawn at 0 health. RespawnHeroDefaultHealth omits InitialHealth.
        var game = NewGame();
        var data = Assert.IsType<RespawnBodyModuleData>(
            game.AssetStore.ObjectDefinitions.GetByName("RespawnHeroDefaultHealth")
                .Behaviors["ModuleTag_Body"].Data);

        Assert.Equal(Fix(250), data.MaxHealth);
        Assert.Equal(Fix(250), data.InitialHealth);

        // And the spawned body actually starts at full (250), not 0.
        var hero = Spawn(game, "RespawnHeroDefaultHealth");
        Assert.Equal(Fix(250), BodyOf(hero).DamageCore.CurrentHealth);
    }

    // ================================================================
    // The permanence decision (the one behavior this Body owns)
    // ================================================================

    [Fact]
    public void KilledByStructure_MarksPermanentlyKilled()
    {
        var game = NewGame();
        var hero = Spawn(game);
        var killer = Spawn(game, "StructureKiller");

        hero.AttemptCombatDamage(Damage(9999, source: killer));

        Assert.True(hero.IsEffectivelyDead);
        Assert.True(BodyOf(hero).IsPermanentlyKilled);
    }

    [Fact]
    public void KilledByNonStructure_IsNotPermanent()
    {
        var game = NewGame();
        var hero = Spawn(game);
        var killer = Spawn(game, "InfantryKiller");

        hero.AttemptCombatDamage(Damage(9999, source: killer));

        Assert.True(hero.IsEffectivelyDead);
        Assert.False(BodyOf(hero).IsPermanentlyKilled);
    }

    [Fact]
    public void KilledBySourcelessDamage_IsNotPermanent()
    {
        var game = NewGame();
        var hero = Spawn(game);

        hero.AttemptCombatDamage(Damage(9999)); // SourceId = Invalid

        Assert.True(hero.IsEffectivelyDead);
        Assert.False(BodyOf(hero).IsPermanentlyKilled);
    }

    [Fact]
    public void NoFilter_KillIsNeverPermanent()
    {
        var game = NewGame();
        var hero = Spawn(game, "RespawnHeroNoFilter");
        var killer = Spawn(game, "StructureKiller");

        hero.AttemptCombatDamage(Damage(9999, source: killer));

        Assert.True(hero.IsEffectivelyDead);
        Assert.False(BodyOf(hero).IsPermanentlyKilled);
    }

    [Fact]
    public void NonLethalHit_LeavesVerdictUntouched()
    {
        var game = NewGame();
        var hero = Spawn(game);
        var killer = Spawn(game, "StructureKiller");

        hero.AttemptCombatDamage(Damage(30, source: killer));

        Assert.False(hero.IsEffectivelyDead);
        Assert.False(BodyOf(hero).IsPermanentlyKilled);
        Assert.Equal(Fix(70), BodyOf(hero).DamageCore.CurrentHealth);
    }

    [Fact]
    public void OnlyTheKillingBlowResolves_LatchIsNotOverwritten()
    {
        // A structure kill latches permanent; a further (already-dead) hit from an infantry
        // must not flip the verdict back to false.
        var game = NewGame();
        var hero = Spawn(game);
        var structureKiller = Spawn(game, "StructureKiller");
        var infantryKiller = Spawn(game, "InfantryKiller");

        hero.AttemptCombatDamage(Damage(9999, source: structureKiller));
        Assert.True(BodyOf(hero).IsPermanentlyKilled);

        hero.AttemptCombatDamage(Damage(9999, source: infantryKiller));
        Assert.True(BodyOf(hero).IsPermanentlyKilled);
    }

    // ================================================================
    // R14 (H1/H4) - the reopened contract
    // ================================================================

    [Fact]
    public void ResolvePermanenceForDeath_ResolvesFromTheDamage_BeforeAnyLatchExists()
    {
        // The seam's own call shape: ask the body about a killing blow it has not yet taken.
        // This is what ClaimDeath does from inside OnDie, where no latch exists yet.
        var game = NewGame();
        var hero = Spawn(game);
        var structureKiller = Spawn(game, "StructureKiller");

        Assert.False(BodyOf(hero).IsPermanenceResolved);

        var permanent = BodyOf(hero).ResolvePermanenceForDeath(
            new DamageInfoInput(structureKiller) { DamageType = DamageType.Magic, Amount = 9999 });

        Assert.True(permanent);
        Assert.True(BodyOf(hero).IsPermanentlyKilled);
        Assert.True(BodyOf(hero).IsPermanenceResolved);
    }

    [Fact]
    public void ResolvePermanenceForDeath_IsIdempotentWithinOneDeath()
    {
        // Both entry points can fire for the same death; the second must not re-test the
        // filter, or a differently-sourced follow-up hit could flip a settled verdict.
        var game = NewGame();
        var hero = Spawn(game);
        var structureKiller = Spawn(game, "StructureKiller");
        var infantryKiller = Spawn(game, "InfantryKiller");

        Assert.True(BodyOf(hero).ResolvePermanenceForDeath(
            new DamageInfoInput(structureKiller) { DamageType = DamageType.Magic, Amount = 9999 }));

        Assert.True(BodyOf(hero).ResolvePermanenceForDeath(
            new DamageInfoInput(infantryKiller) { DamageType = DamageType.Magic, Amount = 9999 }));
        Assert.True(BodyOf(hero).IsPermanentlyKilled);
    }

    [Fact]
    public void Revive_ClearsIsEffectivelyDead_ThroughTheHealthLedger()
    {
        var game = NewGame();
        var hero = Spawn(game);
        hero.AttemptCombatDamage(Damage(9999, source: Spawn(game, "InfantryKiller")));

        Assert.True(hero.IsEffectivelyDead);
        Assert.Equal(Fix(0), BodyOf(hero).DamageCore.CurrentHealth);

        BodyOf(hero).Revive(100);

        // The health restore is what cleared the flag - not a bit set behind the ledger's back.
        Assert.Equal(Fix(100), BodyOf(hero).DamageCore.CurrentHealth);
        Assert.False(hero.IsEffectivelyDead);
    }

    [Fact]
    public void Revive_ReArmsThePermanenceResolver_SoTheSecondDeathResolves()
    {
        var game = NewGame();
        var hero = Spawn(game);
        hero.AttemptCombatDamage(Damage(9999, source: Spawn(game, "InfantryKiller")));
        Assert.True(BodyOf(hero).IsPermanenceResolved);
        Assert.False(BodyOf(hero).IsPermanentlyKilled);

        BodyOf(hero).Revive(100);
        Assert.False(BodyOf(hero).IsPermanenceResolved);

        hero.AttemptCombatDamage(Damage(9999, source: Spawn(game, "StructureKiller")));
        Assert.True(BodyOf(hero).IsPermanentlyKilled);
    }

    [Fact]
    public void Revive_RestoresTheDeclaredPercentOfInitialHealth()
    {
        // The percent is applied by BodyDamageCore's exact Int128 mul-div, so it is bit-stable
        // rather than a float ratio: 40% of 250 is exactly 100.
        var game = NewGame();
        var hero = Spawn(game, "RespawnHeroDefaultHealth");
        hero.AttemptCombatDamage(Damage(9999, source: Spawn(game, "InfantryKiller")));

        BodyOf(hero).Revive(40);

        Assert.Equal(Fix(100), BodyOf(hero).DamageCore.CurrentHealth);
        Assert.False(hero.IsEffectivelyDead);
    }

    // ================================================================
    // Item 3 - Xfer: the _permanentlyKilled latch folds into the Objects CRC channel
    // ================================================================

    [Fact]
    public void PermanentlyKilled_ParticipatesInCrc()
    {
        var game = NewGame();
        var alive = Spawn(game);
        var permanentlyDead = Spawn(game);
        permanentlyDead.AttemptCombatDamage(Damage(9999, source: Spawn(game, "StructureKiller")));

        // A subclass that forgot to walk the latch would fold identically here.
        Assert.NotEqual(
            PortedModuleTestKit.LiveCrc(BodyOf(alive)),
            PortedModuleTestKit.LiveCrc(BodyOf(permanentlyDead)));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var live = Spawn(game);
        live.AttemptCombatDamage(Damage(9999, source: Spawn(game, "StructureKiller"))); // permanently killed
        var shadow = Spawn(game);
        shadow.AttemptCombatDamage(Damage(25)); // differently-stated, still alive

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(BodyOf(live), BodyOf(shadow));
    }

    // ================================================================
    // Item 4 - mid-state save/load continuation
    // ================================================================

    [Fact]
    public void SaveLoad_PermanentKillVerdict_Continues()
    {
        var game = NewGame();
        var live = Spawn(game);
        live.AttemptCombatDamage(Damage(9999, source: Spawn(game, "StructureKiller")));
        Assert.True(BodyOf(live).IsPermanentlyKilled);

        var state = PortedModuleTestKit.Save(BodyOf(live));
        var restoredHost = Spawn(game); // fresh: alive, verdict false
        Assert.False(BodyOf(restoredHost).IsPermanentlyKilled);

        PortedModuleTestKit.Load(BodyOf(restoredHost), state);

        // The verdict must have restored through the contract Xfer.
        Assert.True(BodyOf(restoredHost).IsPermanentlyKilled);
        Assert.Equal(
            PortedModuleTestKit.LiveCrc(BodyOf(live)),
            PortedModuleTestKit.LiveCrc(BodyOf(restoredHost)));
    }
}
