// PorcupineFormationBody R8 contract tests (template v1.1 §5): the reflect-damage pike-wall
// body, exercised on HeadlessSimGame with real parsed INI so the quantizing parse path and
// the landed S1 Fix64 damage/armor/health chain are on the tested path. One test per
// behavioral branch (module creation, the F-R7-2 InitialHealth default, thorn reflection at
// an attacker, the no-reflect guards, the crush-reflect public entries, the reentrancy
// terminator), plus the Xfer contract walk (shadow-copy CRC + mid-state save/load).
//
// Behavioral reference: BFME/BFME2-ONLY module (no generals-gpl source); the reflect
// mechanic and its S1 wiring are documented in research/modules-r8/PorcupineFormationBody.md.
// The reflect weapon is delivered through DamagePipeline.DealDirectDamage back at the
// attacker - the exact public surface S1 froze - so an unarmored attacker loses exactly the
// reflect nugget's Fix64 damage.

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Body;

public class PorcupineFormationBodyContractTests
{
    private const string Definitions = @"
GameData
  UnitDamagedThreshold = 0.5
  UnitReallyDamagedThreshold = 0.1
End

Weapon PorcupineThorns
  AttackRange = 25
  DamageNugget
    Damage = 25
    Radius = 0.0
    DamageType = SLASH
    DeathType = NORMAL
  End
End

Weapon PorcupineCrush
  AttackRange = 25
  DamageNugget
    Damage = 40
    Radius = 0.0
    DamageType = CRUSH
    DeathType = NORMAL
  End
End

Weapon PorcupineBounce
  AttackRange = 25
  DamageNugget
    Damage = 10
    Radius = 0.0
    DamageType = SLASH
    DeathType = NORMAL
  End
End

Object Pikeman
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = PorcupineFormationBodyModule ModuleTag_Body
    MaxHealth = 100
    InitialHealth = 100
    DamageWeaponTemplate = PorcupineThorns
    CrushDamageWeaponTemplate = PorcupineCrush
    CrusherLevelResisted = 2
  End
End

Object PikemanNoInitial
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = PorcupineFormationBodyModule ModuleTag_Body
    MaxHealth = 100
    DamageWeaponTemplate = PorcupineThorns
    CrusherLevelResisted = 2
  End
End

Object BouncePikeman
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = PorcupineFormationBodyModule ModuleTag_Body
    MaxHealth = 100
    InitialHealth = 100
    DamageWeaponTemplate = PorcupineBounce
    CrusherLevelResisted = 2
  End
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
";

    private static HeadlessSimGame NewGame(uint seed = 0xC0FFEEu)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static GameObject Spawn(HeadlessSimGame game, string definition)
        => game.SpawnObject(definition, game.CivilianPlayer, new Vector3(0, 0, 0));

    private static PorcupineFormationBody BodyOf(GameObject gameObject)
        => Assert.IsType<PorcupineFormationBody>(gameObject.BodyModule);

    private static Fix64 Fix(int value) => new(value);

    private static CombatDamageInput Damage(
        int amount, DamageType type = DamageType.Unresistable, GameObject source = null, bool kill = false)
        => new()
        {
            SourceId = source?.Id ?? ObjectId.Invalid,
            DamageType = type,
            Amount = Fix(amount),
            Kill = kill,
        };

    // ================================================================
    // ModuleData binding + the F-R7-2 / F-HB-1 health default
    // ================================================================

    [Fact]
    public void CreateModule_ProducesPorcupineFormationBody_NotPlainActiveBody()
    {
        var game = NewGame();
        var pike = Spawn(game, "Pikeman");

        // The pre-port ModuleData had NO CreateModule override and silently built a plain
        // ActiveBody; the port fixes that.
        Assert.IsType<PorcupineFormationBody>(pike.BodyModule);
    }

    [Fact]
    public void MissingInitialHealth_DefaultsToMaxHealth_NotZero()
    {
        var game = NewGame();
        var pike = Spawn(game, "PikemanNoInitial");

        // F-R7-2 / F-HB-1: the shadowing Parse must re-apply ApplyHealthDefaults, or a BFME+
        // body with only MaxHealth spawns at 0 HP (effectively dead).
        Assert.Equal(Fix(100), BodyOf(pike).DamageCore.CurrentHealth);
        Assert.False(pike.IsEffectivelyDead);
    }

    // ================================================================
    // Thorn reflection at the attacker (the S1 DealDirectDamage path)
    // ================================================================

    [Fact]
    public void AttackFromValidSource_ReflectsThornWeaponAtAttacker()
    {
        var game = NewGame();
        var pike = Spawn(game, "Pikeman");
        var attacker = Spawn(game, "Attacker");
        var attackerBody = Assert.IsType<ActiveBody>(attacker.BodyModule, exactMatch: false);

        // The attacker strikes the pikeman for 20; the pikes reflect 25 SLASH back.
        pike.AttemptCombatDamage(Damage(20, DamageType.Slash, source: attacker));

        Assert.Equal(Fix(80), BodyOf(pike).DamageCore.CurrentHealth);   // took the incoming hit
        Assert.Equal(Fix(75), attackerBody.DamageCore.CurrentHealth);   // and pricked the attacker
    }

    [Fact]
    public void SelfSourcedDamage_DoesNotReflect()
    {
        var game = NewGame();
        var pike = Spawn(game, "Pikeman");

        // Damage whose source is the pikeman itself must not provoke a reflection (there is
        // no foreign attacker to prick). Only the direct hit lands.
        pike.AttemptCombatDamage(Damage(20, DamageType.Slash, source: pike));

        Assert.Equal(Fix(80), BodyOf(pike).DamageCore.CurrentHealth);
    }

