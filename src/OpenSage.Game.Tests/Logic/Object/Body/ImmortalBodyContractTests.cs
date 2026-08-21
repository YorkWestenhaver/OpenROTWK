// ImmortalBody R7 contract tests (template v1.1 §5): the immortality floor, exercised on
// HeadlessSimGame with real parsed INI so the quantizing parse path and the S1 Fix64
// damage/armor/health chain are on the tested path. One test per behavioral branch, plus
// the Xfer contract walk (shadow-copy CRC + mid-state save/load continuation).
//
// Behavioral reference: generals-gpl GeneralsMD ImmortalBody.cpp (semantics only). The
// central claim under test is the task's acceptance criterion: the floor is computed in
// Fix64 on the canonical BodyDamageCore, holds AFTER armor amplification and against
// DAMAGE_KILL, and does not perturb the deterministic RNG stream on a no-op hit.

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Body;

public class ImmortalBodyContractTests
{
    private const string Definitions = @"
GameData
  UnitDamagedThreshold = 0.5
  UnitReallyDamagedThreshold = 0.1
End

Armor AmplifyArmor
  Armor = DEFAULT 100%
  Armor = FLAME 200%
End

Object ImmortalVictim
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = ImmortalBody ModuleTag_Body
    MaxHealth = 100
    InitialHealth = 100
  End
End

Object ImmortalAmplifyVictim
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  ArmorSet
    Conditions = None
    Armor = AmplifyArmor
  End
  Body = ImmortalBody ModuleTag_Body
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

    private static ImmortalBody BodyOf(GameObject gameObject)
        => Assert.IsType<ImmortalBody>(gameObject.BodyModule);

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
    // The immortality floor (GPL: won't let health drop below 1)
    // ================================================================

    [Fact]
    public void LethalDamage_FloorsHealthAtOne_AndDoesNotDie()
    {
        var game = NewGame();
        var victim = Spawn(game, "ImmortalVictim");

        var output = victim.AttemptCombatDamage(Damage(9999));

        // Exactly one hit point remains - and it is exactly Fix64.One (Fix64 floor, not a
        // float-rounded value).
        Assert.Equal(Fix64.One, BodyOf(victim).DamageCore.CurrentHealth);
        Assert.False(victim.IsEffectivelyDead);
        // Only 99 of the 9999 requested was actually dealt (clipped to leave 1 HP).
        Assert.Equal(Fix(99), output.ActualDamageDealt);
    }

    [Fact]
    public void KillFlag_CannotKillAnImmortal()
    {
        var game = NewGame();
        var victim = Spawn(game, "ImmortalVictim");

        // Kill would replace the amount with remaining health (100). The floor caps the
        // loss at 99, so a DAMAGE_KILL leaves the immortal at 1 HP - GPL survives kills.
        victim.AttemptCombatDamage(Damage(0, DamageType.Unresistable, kill: true));

        Assert.Equal(Fix64.One, BodyOf(victim).DamageCore.CurrentHealth);
        Assert.False(victim.IsEffectivelyDead);
    }

    [Fact]
    public void Floor_HoldsAfterArmorAmplification()
    {
        var game = NewGame();
        var victim = Spawn(game, "ImmortalAmplifyVictim");

        // 200 FLAME through 200% armor = 400 post-armor: a pre-armor Amount clamp (the
        // Highlander/Undead stub shape) would clamp 200 -> 99, then armor doubles it to 198
        // and kills. The post-armor seam clamps the 400 loss to 99 - the immortal survives.
        victim.AttemptCombatDamage(Damage(200, DamageType.Flame));

        Assert.Equal(Fix64.One, BodyOf(victim).DamageCore.CurrentHealth);
        Assert.False(victim.IsEffectivelyDead);
    }

    [Fact]
    public void DamageScalar_StillCannotKill()
    {
        var game = NewGame();
        var victim = Spawn(game, "ImmortalVictim");
        BodyOf(victim).ApplyDamageScalar(Fix64.Two);   // double all incoming damage

        victim.AttemptCombatDamage(Damage(9999));

        Assert.Equal(Fix64.One, BodyOf(victim).DamageCore.CurrentHealth);
        Assert.False(victim.IsEffectivelyDead);
    }

    [Fact]
    public void SubLethalDamage_AppliesNormally()
    {
        var game = NewGame();
        var victim = Spawn(game, "ImmortalVictim");

        // Below the floor it behaves exactly like ActiveBody.
        var output = victim.AttemptCombatDamage(Damage(30));

        Assert.Equal(Fix(70), BodyOf(victim).DamageCore.CurrentHealth);
        Assert.Equal(Fix(30), output.ActualDamageDealt);
        Assert.False(victim.IsEffectivelyDead);
    }

