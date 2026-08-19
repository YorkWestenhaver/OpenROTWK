// HighlanderBody port contract tests (experiment-round-4 §4.1 item 4): the immortality
// clamp exercised on HeadlessSimGame with real parsed INI, one test per branch, plus the
// shadow-copy base test and a mid-state save/load continuation.
//
// Behavioral reference (clean-room, semantics only): GPL GeneralsMD
// GameLogic/Object/Body/HighlanderBody.cpp - attemptDamage binds every non-Unresistable hit
// to min(amount, getHealth() - 1) then defers to ActiveBody::attemptDamage. The clamp is on
// the PRE-armor amount, exactly as the original.
//
// These tests are also the regression guard for the shared partial-runtime BUG: the previous
// override recursed into AttemptDamage(...) instead of base.AttemptDamage(...), so it never
// reached real health application (infinite recursion). Any assertion on post-damage health
// below would have stack-overflowed before the fix.

using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Body;

public class HighlanderBodyContractTests
{
    private const string Definitions = @"
GameData
  UnitDamagedThreshold = 0.5
  UnitReallyDamagedThreshold = 0.1
End

Armor FlameVulnerable
  Armor = DEFAULT 100%
  Armor = FLAME 200%
End

Object Highlander
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = HighlanderBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object FlammableHighlander
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  ArmorSet
    Conditions = None
    Armor = FlameVulnerable
  End
  Body = HighlanderBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC0FFEEu)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static GameObject Spawn(HeadlessSimGame game, string definition = "Highlander")
        => game.SpawnObject(definition, game.CivilianPlayer, new System.Numerics.Vector3(0, 0, 0));

    private static HighlanderBody BodyOf(GameObject gameObject)
        => Assert.IsType<HighlanderBody>(gameObject.BodyModule);

    private static Fix64 Fix(int value) => new(value);

    private static CombatDamageInput Damage(int amount, DamageType type, bool kill = false)
        => new()
        {
            SourceId = ObjectId.Invalid,
            DamageType = type,
            Amount = Fix(amount),
            Kill = kill,
        };

    // ---- the module is really on the call path ----

    [Fact]
    public void HighlanderObject_UsesHighlanderBody()
    {
        var victim = Spawn(NewGame());
        Assert.IsType<HighlanderBody>(victim.BodyModule);
    }

    // ---- branch 1: non-Unresistable lethal damage is bound to leave one hitpoint ----

    [Fact]
    public void NormalLethalDamage_CannotKill_LeavesOneHitpoint()
    {
        var game = NewGame();
        var victim = Spawn(game);

        // 250 Slash on 100 health: clamped PRE-armor to 99, 100% armor, leaves exactly 1.
        var output = victim.AttemptCombatDamage(Damage(250, DamageType.Slash));

        Assert.Equal(Fix(99), output.ActualDamageDealt);
        Assert.Equal(Fix(1), BodyOf(victim).DamageCore.CurrentHealth);
        Assert.False(victim.IsEffectivelyDead);
    }

    [Fact]
    public void ExactlyLethalNormalDamage_LeavesOneHitpoint()
    {
        var game = NewGame();
        var victim = Spawn(game);

        // amount == health: min(100, 99) = 99, boundary of the clamp.
        victim.AttemptCombatDamage(Damage(100, DamageType.Slash));

        Assert.Equal(Fix(1), BodyOf(victim).DamageCore.CurrentHealth);
        Assert.False(victim.IsEffectivelyDead);
    }

    [Fact]
    public void RepeatedNormalLethalDamage_NeverKills()
    {
        var game = NewGame();
        var victim = Spawn(game);

        for (var i = 0; i < 5; i++)
        {
            victim.AttemptCombatDamage(Damage(250, DamageType.Slash));
        }

        // Each hit re-clamps against the current (now 1) health: 1 - 0 = 1, forever.
        Assert.Equal(Fix(1), BodyOf(victim).DamageCore.CurrentHealth);
        Assert.False(victim.IsEffectivelyDead);
    }

    // ---- branch 2: sub-lethal normal damage passes through untouched ----

    [Fact]
    public void SublethalNormalDamage_AppliesNormally()
    {
        var game = NewGame();
        var victim = Spawn(game);

        var output = victim.AttemptCombatDamage(Damage(40, DamageType.Slash));

        Assert.Equal(Fix(40), output.ActualDamageDealt);
        Assert.Equal(Fix(60), BodyOf(victim).DamageCore.CurrentHealth);
    }

    // ---- branch 3: Unresistable bypasses the clamp entirely ----

    [Fact]
    public void UnresistableDamage_BypassesClamp_AndKills()
    {
        var game = NewGame();
        var victim = Spawn(game);

        victim.AttemptCombatDamage(Damage(250, DamageType.Unresistable));

        Assert.Equal(Fix(0), BodyOf(victim).DamageCore.CurrentHealth);
        Assert.True(victim.IsEffectivelyDead);
    }

    [Fact]
    public void UnresistableKillFlag_Kills()
    {
        var game = NewGame();
        var victim = Spawn(game);

        victim.AttemptCombatDamage(Damage(0, DamageType.Unresistable, kill: true));

        Assert.True(victim.IsEffectivelyDead);
    }

    // ---- faithful GPL quirk: the clamp is PRE-armor, so an amplifying armor can still
    // push the post-armor amount past the surviving hitpoint and kill a Highlander. This
    // documents that we bind the incoming amount exactly where the original does, not the
    // post-armor amount. ----

    [Fact]
    public void ArmorAmplifiedNormalDamage_CanStillKill_GplPreArmorClampQuirk()
    {
        var game = NewGame();
        var victim = Spawn(game, "FlammableHighlander");

        // 100 Flame: pre-armor clamp min(100, 99) = 99, then FLAME 200% => 198 > 1 remaining.
        victim.AttemptCombatDamage(Damage(100, DamageType.Flame));

        Assert.Equal(Fix(0), BodyOf(victim).DamageCore.CurrentHealth);
        Assert.True(victim.IsEffectivelyDead);
    }

    // ---- Xfer: shadow-copy base test (the module adds no field of its own; the whole
    // walk is the inherited ActiveBody Fix64 ledger). ----

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var live = Spawn(game);
        var shadow = Spawn(game);

        BodyOf(live).ApplyDamageScalar(Fix64.Half);
        live.AttemptCombatDamage(Damage(250, DamageType.Slash));       // clamped to 1 HP
        shadow.AttemptCombatDamage(Damage(30, DamageType.Slash));      // differently-stated

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(BodyOf(live), BodyOf(shadow));
    }

    // ---- damage-to-health application + mid-state save/load continuation ----

    [Fact]
    public void SaveLoad_ContinuationMatches()
    {
        var game = NewGame();
        var live = Spawn(game);

        // Drive the immortality clamp, then snapshot mid-behavior.
        live.AttemptCombatDamage(Damage(250, DamageType.Slash));       // health -> 1
        Assert.Equal(Fix(1), BodyOf(live).DamageCore.CurrentHealth);

        var state = PortedModuleTestKit.Save(BodyOf(live));
        var restored = Spawn(game);
        PortedModuleTestKit.Load(BodyOf(restored), state);

        // A further normal lethal hit still can't kill either instance, identically.
        live.AttemptCombatDamage(Damage(250, DamageType.Slash));
        restored.AttemptCombatDamage(Damage(250, DamageType.Slash));

        Assert.Equal(
            BodyOf(live).DamageCore.CurrentHealth,
            BodyOf(restored).DamageCore.CurrentHealth);
        Assert.Equal(Fix(1), BodyOf(restored).DamageCore.CurrentHealth);
    }
}