    [Fact]
    public void SourcelessDamage_DoesNotReflect_AndDoesNotThrow()
    {
        var game = NewGame();
        var pike = Spawn(game, "Pikeman");

        // Environmental damage (invalid source) has nobody to reflect at.
        pike.AttemptCombatDamage(Damage(20, DamageType.Slash));

        Assert.Equal(Fix(80), BodyOf(pike).DamageCore.CurrentHealth);
    }

    [Fact]
    public void Healing_DoesNotReflect()
    {
        var game = NewGame();
        var pike = Spawn(game, "Pikeman");
        var healer = Spawn(game, "Attacker");
        var healerBody = Assert.IsType<ActiveBody>(healer.BodyModule, exactMatch: false);

        // First drop the pikeman so a heal has room to work, then heal it from the "healer".
        pike.AttemptCombatDamage(Damage(50, DamageType.Slash));   // sourceless: no reflect, health 50

        pike.AttemptCombatDamage(Damage(30, DamageType.Healing, source: healer));

        // Healing is not an attack: the healer is never pricked, and the pikeman is healed.
        Assert.Equal(Fix(100), healerBody.DamageCore.CurrentHealth);
        Assert.Equal(Fix(80), BodyOf(pike).DamageCore.CurrentHealth);
    }

    // ================================================================
    // Crush-resist public entries (F-PFB-1: not yet wired to a live seam)
    // ================================================================

    [Fact]
    public void ResistsCrusherLevel_TrueAtOrBelowResisted_FalseAbove()
    {
        var game = NewGame();
        var pike = Spawn(game, "Pikeman");   // CrusherLevelResisted = 2
        var body = BodyOf(pike);

        Assert.True(body.ResistsCrusherLevel(1));    // infantry
        Assert.True(body.ResistsCrusherLevel(2));    // trees
        Assert.False(body.ResistsCrusherLevel(3));   // vehicles crush through
    }

    [Fact]
    public void ReflectCrushAttempt_FiresCrushWeaponAtCrusher()
    {
        var game = NewGame();
        var pike = Spawn(game, "Pikeman");
        var crusher = Spawn(game, "Attacker");
        var crusherBody = Assert.IsType<ActiveBody>(crusher.BodyModule, exactMatch: false);

        BodyOf(pike).ReflectCrushAttempt(crusher);

        // The crush weapon (40 CRUSH) guts the would-be crusher; the pikeman is untouched.
        Assert.Equal(Fix(60), crusherBody.DamageCore.CurrentHealth);
        Assert.Equal(Fix(100), BodyOf(pike).DamageCore.CurrentHealth);
    }

    [Fact]
    public void ReflectCrushAttempt_NullCrusher_IsNoOp()
    {
        var game = NewGame();
        var pike = Spawn(game, "Pikeman");

        BodyOf(pike).ReflectCrushAttempt(null);   // no crash, no effect

        Assert.Equal(Fix(100), BodyOf(pike).DamageCore.CurrentHealth);
    }

    // ================================================================
    // Reentrancy terminator (two facing porcupines)
    // ================================================================

    [Fact]
    public void TwoFacingPorcupines_ReflectExactlyOnce_AndTerminate()
    {
        var game = NewGame();
        var a = Spawn(game, "BouncePikeman");
        var b = Spawn(game, "BouncePikeman");

        // b strikes a for 10. a reflects 10 at b; b (not yet reflecting) reflects 10 back at
        // a; a IS reflecting, so it takes the hit but does not bounce again -> the chain
        // terminates at depth two. Net: a lost 10 (incoming) + 10 (b's reflect) = 80;
        // b lost 10 (a's reflect) = 90. No stack overflow.
        a.AttemptCombatDamage(Damage(10, DamageType.Slash, source: b));

        Assert.Equal(Fix(80), BodyOf(a).DamageCore.CurrentHealth);
        Assert.Equal(Fix(90), BodyOf(b).DamageCore.CurrentHealth);
    }

    // ================================================================
    // Xfer contract walk (version wrapper + base; no own sim state)
    // ================================================================

    [Fact]
    public void HasSimXfer_And_ShadowCopyCrcMatches_MidBehavior()
    {
        var game = NewGame();
        var live = Spawn(game, "Pikeman");
        var shadow = Spawn(game, "Pikeman");

        Assert.True(BodyOf(live).HasSimXfer);

        // Drive the live body mid-behavior; the shadow starts differently-stated.
        var attacker = Spawn(game, "Attacker");
        live.AttemptCombatDamage(Damage(35, DamageType.Slash, source: attacker));
        shadow.AttemptCombatDamage(Damage(10, DamageType.Slash));

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(BodyOf(live), BodyOf(shadow));
    }

    [Fact]
    public void SaveLoad_ContinuationMatches_MidBehavior()
    {
        var game = NewGame();
        var live = Spawn(game, "Pikeman");
        live.AttemptCombatDamage(Damage(35, DamageType.Slash));   // sourceless: health 65

        var state = PortedModuleTestKit.Save(BodyOf(live));
        var restoredHost = Spawn(game, "Pikeman");
        PortedModuleTestKit.Load(BodyOf(restoredHost), state);

        // Both take the same follow-up hit; restored body continues identically.
        live.AttemptCombatDamage(Damage(20, DamageType.Slash));
        restoredHost.AttemptCombatDamage(Damage(20, DamageType.Slash));

        Assert.Equal(
            BodyOf(live).DamageCore.CurrentHealth,
            BodyOf(restoredHost).DamageCore.CurrentHealth);
        Assert.Equal(Fix(45), BodyOf(restoredHost).DamageCore.CurrentHealth);
    }
}
