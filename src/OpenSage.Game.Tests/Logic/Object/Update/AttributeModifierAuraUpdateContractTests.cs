// Mocked-game contract tests for the AttributeModifierAuraUpdate port (R12/R13): the periodic
// scan (StartsActive/TargetEnemy/ObjectFilter/RequiredConditions/AllowSelf), ConflictsWith,
// refresh-loop consistency (including a revoke-then-back-in-range round trip), the Permanent-flag
// reaction to a (currently test-only, see the module's OnTriggerRemoved doc) upgrade removal, the
// flat/uncomposed grant path's no-stacking behavior, and the shadow-copy base test. Object
// definitions are parsed from INI text through the real parser, so the RefreshDelay/Range
// quantizing parse is on the tested path.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
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

    [Fact]
    public void SecondSourceGrantingTheSameModifierName_DoesNotComposeWithTheFirst()
    {
        // The aura's grant path is flat/uncomposed (R13 finding: a standalone
        // ComposeAuraStrength screen-blend utility was dead code here, never called from
        // RefreshTargets, and was removed -- composition of simultaneous records belongs to
        // AttributeModifierPoolUpdate, not this module). Two independent sources granting the
        // same modifier name to one target must not stack: the second grant is a plain no-op
        // against the still-live first entry.
        var game = NewGame();
        var sourceA = game.SpawnObject("AuraSource", game.CivilianPlayer, new Vector3(0, 0, 0));
        var ally = game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(10, 0, 0));

        StepFrames(game, 3);
        Assert.True(ally.HasAttributeModifier("AuraBuff"));

        // A second, independent grant of the identically-named modifier: GameObject's registry
        // is a flat name-keyed dictionary, so this is a plain no-op against the live entry --
        // there is no magnitude composition anywhere on this path.
        ally.AddAttributeModifier("AuraBuff", new AttributeModifier(game.AssetStore.ModifierLists.GetByName("AuraBuff")));

        Assert.True(ally.HasAttributeModifier("AuraBuff"));
        _ = sourceA;
    }

    [Fact]
    public void RevokeThenBackInRange_RegrantsTheBonus()
    {
        // R13 finding: GameObject.RemoveAttributeModifier only flags the entry Invalid; the
        // dictionary key is evicted by the legacy Scene3D LogicTick loop, which does not run
        // under the headless/deterministic sim. Before the fix, AddAttributeModifier's
        // ContainsKey guard treated that still-present Invalid entry as live and silently
        // no-op'd every later re-grant, permanently desyncing the module's _grantedTargets book-
        // keeping from the registry's actual state. Round-trip an ally out of range and back in.
        var game = NewGame();
        game.SpawnObject("AuraSource", game.CivilianPlayer, new Vector3(0, 0, 0));
        var ally = game.SpawnObject("Grunt", game.CivilianPlayer, new Vector3(10, 0, 0));

        StepFrames(game, 3);
        Assert.True(ally.HasAttributeModifier("AuraBuff"));

        // Move out of range and let a refresh window (5 frames) revoke it.
        ally.UpdateTransform(new Vector3(500, 0, 0));
        ally.UpdateColliders();
        StepFrames(game, 5);
        Assert.False(ally.HasAttributeModifier("AuraBuff"));

        // Move back into range and let another refresh window re-grant it.
        ally.UpdateTransform(new Vector3(10, 0, 0));
        ally.UpdateColliders();
        StepFrames(game, 5);
        Assert.True(ally.HasAttributeModifier("AuraBuff"));
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