    [Fact]
    public void DamageState_ReachesReallyDamaged_ButNeverRubble()
    {
        var game = NewGame();
        var victim = Spawn(game, "ImmortalVictim");

        victim.AttemptCombatDamage(Damage(9999));

        // 1/100 = 1% is below the 10% ReallyDamaged threshold but above zero: the state is
        // ReallyDamaged, never Rubble, and the object is never effectively dead.
        Assert.Equal(BodyDamageType.ReallyDamaged, BodyOf(victim).DamageState);
        Assert.False(victim.IsEffectivelyDead);
    }

    [Fact]
    public void RepeatedLethalHits_AtOneHp_AreDeterministicNoOps()
    {
        var game = NewGame();
        var victim = Spawn(game, "ImmortalVictim");
        var random = game.GameEngine.SimContext.GameLogicRandom;

        // First lethal hit brings the immortal down to 1 HP (crossing 25% may draw the
        // fear-sound RNG once - GPL shape, sim-relevant consumption).
        victim.AttemptCombatDamage(Damage(9999));
        Assert.Equal(Fix64.One, BodyOf(victim).DamageCore.CurrentHealth);

        // A further hit on an already-1-HP immortal is a true no-op: previousHealth is
        // refreshed to current so the fear-sound predicate cannot fire a PHANTOM draw
        // (the F-IMB-1 determinism fix). RNG stream is untouched, health unchanged.
        var drawsBefore = random.DrawCount;
        victim.AttemptCombatDamage(Damage(9999));

        Assert.Equal(drawsBefore, random.DrawCount);
        Assert.Equal(Fix64.One, BodyOf(victim).DamageCore.CurrentHealth);
        Assert.False(victim.IsEffectivelyDead);
    }

    // ================================================================
    // The virtual internalChangeHealth path (external / scripted callers)
    // ================================================================

    [Fact]
    public void InternalChangeHealth_FloorsInFix64_NotFloatView()
    {
        var game = NewGame();
        var victim = Spawn(game, "ImmortalVictim");

        // A direct catastrophic health change is floored at 1, in Fix64.
        BodyOf(victim).InternalChangeHealth(-9999.0f);

        Assert.Equal(Fix64.One, BodyOf(victim).DamageCore.CurrentHealth);
        Assert.False(victim.IsEffectivelyDead);
    }

    [Fact]
    public void InternalChangeHealth_SubLethalDeltaApplies()
    {
        var game = NewGame();
        var victim = Spawn(game, "ImmortalVictim");

        BodyOf(victim).InternalChangeHealth(-40.0f);

        Assert.Equal(Fix(60), BodyOf(victim).DamageCore.CurrentHealth);
    }

    // ================================================================
    // Xfer contract walk (version wrapper + base; no own sim state)
    // ================================================================

    [Fact]
    public void HasSimXfer_And_ShadowCopyCrcMatches_MidBehavior()
    {
        var game = NewGame();
        var live = Spawn(game, "ImmortalVictim");
        var shadow = Spawn(game, "ImmortalVictim");

        Assert.True(BodyOf(live).HasSimXfer);

        // Drive the live body mid-behavior (down to its floor, scalared); the shadow starts
        // differently-stated.
        BodyOf(live).ApplyDamageScalar(Fix64.Half);
        live.AttemptCombatDamage(Damage(9999));
        shadow.AttemptCombatDamage(Damage(20));

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(BodyOf(live), BodyOf(shadow));
    }

    [Fact]
    public void SaveLoad_ContinuationMatches_MidBehavior()
    {
        var game = NewGame();
        var live = Spawn(game, "ImmortalVictim");
        live.AttemptCombatDamage(Damage(35));   // health 65

        var state = PortedModuleTestKit.Save(BodyOf(live));
        var restoredHost = Spawn(game, "ImmortalVictim");
        PortedModuleTestKit.Load(BodyOf(restoredHost), state);

        // Both take the same follow-up lethal hit and both floor identically at 1 HP.
        live.AttemptCombatDamage(Damage(9999));
        restoredHost.AttemptCombatDamage(Damage(9999));

        Assert.Equal(
            BodyOf(live).DamageCore.CurrentHealth,
            BodyOf(restoredHost).DamageCore.CurrentHealth);
        Assert.Equal(Fix64.One, BodyOf(restoredHost).DamageCore.CurrentHealth);
        Assert.False(restoredHost.IsEffectivelyDead);
    }
}
