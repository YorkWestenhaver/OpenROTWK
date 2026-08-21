// ReflectDamage R13 contract tests (modules-r13/specs/ReflectDamageModuleData.md §3): one test
// per behavioral branch (type gate, minimum-amount gate incl. the >= boundary, null-mask "match
// all" convention, zero-percent early-out, self/invalid/destroyed source guards, the
// non-reentrancy guard, the unused-hooks no-op), plus the version-only Xfer contract walk
// (shadow-copy CRC + mid-state save/load), on HeadlessSimGame with real parsed INI so the S1
// Fix64 damage/armor/health chain and the ActiveBody -> IDamageModule.OnDamage dispatch are on
// the tested path (not mocked). No corpus occurrence of `Behavior = ReflectDamage` exists in the
// AotR data tree (spec §0 "Corpus check"), so the object definitions below are synthetic, mirrored
// from the spec's own §3 sketch.
//
// Behavioral reference: no GPL sibling exists for this BFME/BFME2-only mechanic (spec §0); the
// rule under test is the data-derivation closed-form spelled out in spec §1, resolved against
// the landed TransitionDamageFX (null-mask convention) and PorcupineFormationBody (reentrancy
// guard, self/invalid-source predicate, target-destroyed guard) precedents.

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Damage;

public class ReflectDamageContractTests
{
    private const string Definitions = @"
GameData
  UnitDamagedThreshold = 0.5
  UnitReallyDamagedThreshold = 0.1
End

Object Attacker
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
    InitialHealth = 100
  End
End

Object Reflector
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
    InitialHealth = 1000
  End
  Behavior = ReflectDamage ModuleTag_ReflectDamage
    DamageTypesToReflect = ARMOR_PIERCING SMALL_ARMS
    ReflectDamagePercentage = 50%
    MinimumDamageToReflect = 10
  End
End

Object ReflectorNullMask
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
    InitialHealth = 1000
  End
  Behavior = ReflectDamage ModuleTag_ReflectDamage
    ReflectDamagePercentage = 50%
    MinimumDamageToReflect = 10
  End
End

Object ReflectorZeroPercent
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
    InitialHealth = 1000
  End
  Behavior = ReflectDamage ModuleTag_ReflectDamage
    DamageTypesToReflect = ARMOR_PIERCING SMALL_ARMS
    ReflectDamagePercentage = 0%
    MinimumDamageToReflect = 10
  End
End

Object MutualReflector
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
    InitialHealth = 1000
  End
  Behavior = ReflectDamage ModuleTag_ReflectDamage
    DamageTypesToReflect = ARMOR_PIERCING REFLECTED
    ReflectDamagePercentage = 50%
    MinimumDamageToReflect = 10
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xEF1EC7u)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static GameObject Spawn(HeadlessSimGame game, string definition)
        => game.SpawnObject(definition, game.CivilianPlayer, new Vector3(0, 0, 0));

    private static ReflectDamage ReflectDamageOf(GameObject gameObject)
        => Assert.IsType<ReflectDamage>(gameObject.FindBehavior<ReflectDamage>());

    private static Fix64 Fix(int value) => new(value);

    private static CombatDamageInput Hit(GameObject source, int amount, DamageType type)
        => new()
        {
            SourceId = source?.Id ?? ObjectId.Invalid,
            DamageType = type,
            Amount = Fix(amount),
        };

    // ================================================================
    // Type gate + minimum-amount gate + the reflected percentage
    // ================================================================

    [Fact]
    public void MatchingType_AboveMinimum_ReflectsPercentOfDealtDamage()
    {
        var game = NewGame();
        var attacker = Spawn(game, "Attacker");
        var reflector = Spawn(game, "Reflector");

        reflector.AttemptCombatDamage(Hit(attacker, 40, DamageType.ArmorPiercing));

        Assert.Equal(960f, reflector.BodyModule.Health);
        Assert.Equal(80f, attacker.BodyModule.Health);
        Assert.Equal(DamageType.Reflected, attacker.BodyModule.LastDamageInfo.Value.Request.DamageType);
    }

    [Fact]
    public void NonMatchingType_DoesNotReflect()
    {
        var game = NewGame();
        var attacker = Spawn(game, "Attacker");
        var reflector = Spawn(game, "Reflector");

        reflector.AttemptCombatDamage(Hit(attacker, 40, DamageType.Flame));

        Assert.Equal(960f, reflector.BodyModule.Health);
        Assert.Equal(100f, attacker.BodyModule.Health);
    }

    [Fact]
    public void BelowMinimumDamage_DoesNotReflect()
    {
        var game = NewGame();
        var attacker = Spawn(game, "Attacker");
        var reflector = Spawn(game, "Reflector");

        reflector.AttemptCombatDamage(Hit(attacker, 5, DamageType.ArmorPiercing));

        Assert.Equal(995f, reflector.BodyModule.Health);
        Assert.Equal(100f, attacker.BodyModule.Health);
    }

    [Fact]
    public void ExactlyAtMinimumDamage_Reflects()
    {
        var game = NewGame();
        var attacker = Spawn(game, "Attacker");
        var reflector = Spawn(game, "Reflector");

        reflector.AttemptCombatDamage(Hit(attacker, 10, DamageType.ArmorPiercing));

        // Proves the >= boundary, not >.
        Assert.Equal(95f, attacker.BodyModule.Health);
    }

    [Fact]
    public void NullMask_ReflectsEveryType()
    {
        var game = NewGame();
        var attacker = Spawn(game, "Attacker");
        var reflector = Spawn(game, "ReflectorNullMask");

        reflector.AttemptCombatDamage(Hit(attacker, 40, DamageType.Flame));

        Assert.Equal(80f, attacker.BodyModule.Health);
    }

    [Fact]
    public void ZeroPercent_NeverReflects()
    {
        var game = NewGame();
        var attacker = Spawn(game, "Attacker");
        var reflector = Spawn(game, "ReflectorZeroPercent");

        reflector.AttemptCombatDamage(Hit(attacker, 40, DamageType.ArmorPiercing));

        Assert.Equal(960f, reflector.BodyModule.Health);
        // The zero-guard is an early return, not a zero-amount delivery: the attacker's own
        // damage history is untouched, proving DealDirectDamage was never invoked on it at all.
        // (ActiveBody.LastDamageInfo is backed by a non-nullable struct field, so it is never
        // null - "untouched" shows up as the default, source-less value it was constructed with.)
        Assert.Equal(100f, attacker.BodyModule.Health);
        Assert.False(attacker.BodyModule.LastDamageInfo!.Value.Request.SourceID.IsValid);
    }

    // ================================================================
    // Source resolution guards (self / invalid / destroyed)
    // ================================================================

    [Fact]
    public void InvalidSource_DoesNotReflect_NoException()
    {
        var game = NewGame();
        var reflector = Spawn(game, "Reflector");

        reflector.AttemptCombatDamage(Hit(null, 40, DamageType.ArmorPiercing));

        Assert.Equal(960f, reflector.BodyModule.Health);
    }

    [Fact]
    public void SelfSourcedDamage_DoesNotReflect()
    {
        var game = NewGame();
        var reflector = Spawn(game, "Reflector");

        reflector.AttemptCombatDamage(Hit(reflector, 40, DamageType.ArmorPiercing));

        // Loses exactly the original 40, not 40 plus a self-reflected 20 on top.
        Assert.Equal(960f, reflector.BodyModule.Health);
    }

    [Fact]
    public void DestroyedSource_DoesNotReflect_NoException()
    {
        var game = NewGame();
        var attacker = Spawn(game, "Attacker");
        var reflector = Spawn(game, "Reflector");

        attacker.Kill();
        Assert.True(attacker.IsDestroyed);

        reflector.AttemptCombatDamage(Hit(attacker, 40, DamageType.ArmorPiercing));

        Assert.Equal(960f, reflector.BodyModule.Health);
    }

    // ================================================================
    // Non-reentrancy
    // ================================================================

    [Fact]
    public void ReflectedHitDoesNotReflectAgain_NoInfiniteRecursion()
    {
        var game = NewGame();
        var attacker = Spawn(game, "MutualReflector");
        var reflector = Spawn(game, "MutualReflector");

        // Both carry a ReflectDamage module whose mask includes REFLECTED itself (the
        // deliberately pathological configuration named in the spec's "Non-reentrancy" section).
        // reflector takes 40 from attacker -> reflects 20 back at attacker -> attacker's OWN
        // module (a different instance, its own _reflecting starts false) reflects 50% of that
        // 20 = 10 back at reflector -> reflector's module is STILL inside its original OnDamage
        // call (_reflecting == true), so this third hit lands as plain damage but does not
        // itself reflect again. Reflector total: 40 + 10 = 50. Attacker total: 20. Neither an
        // unbounded ping-pong nor a stack overflow.
        reflector.AttemptCombatDamage(Hit(attacker, 40, DamageType.ArmorPiercing));

        Assert.Equal(950f, reflector.BodyModule.Health);
        Assert.Equal(980f, attacker.BodyModule.Health);
    }

    // ================================================================
    // Unused hooks
    // ================================================================

    [Fact]
    public void OnHealing_And_OnBodyDamageStateChange_AreNoOps()
    {
        var game = NewGame();
        var attacker = Spawn(game, "Attacker");
        var reflector = Spawn(game, "Reflector");

        // Cross a body-damage-state threshold (Reflector: MaxHealth 1000, threshold 50% -> 501
        // damage worsens to Damaged) and heal it back - neither reaction is OnDamage, so neither
        // should trigger a reflected hit on the attacker.
        reflector.AttemptCombatDamage(Hit(attacker, 501, DamageType.ArmorPiercing));
        Assert.Equal(BodyDamageType.Damaged, reflector.BodyModule.DamageState);

        var attackerHealthAfterDamageTransition = attacker.BodyModule.Health;

        reflector.AttemptCombatDamage(new CombatDamageInput
        {
            SourceId = ObjectId.Invalid,
            DamageType = DamageType.Healing,
            Amount = Fix(100),
        });

        Assert.Equal(attackerHealthAfterDamageTransition, attacker.BodyModule.Health);
    }

    // ================================================================
    // Xfer contract walk (version-only; no own sim state)
    // ================================================================

    [Fact]
    public void HasSimXfer_And_ShadowCopyCrcMatches_MidBehavior()
    {
        var game = NewGame();
        var attacker = Spawn(game, "Attacker");
        var live = Spawn(game, "Reflector");
        var shadow = Spawn(game, "Reflector");

        Assert.True(ReflectDamageOf(live).HasSimXfer);

        live.AttemptCombatDamage(Hit(attacker, 40, DamageType.ArmorPiercing));

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(
            ReflectDamageOf(live), ReflectDamageOf(shadow));
    }

    [Fact]
    public void SaveLoad_RoundTrips_MidBehavior()
    {
        var game = NewGame();
        var attacker = Spawn(game, "Attacker");
        var live = Spawn(game, "Reflector");
        live.AttemptCombatDamage(Hit(attacker, 40, DamageType.ArmorPiercing));

        var state = PortedModuleTestKit.Save(ReflectDamageOf(live));
        var restored = Spawn(game, "Reflector");
        PortedModuleTestKit.Load(ReflectDamageOf(restored), state);

        Assert.Equal(
            PortedModuleTestKit.LiveCrc(ReflectDamageOf(live)),
            PortedModuleTestKit.LiveCrc(ReflectDamageOf(restored)));
    }
}
