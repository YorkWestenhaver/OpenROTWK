// Round-8 FreeLifeBody contract tests (Body/Damage category), on HeadlessSimGame with real
// parsed INI so the quantizing parse path is exercised (FreeLifeHealthPercent ->
// ParseFix64Percentage, FreeLifeTime -> ParseDurationLogicFrames). FreeLifeBody is
// BFME2/RotWK-only with NO GPL reference (see FreeLifeBody.cs header); these tests pin the
// task-packet semantics: one free auto-revive to FreeLifeHealthPercent of max on the killing
// blow, then timed invincibility (FreeLifeTime / FreeLifeInvincible), gated by an optional
// prerequisite upgrade. Also covers the Xfer fold of the three sim-state fields into the
// Objects CRC channel and the carried F-R7-2 InitialHealth-default fix.

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Body;

public class FreeLifeBodyContractTests
{
    private const string Definitions = @"
GameData
  UnitDamagedThreshold = 0.5
  UnitReallyDamagedThreshold = 0.1
End

Armor BaseArmor
  Armor = DEFAULT 100%
End

Upgrade Upgrade_FreeLife
  Type = OBJECT
End

Object FreeTester
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  ArmorSet
    Conditions = None
    Armor = BaseArmor
  End
  Body = FreeLifeBody ModuleTag_Body
    MaxHealth = 100
    InitialHealth = 100
    FreeLifeHealthPercent = 50
    FreeLifeTime = 600
    FreeLifeInvincible = Yes
  End
End

Object FreeNoInvince
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  ArmorSet
    Conditions = None
    Armor = BaseArmor
  End
  Body = FreeLifeBody ModuleTag_Body
    MaxHealth = 100
    InitialHealth = 100
    FreeLifeHealthPercent = 50
    FreeLifeTime = 600
    FreeLifeInvincible = No
  End
End

Object FreeGated
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  ArmorSet
    Conditions = None
    Armor = BaseArmor
  End
  Body = FreeLifeBody ModuleTag_Body
    MaxHealth = 100
    InitialHealth = 100
    FreeLifeHealthPercent = 50
    FreeLifeTime = 600
    FreeLifeInvincible = No
    FreeLifePrerequisiteUpgrade = Upgrade_FreeLife
  End
End

Object FreeMinimal
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  ArmorSet
    Conditions = None
    Armor = BaseArmor
  End
  Body = FreeLifeBody ModuleTag_Body
    MaxHealth = 100
    FreeLifeHealthPercent = 50
    FreeLifeTime = 600
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC0FFEEu)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2Rotwk, matchSeed: seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static GameObject Spawn(HeadlessSimGame game, string def = "FreeTester")
        => game.SpawnObject(def, game.CivilianPlayer, new Vector3(0, 0, 0));

    private static FreeLifeBody BodyOf(GameObject gameObject)
        => Assert.IsType<FreeLifeBody>(gameObject.BodyModule, exactMatch: false);

    private static Fix64 Fix(int value) => new(value);

    private static CombatDamageInput Damage(int amount, DamageType type = DamageType.Magic)
        => new()
        {
            SourceId = ObjectId.Invalid,
            DamageType = type,
            Amount = Fix(amount),
            Kill = false,
        };

    private static FreeLifeBodyModuleData DataOf(HeadlessSimGame game, string def)
        => Assert.IsType<FreeLifeBodyModuleData>(
            game.AssetStore.ObjectDefinitions.GetByName(def).Behaviors["ModuleTag_Body"].Data);

    // ================================================================
    // Item 1 - ModuleData audit (S5 vocabulary) + carried F-R7-2 fix
    // ================================================================

    [Fact]
    public void FreeLifeHealthPercent_ParsedAsFix64Fraction()
    {
        // ParseFix64Percentage: "50" -> 0.5 exactly (text / 100). Verified in Fix64.
        var data = DataOf(NewGame(), "FreeTester");
        Assert.Equal(Fix(1) / Fix(2), data.FreeLifeHealthPercent);
        Assert.Equal(Fix(50), Fix(100) * data.FreeLifeHealthPercent);
    }

    [Fact]
    public void FreeLifeTime_ParsedAsDurationLogicFrames()
    {
        // 600 ms at the title's 5 Hz -> ceil(600 * 5 / 1000) = 3 frames (S5 default ceil).
        var data = DataOf(NewGame(), "FreeTester");
        Assert.Equal(new LogicFrameSpan(3), data.FreeLifeTime);
    }

    [Fact]
    public void InitialHealth_DefaultsToMaxHealth_WhenOmitted()
    {
        // F-R7-2 / F-HB-1: the shadowing Parse must re-apply ActiveBody's BFME
        // InitialHealth = MaxHealth default, else a MaxHealth-only block spawns at 0 HP.
        var game = NewGame();
        var data = DataOf(game, "FreeMinimal");
        Assert.Equal(Fix(100), data.InitialHealth);

        var obj = Spawn(game, "FreeMinimal");
        Assert.Equal(Fix(100), BodyOf(obj).DamageCore.CurrentHealth);
        Assert.False(obj.IsEffectivelyDead);
    }

    // ================================================================
    // Item 4 - the free-life branch tree (packet semantics)
    // ================================================================

    [Fact]
    public void LethalHit_TriggersFreeLife_RestoresToPercentOfMax()
    {
        var game = NewGame();
        var obj = Spawn(game);

        obj.AttemptCombatDamage(Damage(500));

        Assert.False(obj.IsEffectivelyDead);
        Assert.Equal(Fix(50), BodyOf(obj).DamageCore.CurrentHealth); // 50% of MaxHealth 100
    }

    [Fact]
    public void NonLethalHit_DoesNotConsumeFreeLife()
    {
        var game = NewGame();
        var obj = Spawn(game, "FreeNoInvince");

        obj.AttemptCombatDamage(Damage(30));
        Assert.Equal(Fix(70), BodyOf(obj).DamageCore.CurrentHealth);

        // Free life is still bankable: a subsequent lethal hit resurrects rather than kills.
        obj.AttemptCombatDamage(Damage(500));
        Assert.False(obj.IsEffectivelyDead);
        Assert.Equal(Fix(50), BodyOf(obj).DamageCore.CurrentHealth);
    }

    [Fact]
    public void SecondLethalHit_AfterFreeLifeUsed_KillsNormally()
    {
        // FreeNoInvince: no invincibility window, so the second lethal hit is not blocked.
        var game = NewGame();
        var obj = Spawn(game, "FreeNoInvince");

        obj.AttemptCombatDamage(Damage(500));       // free life -> 50 HP
        Assert.False(obj.IsEffectivelyDead);

        obj.AttemptCombatDamage(Damage(500));       // one free life only -> dies
        Assert.True(obj.IsEffectivelyDead);
    }

    // ================================================================
    // Item 4 - the invincibility window (FreeLifeInvincible / FreeLifeTime)
    // ================================================================

    [Fact]
    public void DuringInvincibility_DamageIsBlocked()
    {
        var game = NewGame();
        var obj = Spawn(game);

        obj.AttemptCombatDamage(Damage(500));       // free life -> 50 HP, invincible
        obj.AttemptCombatDamage(Damage(30));        // blocked

        Assert.Equal(Fix(50), BodyOf(obj).DamageCore.CurrentHealth);
    }

    [Fact]
    public void UnresistableDamage_BypassesInvincibility_AndKills()
    {
        var game = NewGame();
        var obj = Spawn(game);

        obj.AttemptCombatDamage(Damage(500));                              // free life used, invincible
        obj.AttemptCombatDamage(Damage(500, DamageType.Unresistable));     // bypasses -> dies

        Assert.True(obj.IsEffectivelyDead);
    }

    [Fact]
    public void DuringInvincibility_HealingThroughAttemptDamage_IsNotSwallowed()
    {
        // F-INT-R8-2: the invincibility gate used to block every non-Unresistable damage type,
        // including Healing routed (mis-routed, per ActiveBody's "shouldn't happen" comment)
        // through AttemptDamage rather than called directly as AttemptHealing. That silently ate
        // heals for up to FreeLifeTime. Healing must land like Unresistable already did.
        var game = NewGame();
        var obj = Spawn(game);

        obj.AttemptCombatDamage(Damage(500));                 // free life -> 50 HP, invincible
        obj.AttemptCombatDamage(Damage(20, DamageType.Healing)); // must not be swallowed

        Assert.Equal(Fix(70), BodyOf(obj).DamageCore.CurrentHealth);
    }

    [Fact]
    public void Invincibility_HoldsUntilFreeLifeTime_ThenExpires()
    {
        var game = NewGame();
        var obj = Spawn(game);

        obj.AttemptCombatDamage(Damage(500));       // free life at frame F, invincible until F+3

        // Two frames later still inside the window: damage stays blocked.
        game.Step();
        game.Step();
        obj.AttemptCombatDamage(Damage(30));
        Assert.Equal(Fix(50), BodyOf(obj).DamageCore.CurrentHealth);

        // Third frame reaches the end frame: the window expires and damage lands again.
        game.Step();
        obj.AttemptCombatDamage(Damage(30));
        Assert.Equal(Fix(20), BodyOf(obj).DamageCore.CurrentHealth);
        Assert.False(obj.IsEffectivelyDead);
    }

    // ================================================================
    // Item 4 - prerequisite-upgrade gate
    // ================================================================

    [Fact]
    public void PrerequisiteUpgrade_Missing_NoFreeLife_Dies()
    {
        var game = NewGame();
        var obj = Spawn(game, "FreeGated");        // requires Upgrade_FreeLife, not granted

        obj.AttemptCombatDamage(Damage(500));

        Assert.True(obj.IsEffectivelyDead);
    }

    [Fact]
    public void PrerequisiteUpgrade_Present_FreeLifeTriggers()
    {
        var game = NewGame();
        var obj = Spawn(game, "FreeGated");
        obj.Upgrade(game.AssetStore.Upgrades.GetByName("Upgrade_FreeLife"));

        obj.AttemptCombatDamage(Damage(500));

        Assert.False(obj.IsEffectivelyDead);
        Assert.Equal(Fix(50), BodyOf(obj).DamageCore.CurrentHealth);
    }

    // ================================================================
    // Item 3 - Xfer: the three sim-state fields fold into the Objects CRC channel
    // ================================================================

    [Fact]
    public void FreeLifeState_ParticipatesInCrc()
    {
        var game = NewGame();
        var fresh = Spawn(game, "FreeNoInvince");
        var used = Spawn(game, "FreeNoInvince");
        used.AttemptCombatDamage(Damage(500));      // free life used, 50 HP

        // A subclass that forgot to walk _freeLifeUsed / health would fold identically here.
        Assert.NotEqual(
            PortedModuleTestKit.LiveCrc(BodyOf(fresh)),
            PortedModuleTestKit.LiveCrc(BodyOf(used)));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var live = Spawn(game);
        live.AttemptCombatDamage(Damage(500));      // free life used, invincible until F+3
        var shadow = Spawn(game);
        shadow.AttemptCombatDamage(Damage(25));     // differently-stated, still first life

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(BodyOf(live), BodyOf(shadow));
    }

    [Fact]
    public void SaveLoad_FreeLifeUsedFlag_ContinuationMatches()
    {
        var game = NewGame();
        var live = Spawn(game, "FreeNoInvince");
        live.AttemptCombatDamage(Damage(500));      // free life used, 50 HP

        var state = PortedModuleTestKit.Save(BodyOf(live));
        var restored = Spawn(game, "FreeNoInvince"); // fresh: 100 HP, free life available
        PortedModuleTestKit.Load(BodyOf(restored), state);

        // If _freeLifeUsed did not restore, the follow-up lethal hit would resurrect the
        // restored host (survive at 50) while the live one dies. Both dying is the discriminator.
        live.AttemptCombatDamage(Damage(500));
        restored.AttemptCombatDamage(Damage(500));

        Assert.Equal(
            BodyOf(live).DamageCore.CurrentHealth,
            BodyOf(restored).DamageCore.CurrentHealth);
        Assert.True(live.IsEffectivelyDead);
        Assert.True(restored.IsEffectivelyDead);
    }

    [Fact]
    public void SaveLoad_InvincibilityWindow_RestoresActiveAndExpiry()
    {
        var game = NewGame();
        var live = Spawn(game);
        live.AttemptCombatDamage(Damage(500));      // invincible until F+3

        var state = PortedModuleTestKit.Save(BodyOf(live));
        var restored = Spawn(game);                 // fresh: not invincible
        PortedModuleTestKit.Load(BodyOf(restored), state);

        // _invincibleActive restored: immediate damage on the restored host is blocked.
        restored.AttemptCombatDamage(Damage(30));
        Assert.Equal(Fix(50), BodyOf(restored).DamageCore.CurrentHealth);
    }
}
