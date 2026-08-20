// Mocked-game contract tests for the AttributeModifierAuraUpdate port (R12): the periodic
// scan (StartsActive/TargetEnemy/ObjectFilter/RequiredConditions/AllowSelf), ConflictsWith,
// refresh-loop consistency, the Permanent-flag reaction to a (currently test-only, see the
// module's OnTriggerRemoved doc) upgrade removal, the AotR weighted-blend composition identity,
// and the shadow-copy base test. Object definitions are parsed from INI text through the real
// parser, so the RefreshDelay/Range quantizing parse is on the tested path.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class AttributeModifierAuraUpdateContractTests
{
    // RefreshDelay 1000 ms -> 5 frames at the frozen 5 Hz (F6); Range 100.
    private const string Definitions = @"
ModifierList AuraBuff
  Category = LEADERSHIP
  Modifier = ARMOR 25%
End

ModifierList RivalBuff
  Category = LEADERSHIP
  Modifier = ARMOR 10%
End

Object AuraSource
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AttributeModifierAuraUpdate ModuleTag_Aura
    StartsActive = Yes
    BonusName = AuraBuff
    RefreshDelay = 1000
    Range = 100
    TargetEnemy = No
    ObjectFilter = NONE +INFANTRY
    ConflictsWith = RivalBuff
    AllowSelf = No
  End
End

Object EnemyAuraSource
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AttributeModifierAuraUpdate ModuleTag_Aura
    StartsActive = Yes
    BonusName = AuraBuff
    RefreshDelay = 1000
    Range = 100
    TargetEnemy = Yes
    ObjectFilter = NONE +INFANTRY
    AllowSelf = No
  End
End

Object GatedAuraSource
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AttributeModifierAuraUpdate ModuleTag_Aura
    StartsActive = Yes
    BonusName = AuraBuff
    RefreshDelay = 1000
    Range = 100
    TargetEnemy = No
    ObjectFilter = NONE +INFANTRY
    RequiredConditions = AFLAME
    AllowSelf = Yes
  End
End

Object PermanentAuraSource
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AttributeModifierAuraUpdate ModuleTag_Aura
    StartsActive = Yes
    BonusName = AuraBuff
    RefreshDelay = 1000
    Range = 100
    TargetEnemy = No
    ObjectFilter = NONE +INFANTRY
    AllowSelf = No
    Permanent = Yes
  End
End

Object TemporaryAuraSource
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AttributeModifierAuraUpdate ModuleTag_Aura
    StartsActive = Yes
    BonusName = AuraBuff
    RefreshDelay = 1000
    Range = 100
    TargetEnemy = No
    ObjectFilter = NONE +INFANTRY
    AllowSelf = No
    Permanent = No
  End
End

Object Grunt
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object Bunker
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xA33A)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static void StepFrames(HeadlessSimGame game, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            game.Step();
        }
    }

    private static AttributeModifierAuraUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<AttributeModifierAuraUpdate>().Single();

    private static void MakeEnemies(Player a, Player b)
    {
        a.AddEnemy(b);
        b.AddEnemy(a);
    }

    [Fact]
    public void StartsActive_AppliesBonusToAllyWithinRange()
    {
        var game = NewGame();
        var source = game.SpawnObject("AuraSource", game.CivilianPlayer, new Vector3(0, 0, 0));
        var ally = game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(10, 0, 0));

        StepFrames(game, 3);

        Assert.True(ally.HasAttributeModifier("AuraBuff"));
    }

    [Fact]
    public void StartsActive_DoesNotApplyOutsideRange()
    {
        var game = NewGame();
        game.SpawnObject("AuraSource", game.CivilianPlayer, new Vector3(0, 0, 0));
        var farAlly = game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(500, 0, 0));

        StepFrames(game, 3);

        Assert.False(farAlly.HasAttributeModifier("AuraBuff"));
    }

    [Fact]
    public void TargetEnemy_OnlyAppliesToEnemies_NotAllies()
    {
        var game = NewGame();
        var source = game.SpawnObject("EnemyAuraSource", game.CivilianPlayer, new Vector3(0, 0, 0));
        var enemy = game.SpawnObject("Grunt", game.PlayerManager.NeutralPlayer, new Vector3(10, 0, 0));
        var ally = game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(20, 0, 0));
        MakeEnemies(game.CivilianPlayer, game.PlayerManager.NeutralPlayer);

        StepFrames(game, 3);

        Assert.True(enemy.HasAttributeModifier("AuraBuff"));
        Assert.False(ally.HasAttributeModifier("AuraBuff"));
    }

    [Fact]
    public void ObjectFilter_ExcludesNonMatchingKindOf()
    {
        var game = NewGame();
        game.SpawnObject("AuraSource", game.CivilianPlayer, new Vector3(0, 0, 0));
        var bunker = game.SpawnObject("Bunker", game.CivilianPlayer, new Vector3(10, 0, 0));

        StepFrames(game, 3);

        Assert.False(bunker.HasAttributeModifier("AuraBuff"));
    }

    [Fact]
    public void AllowSelf_False_ExcludesTheSourceItself()
    {
        var game = NewGame();
        var source = game.SpawnObject("AuraSource", game.CivilianPlayer, new Vector3(0, 0, 0));

        StepFrames(game, 3);

        Assert.False(source.HasAttributeModifier("AuraBuff"));
    }

    [Fact]
    public void AllowSelf_True_AppliesToTheSourceItself()
    {
        var game = NewGame();
        var source = game.SpawnObject("GatedAuraSource", game.CivilianPlayer, new Vector3(0, 0, 0));
        source.SetModelConditionState(ModelConditionFlag.Aflame);

        StepFrames(game, 3);

        Assert.True(source.HasAttributeModifier("AuraBuff"));
    }

    [Fact]
    public void RequiredConditions_ExcludesCandidateMissingTheFlag()
    {
        var game = NewGame();
        game.SpawnObject("GatedAuraSource", game.CivilianPlayer, new Vector3(0, 0, 0));
        var untouched = game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(10, 0, 0));

        StepFrames(game, 3);

        Assert.False(untouched.HasAttributeModifier("AuraBuff"));
    }

    [Fact]
    public void RequiredConditions_IncludesCandidateHavingTheFlag()
    {
        var game = NewGame();
        game.SpawnObject("GatedAuraSource", game.CivilianPlayer, new Vector3(0, 0, 0));
        var aflame = game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(10, 0, 0));
        aflame.SetModelConditionState(ModelConditionFlag.Aflame);

        StepFrames(game, 3);

        Assert.True(aflame.HasAttributeModifier("AuraBuff"));
    }

    [Fact]
    public void ConflictsWith_SkipsATargetAlreadyCarryingTheConflictingModifier()
    {
        var game = NewGame();
        game.SpawnObject("AuraSource", game.CivilianPlayer, new Vector3(0, 0, 0));
        var rival = game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(10, 0, 0));
        rival.AddAttributeModifier("RivalBuff", new AttributeModifier(game.AssetStore.ModifierLists.GetByName("RivalBuff")));

        StepFrames(game, 3);

        Assert.False(rival.HasAttributeModifier("AuraBuff"));
        Assert.True(rival.HasAttributeModifier("RivalBuff"));
    }

    [Fact]
    public void RefreshLoop_RepeatedScans_KeepAConsistentTargetSet()
    {
        var game = NewGame();
        var source = game.SpawnObject("AuraSource", game.CivilianPlayer, new Vector3(0, 0, 0));
        var ally = game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(10, 0, 0));

        StepFrames(game, 3);
        Assert.True(ally.HasAttributeModifier("AuraBuff"));

        var module = ModuleOf(source);
        var grantedAfterFirstScan = module.GrantedTargets.Count;

        // Two more refresh windows (RefreshDelay = 5 frames each): an unchanged world produces
        // an unchanged granted set - no re-grant, no flicker.
        StepFrames(game, 12);

        Assert.True(ally.HasAttributeModifier("AuraBuff"));
        Assert.Equal(grantedAfterFirstScan, module.GrantedTargets.Count);
    }

    [Fact]
    public void Permanent_Yes_KeepsTheBonusAfterTheTriggerIsRemoved()
    {
        var game = NewGame();
        var source = game.SpawnObject("PermanentAuraSource", game.CivilianPlayer, new Vector3(0, 0, 0));
        var ally = game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(10, 0, 0));

        StepFrames(game, 3);
        Assert.True(ally.HasAttributeModifier("AuraBuff"));

        ModuleOf(source).OnTriggerRemoved();
        StepFrames(game, 6);

        Assert.True(ally.HasAttributeModifier("AuraBuff"));
    }

    [Fact]
    public void Permanent_No_DropsTheBonusWhenTheTriggerIsRemoved()
    {
        var game = NewGame();
        var source = game.SpawnObject("TemporaryAuraSource", game.CivilianPlayer, new Vector3(0, 0, 0));
        var ally = game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(10, 0, 0));

        StepFrames(game, 3);
        Assert.True(ally.HasAttributeModifier("AuraBuff"));

        ModuleOf(source).OnTriggerRemoved();

        Assert.False(ally.HasAttributeModifier("AuraBuff"));
        Assert.False(ModuleOf(source).IsActive);
    }

    [Theory]
    [InlineData("0", "0.5", "0.5")]
    [InlineData("0.5", "0.5", "0.75")]
    public void ComposeAuraStrength_FoldsAsScreenBlend_NotPlainSum(string accumulator, string value, string expected)
    {
        var acc = Fix64.FromDecimalLiteral(accumulator);
        var v = Fix64.FromDecimalLiteral(value);
        var expectedFix = Fix64.FromDecimalLiteral(expected);

        var result = AttributeModifierAuraUpdate.ComposeAuraStrength(acc, v);

        // Two independent "0.5" bonuses compose to "0.75", never the additive-sum "1.0".
        Assert.Equal(expectedFix, result);
    }

    [Fact]
    public void ComposeAuraStrength_ThreeStackedBonuses_IsNotTheAdditiveSum()
    {
        var half = Fix64.FromDecimalLiteral("0.5");

        var folded = AttributeModifierAuraUpdate.ComposeAuraStrength(
            AttributeModifierAuraUpdate.ComposeAuraStrength(Fix64.Zero, half), half);
        folded = AttributeModifierAuraUpdate.ComposeAuraStrength(folded, half);

        // 1 - (1-0.5)^3 = 0.875, not the additive sum 1.5.
        Assert.Equal(Fix64.FromDecimalLiteral("0.875"), folded);
        Assert.NotEqual(Fix64.FromDecimalLiteral("1.5"), folded);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var liveHost = game.SpawnObject("AuraSource", game.CivilianPlayer, new Vector3(0, 0, 0));
        game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(10, 0, 0));
        StepFrames(game, 3);
        var live = ModuleOf(liveHost);
        Assert.NotEmpty(live.GrantedTargets);

        var shadowHost = game.SpawnObject("AuraSource", game.CivilianPlayer, new Vector3(400, 400, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }
}
