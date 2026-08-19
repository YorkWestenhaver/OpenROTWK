// R7 UndeadBody contract tests (Body/Damage category), on HeadlessSimGame with real
// parsed INI so the quantizing parse path (ParseFix64 for SecondLifeMaxHealth) is on the
// tested path. Covers the GPL second-life branch tree, the F-UB-1 self-recursion fix
// (the first-life lethal hit reaching *base* ActiveBody rather than re-entering this
// override until stack overflow), and the Xfer fold of the _isSecondLife flag into the
// Objects CRC channel (shadow-copy CRC equality + a CRC-participation test + mid-state
// save/load continuation).

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Body;

public class UndeadBodyContractTests
{
    private const string Definitions = @"
GameData
  UnitDamagedThreshold = 0.5
  UnitReallyDamagedThreshold = 0.1
End

Armor BaseArmor
  Armor = DEFAULT 100%
End

Armor SecondArmor
  Armor = DEFAULT 100%
End

Object UndeadTester
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  ArmorSet
    Conditions = None
    Armor = BaseArmor
  End
  ArmorSet
    Conditions = SECOND_LIFE
    Armor = SecondArmor
  End
  Body = UndeadBody ModuleTag_Body
    MaxHealth = 100
    InitialHealth = 100
    SecondLifeMaxHealth = 40
  End
  Behavior = SlowDeathBehavior ModuleTag_Death
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC0FFEEu)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static GameObject Spawn(HeadlessSimGame game)
        => game.SpawnObject("UndeadTester", game.CivilianPlayer, new Vector3(0, 0, 0));

    private static UndeadBody BodyOf(GameObject gameObject)
        => Assert.IsType<UndeadBody>(gameObject.BodyModule, exactMatch: false);

    private static Fix64 Fix(int value) => new(value);

    private static CombatDamageInput Damage(int amount, DamageType type = DamageType.Magic)
        => new()
        {
            SourceId = ObjectId.Invalid,
            DamageType = type,
            Amount = Fix(amount),
            Kill = false,
        };

    // Drive a body from full into its second life with one intercepted lethal hit.
    private static GameObject SpawnInSecondLife(HeadlessSimGame game)
    {
        var obj = Spawn(game);
        obj.AttemptCombatDamage(Damage(500));
        Assert.True(BodyOf(obj).TestArmorSetFlag(ArmorSetCondition.SecondLife));
        return obj;
    }

    // ================================================================
    // ModuleData audit
    // ================================================================

    [Fact]
    public void SecondLifeMaxHealth_ParsedAsFix64_IsExact()
    {
        var game = NewGame();
        var data = Assert.IsType<UndeadBodyModuleData>(
            game.AssetStore.ObjectDefinitions.GetByName("UndeadTester")
                .Behaviors["ModuleTag_Body"].Data);

        // Health quantity audited to Q31.32 at the S5 integer text boundary.
        Assert.Equal(Fix(40), data.SecondLifeMaxHealth);
    }

    // ================================================================
    // Second-life branch tree (GPL UndeadBody::attemptDamage)
    // ================================================================

    [Fact]
    public void FirstLethalHit_ClampedToSurvive_StartsSecondLife()
    {
        // This is the F-UB-1 regression guard: before the fix the intercepted hit was
        // routed to the *overridden* AttemptDamage, which re-entered this method and
        // recursed until StackOverflow. Reaching this assertion at all proves the fix.
        var game = NewGame();
        var obj = Spawn(game);

        var output = obj.AttemptCombatDamage(Damage(500));

        // Bound to Health-1 (=99), taken, then max/initial/current reset to SecondLifeMaxHealth.
        Assert.False(obj.IsEffectivelyDead);
        Assert.Equal(Fix(40), BodyOf(obj).DamageCore.CurrentHealth); // FullyHeal to the new max
        Assert.Equal(Fix(40), BodyOf(obj).DamageCore.MaxHealth);
        Assert.True(BodyOf(obj).TestArmorSetFlag(ArmorSetCondition.SecondLife));
        // The pre-reset hit was 99 (100-1), not the full 500 and not a kill.
        Assert.Equal(Fix(99), output.ActualDamageDealt);
    }

    [Fact]
    public void NonLethalHit_DoesNotTriggerSecondLife()
    {
        var game = NewGame();
        var obj = Spawn(game);

        obj.AttemptCombatDamage(Damage(30));

        Assert.Equal(Fix(70), BodyOf(obj).DamageCore.CurrentHealth);
        Assert.Equal(Fix(100), BodyOf(obj).DamageCore.MaxHealth); // unchanged, still first life
        Assert.False(BodyOf(obj).TestArmorSetFlag(ArmorSetCondition.SecondLife));
    }

    [Fact]
    public void UnresistableLethalHit_IsNotIntercepted_AndKills()
    {
        // GPL guards the interception with `damageType != DAMAGE_UNRESISTABLE`.
        var game = NewGame();
        var obj = Spawn(game);

        obj.AttemptCombatDamage(Damage(500, DamageType.Unresistable));

        Assert.True(obj.IsEffectivelyDead);
        Assert.False(BodyOf(obj).TestArmorSetFlag(ArmorSetCondition.SecondLife));
    }

    [Fact]
    public void SecondDeath_IsHandledNormally()
    {
        var game = NewGame();
        var obj = SpawnInSecondLife(game); // now on second life at 40/40

        // A second lethal hit is no longer intercepted (m_isSecondLife = TRUE).
        obj.AttemptCombatDamage(Damage(500));

        Assert.True(obj.IsEffectivelyDead);
    }

    // ================================================================
    // Xfer: the _isSecondLife flag folds into the Objects CRC channel
    // ================================================================

    [Fact]
    public void IsSecondLife_ParticipatesInCrc()
    {
        var game = NewGame();
        var firstLife = Spawn(game);
        var secondLife = SpawnInSecondLife(game);

        // A subclass that forgot to walk the flag would fold identically here.
        Assert.NotEqual(
            PortedModuleTestKit.LiveCrc(BodyOf(firstLife)),
            PortedModuleTestKit.LiveCrc(BodyOf(secondLife)));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var live = SpawnInSecondLife(game);        // second life, 40/40
        var shadow = Spawn(game);
        shadow.AttemptCombatDamage(Damage(25));    // differently-stated, still first life

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(BodyOf(live), BodyOf(shadow));
    }

    [Fact]
    public void SaveLoad_SecondLifeFlagAndHealth_ContinuationMatches()
    {
        var game = NewGame();
        var live = SpawnInSecondLife(game);        // second life, 40/40

        var state = PortedModuleTestKit.Save(BodyOf(live));
        var restoredHost = Spawn(game);            // fresh: first life, 100/100
        PortedModuleTestKit.Load(BodyOf(restoredHost), state);

        // _isSecondLife must have restored through the contract Xfer: otherwise the
        // follow-up lethal hit is re-intercepted on the restored host (survives at 40)
        // while the live one dies. Both dying is the discriminator for the restored flag.
        // (The SECOND_LIFE *armor-set* flag rides only ActiveBody's legacy persist today -
        // it is not yet in the contract CRC walk on this base: S1 finding F-WDA-5, open.)
        live.AttemptCombatDamage(Damage(500));
        restoredHost.AttemptCombatDamage(Damage(500));

        Assert.Equal(
            BodyOf(live).DamageCore.CurrentHealth,
            BodyOf(restoredHost).DamageCore.CurrentHealth);
        Assert.True(live.IsEffectivelyDead);
        Assert.True(restoredHost.IsEffectivelyDead);
    }
}
